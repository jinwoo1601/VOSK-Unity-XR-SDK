// ============================================================================
// Purpose:  PlayMode tests for the model cache's archive-hash freshness stamp
// Layer:    Tests.Runtime
// Owns:     ModelExtractorCacheTests (public class)
// Depends:  ModelExtractor, VoxrBridgeErrorCode
// ============================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VoXR.Tests.Runtime
{
    // Issue #145: the cache used to key on the archive's leaf file name alone, so a
    // re-tuned model shipped under the same name was silently ignored on any device
    // that had already run the app. These tests pin the stamped-hash contract —
    // changed bytes re-extract, unchanged bytes do not.
    public class ModelExtractorCacheTests
    {
        const string ModelName = "testmodel";

        string _baseDir;
        List<string> _errors;

        [SetUp]
        public void SetUp()
        {
            _baseDir = Path.Combine(
                Application.temporaryCachePath,
                "VoxrTestCache_" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(_baseDir);
            _errors = new List<string>();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_baseDir))
                Directory.Delete(_baseDir, true);
        }

        [UnityTest]
        public IEnumerator ChangedArchive_ReExtractsAndUpdatesStamp()
        {
            var first = Extract(BuildModelArchive("--beam=13"));
            while (!first.IsCompleted)
                yield return null;

            string path = first.Result;
            Assert.IsNotNull(path, $"First extraction must succeed. Errors: [{ErrorSummary}]");
            StringAssert.Contains(
                "--beam=13",
                ReadModelConf(path),
                "The first archive's decoder config must land in the extracted model."
            );
            string firstStamp = ReadStamp(path);

            var second = Extract(BuildModelArchive("--beam=11"));
            while (!second.IsCompleted)
                yield return null;

            Assert.IsNotNull(
                second.Result,
                $"Re-extraction must succeed. Errors: [{ErrorSummary}]"
            );
            StringAssert.Contains(
                "--beam=11",
                ReadModelConf(second.Result),
                "A changed archive must re-extract — the retuned model.conf is the whole "
                    + "point of issue #145."
            );
            Assert.AreNotEqual(
                firstStamp,
                ReadStamp(second.Result),
                "The stamp must track the new archive's hash, or the next launch would "
                    + "re-extract all over again."
            );
        }

        [UnityTest]
        public IEnumerator UnchangedArchive_KeepsExistingExtraction()
        {
            // The same byte array both times: identical bytes must hash identically.
            byte[] archive = BuildModelArchive("--beam=13");

            var first = Extract(archive);
            while (!first.IsCompleted)
                yield return null;

            string path = first.Result;
            Assert.IsNotNull(path, $"First extraction must succeed. Errors: [{ErrorSummary}]");
            string firstStamp = ReadStamp(path);

            // A re-extraction deletes the directory, so a sentinel inside it is the
            // reliable witness — far more so than a directory timestamp.
            string sentinel = Path.Combine(path, "marker.txt");
            File.WriteAllText(sentinel, "sentinel");

            var second = Extract(archive);
            while (!second.IsCompleted)
                yield return null;

            Assert.AreEqual(path, second.Result, "The cached path must be returned again.");
            Assert.IsTrue(
                File.Exists(sentinel),
                "An unchanged archive must not re-extract — the sentinel proves the "
                    + "cached directory survived untouched."
            );
            Assert.AreEqual(firstStamp, ReadStamp(path), "An untouched cache keeps its stamp.");
            Assert.IsEmpty(_errors, $"A cache hit must raise no error: [{ErrorSummary}]");
        }

        [UnityTest]
        public IEnumerator MissingStamp_ReExtractsExactlyOnce()
        {
            byte[] archive = BuildModelArchive("--beam=13");

            var first = Extract(archive);
            while (!first.IsCompleted)
                yield return null;

            string path = first.Result;
            Assert.IsNotNull(path, $"First extraction must succeed. Errors: [{ErrorSummary}]");

            // Simulate an install extracted before stamping existed.
            File.Delete(Path.Combine(path, ModelExtractor.StampFileName));
            string sentinel = Path.Combine(path, "marker.txt");
            File.WriteAllText(sentinel, "sentinel");

            var upgrade = Extract(archive);
            while (!upgrade.IsCompleted)
                yield return null;

            Assert.IsNotNull(
                upgrade.Result,
                $"The upgrade extraction must succeed. Errors: [{ErrorSummary}]"
            );
            Assert.IsFalse(
                File.Exists(sentinel),
                "A missing stamp must read as a mismatch and re-extract the model."
            );
            Assert.IsEmpty(_errors, $"The upgrade path must raise no error: [{ErrorSummary}]");
            Assert.IsTrue(
                File.Exists(Path.Combine(upgrade.Result, ModelExtractor.StampFileName)),
                "The re-extraction must leave a stamp behind."
            );

            // Second sentinel: the upgrade must cost one extraction, not one per launch.
            File.WriteAllText(sentinel, "sentinel");

            var steadyState = Extract(archive);
            while (!steadyState.IsCompleted)
                yield return null;

            Assert.IsTrue(
                File.Exists(sentinel),
                "Once stamped, the same archive must stop re-extracting."
            );
        }

        [UnityTest]
        public IEnumerator MissingArchive_WithValidCache_ReturnsCacheWithoutError()
        {
            var first = Extract(BuildModelArchive("--beam=13"));
            while (!first.IsCompleted)
                yield return null;

            string path = first.Result;
            Assert.IsNotNull(path, $"First extraction must succeed. Errors: [{ErrorSummary}]");

            var withoutArchive = Extract(null);
            while (!withoutArchive.IsCompleted)
                yield return null;

            Assert.AreEqual(
                path,
                withoutArchive.Result,
                "An unreadable archive must not cost the caller a valid cache — freshness "
                    + "is merely unverifiable."
            );
            Assert.IsEmpty(
                _errors,
                $"Serving the cache must stay silent, as it was before stamping: [{ErrorSummary}]"
            );
        }

        [UnityTest]
        public IEnumerator MissingArchive_WithoutCache_RaisesModelLoadFailed()
        {
            var task = Extract(null);
            while (!task.IsCompleted)
                yield return null;

            Assert.IsNull(task.Result, "A missing archive with no cache cannot yield a model.");
            Assert.AreEqual(1, _errors.Count, $"Expected exactly one failure: [{ErrorSummary}]");
            StringAssert.Contains(
                VoxrBridgeErrorCode.ModelLoadFailed.ToString(),
                _errors[0],
                "The failure code callers key on must be unchanged."
            );
        }

        [UnityTest]
        public IEnumerator CorruptArchive_FailsValidationAndLeavesNoCache()
        {
            var task = Extract(BuildModelArchive(null));
            while (!task.IsCompleted)
                yield return null;

            Assert.IsNull(task.Result, "An archive missing conf/model.conf must not validate.");
            Assert.AreEqual(1, _errors.Count, $"Expected exactly one failure: [{ErrorSummary}]");
            StringAssert.Contains(
                VoxrBridgeErrorCode.ModelLoadFailed.ToString(),
                _errors[0],
                "A corrupt archive must surface ModelLoadFailed."
            );
            Assert.IsFalse(
                Directory.Exists(Path.Combine(_baseDir, ModelName)),
                "A failed extraction must not publish a model directory."
            );
            Assert.IsFalse(
                Directory.Exists(Path.Combine(_baseDir, ".tmp_" + ModelName)),
                "A failed extraction must not leave its temp directory behind."
            );
        }

        [UnityTest]
        public IEnumerator CorruptArchive_WithValidCache_KeepsExistingModel()
        {
            var first = Extract(BuildModelArchive("--beam=13"));
            while (!first.IsCompleted)
                yield return null;

            string path = first.Result;
            Assert.IsNotNull(path, $"First extraction must succeed. Errors: [{ErrorSummary}]");
            string firstStamp = ReadStamp(path);

            // Different bytes, so the stamp mismatches and a re-extraction is attempted; no
            // conf/model.conf, so that replacement then fails structural validation.
            var corrupt = Extract(BuildModelArchive(null));
            while (!corrupt.IsCompleted)
                yield return null;

            Assert.IsNull(corrupt.Result, "A corrupt replacement archive cannot yield a model.");
            Assert.AreEqual(1, _errors.Count, $"Expected exactly one failure: [{ErrorSummary}]");
            StringAssert.Contains(
                VoxrBridgeErrorCode.ModelLoadFailed.ToString(),
                _errors[0],
                "A corrupt archive must surface ModelLoadFailed."
            );
            Assert.IsTrue(
                Directory.Exists(path),
                "The working model must survive a corrupt replacement — deleting the cache "
                    + "before the replacement exists leaves the device with no model at all."
            );
            Assert.IsTrue(
                ModelExtractor.ValidateModelDirectory(path),
                "The surviving cache must still be structurally valid, not a half-emptied shell."
            );
            StringAssert.Contains(
                "--beam=13",
                ReadModelConf(path),
                "The surviving cache must still hold the original model's contents."
            );
            Assert.AreEqual(
                firstStamp,
                ReadStamp(path),
                "The surviving cache keeps its own stamp, so a later good archive still "
                    + "reads as a mismatch and re-extracts."
            );
            Assert.IsFalse(
                Directory.Exists(Path.Combine(_baseDir, ".tmp_" + ModelName)),
                "A failed extraction must not leave its temp directory behind."
            );
        }

        [UnityTest]
        public IEnumerator ThrowingArchiveSource_WithValidCache_ReturnsCacheWithoutError()
        {
            var first = Extract(BuildModelArchive("--beam=13"));
            while (!first.IsCompleted)
                yield return null;

            string path = first.Result;
            Assert.IsNotNull(path, $"First extraction must succeed. Errors: [{ErrorSummary}]");

            // Throws at the call site rather than returning a faulted task, which pins that
            // the invocation itself — not just the await — sits inside the fallback's guard.
            var throwing = ExtractFrom(() => throw new IOException("locked"));
            while (!throwing.IsCompleted)
                yield return null;

            Assert.AreEqual(
                path,
                throwing.Result,
                "A read that throws must degrade to the same fallback as one returning null: "
                    + "a locked or permission-denied archive leaves freshness merely "
                    + "unverifiable, not the cache unusable."
            );
            Assert.IsEmpty(
                _errors,
                $"Serving the cache must stay silent, as the null path does: [{ErrorSummary}]"
            );
        }

        [UnityTest]
        public IEnumerator ThrowingArchiveSource_WithoutCache_RaisesModelLoadFailed()
        {
            // The faulted-task shape, matching production's async ReadStreamingAsset.
            var task = ExtractFrom(() => Task.FromException<byte[]>(new IOException("locked")));
            while (!task.IsCompleted)
                yield return null;

            Assert.IsNull(task.Result, "An unreadable archive with no cache cannot yield a model.");
            Assert.AreEqual(1, _errors.Count, $"Expected exactly one failure: [{ErrorSummary}]");
            StringAssert.Contains(
                VoxrBridgeErrorCode.ModelLoadFailed.ToString(),
                _errors[0],
                "The failure code callers key on must be unchanged."
            );
            StringAssert.Contains(
                "could not be read",
                _errors[0],
                "An unreadable archive must report as unreadable — a file that is present "
                    + "but locked is a different problem from one that was never shipped."
            );
            StringAssert.DoesNotContain(
                "not found in StreamingAssets",
                _errors[0],
                "The missing-archive wording stays pinned to the genuinely-missing case."
            );
        }

        [UnityTest]
        public IEnumerator MissingArchive_WithInvalidCache_SweepsCacheAndTemp()
        {
            // An unusable cache (am/ alone fails validation) plus a temp directory left
            // behind by an interrupted extraction — both dead weight on device storage.
            string cachePath = Path.Combine(_baseDir, ModelName);
            Directory.CreateDirectory(Path.Combine(cachePath, "am"));

            string tempPath = Path.Combine(_baseDir, ".tmp_" + ModelName);
            Directory.CreateDirectory(tempPath);
            File.WriteAllText(Path.Combine(tempPath, "partial.bin"), "partial");

            var task = Extract(null);
            while (!task.IsCompleted)
                yield return null;

            Assert.IsNull(
                task.Result,
                "A missing archive with no valid cache cannot yield a model."
            );
            Assert.AreEqual(1, _errors.Count, $"Expected exactly one failure: [{ErrorSummary}]");
            StringAssert.Contains(
                VoxrBridgeErrorCode.ModelLoadFailed.ToString(),
                _errors[0],
                "The failure code callers key on must be unchanged."
            );
            Assert.IsFalse(
                Directory.Exists(cachePath),
                "A cache that cannot validate is worthless and must be swept, as the "
                    + "pre-stamp code did on this path."
            );
            Assert.IsFalse(
                Directory.Exists(tempPath),
                "A stale temp must be swept too — otherwise a partial unpack sits on device "
                    + "storage indefinitely."
            );
        }

        Task<string> Extract(byte[] archiveBytes) =>
            ExtractFrom(() => Task.FromResult(archiveBytes));

        // The seam taken with an arbitrary source, so a throwing read can be injected.
        Task<string> ExtractFrom(Func<Task<byte[]>> archiveSource) =>
            ModelExtractor.ExtractModelAsync(
                ModelName,
                _baseDir,
                archiveSource,
                ModelName + ".zip",
                (code, msg) => _errors.Add($"{code}: {msg}")
            );

        string ErrorSummary => string.Join("; ", _errors);

        static string ReadModelConf(string modelPath) =>
            File.ReadAllText(Path.Combine(modelPath, "conf", "model.conf"));

        static string ReadStamp(string modelPath) =>
            File.ReadAllText(Path.Combine(modelPath, ModelExtractor.StampFileName));

        // Builds a VOSK-shaped archive in memory. The entries sit under a root folder
        // because real VOSK archives have one and the extractor strips the first path
        // segment; graph/ needs a file inside it because directory entries are skipped.
        // A null modelConfContents omits conf/model.conf, producing a corrupt archive.
        static byte[] BuildModelArchive(string modelConfContents)
        {
            using var stream = new MemoryStream();

            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(archive, "model-root/am/final.mdl", "stub");
                WriteEntry(archive, "model-root/conf/mfcc.conf", "stub");
                if (modelConfContents != null)
                    WriteEntry(archive, "model-root/conf/model.conf", modelConfContents);
                WriteEntry(archive, "model-root/graph/HCLG.fst", "stub");
            }

            return stream.ToArray();
        }

        static void WriteEntry(ZipArchive archive, string entryName, string contents)
        {
            var entry = archive.CreateEntry(entryName);
            // Pinned so two archives with the same contents hash the same: the entry
            // timestamp would otherwise default to "now" and vary between builds.
            entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

            using var writer = new StreamWriter(entry.Open());
            writer.Write(contents);
        }
    }
}
