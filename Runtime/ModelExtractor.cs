// ============================================================================
// Purpose:  Extracts ZIP-compressed VOSK models from StreamingAssets to persistent storage
// Layer:    Runtime
// Owns:     ModelExtractor (internal static class)
// Depends:  VoxrBridgeErrorCode
// ============================================================================
using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace VoXR
{
    internal static class ModelExtractor
    {
        const string ModelCacheFolder = "VoxrModels";

        internal static async Task<string> ExtractModelAsync(
            string modelRelativePath,
            Action<VoxrBridgeErrorCode, string> onError)
        {
            string modelName = Path.GetFileName(modelRelativePath);
            string basePath = Path.Combine(Application.persistentDataPath, ModelCacheFolder);
            string finalPath = Path.Combine(basePath, modelName);
            string tempPath = Path.Combine(basePath, $".tmp_{modelName}");

            try
            {
                Directory.CreateDirectory(basePath);

                if (Directory.Exists(finalPath))
                {
                    if (ValidateModelDirectory(finalPath))
                        return finalPath;

                    Directory.Delete(finalPath, true);
                }

                // Clean up stale temp from interrupted extraction
                if (Directory.Exists(tempPath))
                    Directory.Delete(tempPath, true);

                byte[] archiveBytes = await ReadStreamingAsset(modelRelativePath + ".zip");
                if (archiveBytes == null)
                {
                    onError?.Invoke(VoxrBridgeErrorCode.ModelLoadFailed,
                        $"Model archive not found in StreamingAssets: {modelRelativePath}.zip");
                    return null;
                }

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
            bool hasGraph = Directory.Exists(Path.Combine(path, "graph"));

            return hasModel && hasConf && hasGraph;
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
