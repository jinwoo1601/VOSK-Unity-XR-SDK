// ============================================================================
// Purpose:  Extracts ZIP-compressed VOSK models from StreamingAssets to persistent
//           storage, re-extracting whenever the archive's contents change
// Layer:    Runtime
// Owns:     ModelExtractor (internal static class)
// Depends:  VoxrBridgeErrorCode
// ============================================================================
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace VoXR
{
    internal static class ModelExtractor
    {
        const string ModelCacheFolder = "VoxrModels";
        internal const string StampFileName = ".voxr-model-stamp";

        internal static Task<string> ExtractModelAsync(
            string modelRelativePath,
            Action<VoxrBridgeErrorCode, string> onError)
        {
            string archiveRelativePath = modelRelativePath + ".zip";

            return ExtractModelAsync(
                Path.GetFileName(modelRelativePath),
                Path.Combine(Application.persistentDataPath, ModelCacheFolder),
                () => ReadStreamingAsset(archiveRelativePath),
                archiveRelativePath,
                onError);
        }

        internal static async Task<string> ExtractModelAsync(
            string modelName,
            string basePath,
            Func<Task<byte[]>> archiveSource,
            string archiveDescription,
            Action<VoxrBridgeErrorCode, string> onError)
        {
            string finalPath = Path.Combine(basePath, modelName);
            string tempPath = Path.Combine(basePath, $".tmp_{modelName}");

            try
            {
                Directory.CreateDirectory(basePath);

                byte[] archiveBytes = null;
                string readFailure = null;

                try
                {
                    archiveBytes = await archiveSource();
                }
                catch (Exception ex)
                {
                    // A read that throws is no more fatal than one that returns null: both
                    // leave the cache's freshness unverifiable, and a valid cache is still a
                    // usable model. Degrade to the same fallback instead of failing outright.
                    readFailure = ex.Message;
                }

                if (archiveBytes == null)
                {
                    // Without the archive the cache's freshness cannot be checked, but a
                    // valid cache is still a usable model — and before stamping existed
                    // an unreadable archive was harmless whenever the cache was valid.
                    // Keep serving it silently rather than regressing into an error.
                    if (Directory.Exists(finalPath) && ValidateModelDirectory(finalPath))
                        return finalPath;

                    // No replacement is coming down this path, so restore the disk hygiene
                    // the pre-stamp code performed here: the cache has necessarily failed
                    // validation and is worthless, and a stale temp is pure dead weight.
                    if (Directory.Exists(finalPath))
                        Directory.Delete(finalPath, true);

                    if (Directory.Exists(tempPath))
                        Directory.Delete(tempPath, true);

                    string failureMessage = readFailure != null
                        ? $"Model archive could not be read: {archiveDescription} ({readFailure})"
                        : $"Model archive not found in StreamingAssets: {archiveDescription}";

                    onError?.Invoke(VoxrBridgeErrorCode.ModelLoadFailed, failureMessage);
                    return null;
                }

                string sourceKey = await Task.Run(() => ComputeArchiveKey(archiveBytes));

                // A missing stamp reads as a mismatch, so a pre-stamp install re-extracts
                // exactly once and is stamped from then on. The stale cache is deliberately
                // NOT deleted here: it stays as the working model until its replacement is
                // extracted, validated and stamped, so a corrupt archive costs an error
                // rather than the model the device already had.
                if (Directory.Exists(finalPath)
                    && ValidateModelDirectory(finalPath)
                    && ReadStamp(finalPath) == sourceKey)
                    return finalPath;

                // Clean up stale temp from interrupted extraction
                if (Directory.Exists(tempPath))
                    Directory.Delete(tempPath, true);

                await Task.Run(() =>
                {
                    using var stream = new MemoryStream(archiveBytes);
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

                    foreach (var entry in archive.Entries)
                    {
                        // Skip directory entries
                        if (string.IsNullOrEmpty(entry.Name))
                            continue;

                        // Strip the top-level folder from the archive if present.
                        // VOSK archives contain a root folder (e.g., "vosk-model-small-en-us-0.15/").
                        // We want the contents directly under tempPath.
                        string entryPath = entry.FullName;
                        int separatorIndex = entryPath.IndexOf('/');
                        if (separatorIndex >= 0)
                            entryPath = entryPath.Substring(separatorIndex + 1);

                        if (string.IsNullOrEmpty(entryPath))
                            continue;

                        string destinationPath = Path.GetFullPath(Path.Combine(tempPath, entryPath));
                        string fullTempPath = Path.GetFullPath(tempPath) + Path.DirectorySeparatorChar;

                        if (!destinationPath.StartsWith(fullTempPath, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"Zip entry escapes target directory: {entry.FullName}");

                        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                        entry.ExtractToFile(destinationPath, overwrite: true);
                    }
                });

                if (!ValidateModelDirectory(tempPath))
                {
                    if (Directory.Exists(tempPath))
                        Directory.Delete(tempPath, true);

                    onError?.Invoke(VoxrBridgeErrorCode.ModelLoadFailed,
                        "Extracted model failed structural validation. The archive may be corrupt.");
                    return null;
                }

                // Stamp inside the temp directory so the atomic rename below is what
                // publishes it: a stamped cache can only ever appear complete.
                File.WriteAllText(Path.Combine(tempPath, StampFileName), sourceKey);

                // Surrender the old cache only now that its replacement is complete.
                if (Directory.Exists(finalPath))
                    Directory.Delete(finalPath, true);

                // Atomic rename
                Directory.Move(tempPath, finalPath);
                return finalPath;
            }
            catch (Exception ex)
            {
                try
                {
                    if (Directory.Exists(tempPath))
                        Directory.Delete(tempPath, true);
                }
                catch
                {
                    // Best-effort cleanup
                }

                onError?.Invoke(VoxrBridgeErrorCode.ModelLoadFailed,
                    $"Model extraction failed: {ex.Message}");
                return null;
            }
        }

        internal static bool ValidateModelDirectory(string path)
        {
            if (!Directory.Exists(path))
                return false;

            bool hasModel = File.Exists(Path.Combine(path, "am", "final.mdl"));
            bool hasConf = File.Exists(Path.Combine(path, "conf", "mfcc.conf"));
            bool hasModelConf = File.Exists(Path.Combine(path, "conf", "model.conf"));
            bool hasGraph = Directory.Exists(Path.Combine(path, "graph"));

            return hasModel && hasConf && hasModelConf && hasGraph;
        }

        // SHA-256 over the raw archive bytes: deterministic across platforms and
        // needs no dependency beyond the BCL.
        internal static string ComputeArchiveKey(byte[] archiveBytes)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(archiveBytes);

            var builder = new StringBuilder("sha256:", 7 + hash.Length * 2);
            foreach (byte b in hash)
                builder.Append(b.ToString("x2"));

            return builder.ToString();
        }

        static string ReadStamp(string modelPath)
        {
            try
            {
                string stampPath = Path.Combine(modelPath, StampFileName);
                if (!File.Exists(stampPath))
                    return null;

                return File.ReadAllText(stampPath).Trim();
            }
            catch
            {
                // An unreadable stamp must mean "re-extract", never an exception.
                return null;
            }
        }

        static async Task<byte[]> ReadStreamingAsset(string relativePath)
        {
            string fullPath = Path.Combine(Application.streamingAssetsPath, relativePath);

            if (Application.platform == RuntimePlatform.Android)
            {
                // On Android, StreamingAssets is inside the APK — must use UnityWebRequest
                using var request = UnityWebRequest.Get(fullPath);
                var operation = request.SendWebRequest();

                while (!operation.isDone)
                    await Task.Yield();

                if (request.result != UnityWebRequest.Result.Success)
                    return null;

                return request.downloadHandler.data;
            }

            // On Editor / standalone, direct file access works
            if (!File.Exists(fullPath))
                return null;

            return await Task.Run(() => File.ReadAllBytes(fullPath));
        }
    }
}
