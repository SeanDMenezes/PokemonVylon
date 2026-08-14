using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PokemonVylon.UpdateIndex;

namespace PatchBuilderGUI;

static class PatchBuildService
{
    public static async Task<PatchBuildResult> BuildAsync(
        PatchBuildRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string oldFolder = Path.GetFullPath(request.OldFolder.Trim());
        string newFolder = Path.GetFullPath(request.NewFolder.Trim());
        string fromVersion = request.FromVersion.Trim();
        string toVersion = request.ToVersion.Trim();

        string? validationError = PatchValidation.Validate(
            new PatchBuildRequest(oldFolder, newFolder, fromVersion, toVersion));

        if (validationError is not null)
        {
            throw new InvalidOperationException(validationError);
        }

        string outputFolder = PatchValidation.GetOutputFolder(newFolder, toVersion);
        string zipPath = PatchValidation.GetZipPath(newFolder, fromVersion, toVersion);
        string updateIndexPath = PatchValidation.GetUpdateIndexPath(newFolder);

        if (Directory.Exists(outputFolder))
        {
            progress?.Report("Removing previous patch folder...");
            Directory.Delete(outputFolder, true);
        }

        Directory.CreateDirectory(outputFolder);

        progress?.Report("Scanning old and new builds...");
        progress?.Report($"Old: {oldFolder}");
        progress?.Report($"New: {newFolder}");

        var oldFiles = GetFiles(oldFolder);
        var newFiles = GetFiles(newFolder);

        cancellationToken.ThrowIfCancellationRequested();

        var changedFiles = new ConcurrentBag<string>();
        var commonRelativePaths = new HashSet<string>(
            newFiles.Keys,
            StringComparer.OrdinalIgnoreCase);

        commonRelativePaths.IntersectWith(oldFiles.Keys);

        int totalCommonFiles = commonRelativePaths.Count;
        int processedFiles = 0;

        progress?.Report($"Comparing {totalCommonFiles:N0} shared files...");

        await Task.Run(
            () =>
            {
                Parallel.ForEach(
                    commonRelativePaths,
                    new ParallelOptions { CancellationToken = cancellationToken },
                    relativePath =>
                    {
                        var oldEntry = oldFiles[relativePath];
                        var newEntry = newFiles[relativePath];

                        if (oldEntry.Length != newEntry.Length)
                        {
                            changedFiles.Add(relativePath);
                            return;
                        }

                        if (oldEntry.LastWriteUtc == newEntry.LastWriteUtc)
                        {
                            return;
                        }

                        string oldHash = CalculateSha256(oldEntry.FullPath);
                        string newHash = CalculateSha256(newEntry.FullPath);

                        if (!string.Equals(
                                oldHash,
                                newHash,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            changedFiles.Add(relativePath);
                        }

                        int completed = Interlocked.Increment(ref processedFiles);
                        if (completed % 1000 == 0)
                        {
                            progress?.Report(
                                $"Compared {completed:N0}/{totalCommonFiles:N0} common files");
                        }
                    });
            },
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        var deletedFiles = oldFiles.Keys
            .Where(relativePath => !newFiles.ContainsKey(relativePath))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var newFilesOnly = newFiles.Keys
            .Where(relativePath => !oldFiles.ContainsKey(relativePath))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var changedFileList = changedFiles
            .Concat(newFilesOnly)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        progress?.Report($"Changed/new files: {changedFileList.Count:N0}");
        progress?.Report($"Deleted files: {deletedFiles.Count:N0}");

        foreach (string relativePath in changedFileList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string source = Path.Combine(
                newFolder,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            string destination = Path.Combine(
                outputFolder,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(source, destination, true);
        }

        progress?.Report("Generating target build manifest...");
        var manifestBuild = await Task.Run(
            () => BuildBuildManifest(newFolder, toVersion, progress),
            cancellationToken);

        await WriteJsonAsync(
            Path.Combine(newFolder, "manifest.json"),
            manifestBuild,
            writeIndented: true,
            cancellationToken);

        progress?.Report("Writing patch.json...");

        var manifest = new PatchManifest
        {
            FromVersion = fromVersion,
            ToVersion = toVersion,
            Files = new Dictionary<string, PatchFileManifest>(StringComparer.OrdinalIgnoreCase),
            Deleted = deletedFiles
        };

        foreach (string relativePath in changedFileList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fullPath = Path.Combine(
                newFolder,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            var info = new FileInfo(fullPath);
            manifest.Files[relativePath] = new PatchFileManifest
            {
                Size = info.Length,
                Sha256 = CalculateSha256(fullPath)
            };
        }

        await WriteJsonAsync(
            Path.Combine(outputFolder, "patch.json"),
            manifest,
            writeIndented: true,
            cancellationToken);

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        progress?.Report("Creating patch zip...");
        await Task.Run(
            () => ZipFile.CreateFromDirectory(
                outputFolder,
                zipPath,
                CompressionLevel.Optimal,
                includeBaseDirectory: false),
            cancellationToken);

        string zipSha256 = CalculateSha256(zipPath);
        string assetName = Path.GetFileName(zipPath);
        string releaseTag = UpdateIndexNaming.GetDefaultReleaseTag(toVersion);

        progress?.Report("Updating update-index.json...");
        UpdateIndexManifest updateIndex = UpdateIndexBuilder.LoadOrCreate(updateIndexPath);
        UpdateIndexBuilder.UpsertEdge(
            updateIndex,
            UpdateIndexBuilder.CreateEdge(fromVersion, toVersion, assetName, releaseTag, zipSha256));
        UpdateIndexBuilder.ApplyRelease(updateIndex, fromVersion, toVersion);
        await UpdateIndexBuilder.SaveAsync(updateIndex, updateIndexPath);

        progress?.Report("Cleaning up temporary patch folder...");
        try
        {
            Directory.Delete(outputFolder, recursive: true);
        }
        catch (Exception ex)
        {
            progress?.Report($"Could not delete temp folder ({outputFolder}): {ex.Message}");
        }

        long zipSize = new FileInfo(zipPath).Length;
        progress?.Report($"Done. Patch size: {zipSize / 1024.0 / 1024.0:F2} MB");
        progress?.Report(zipPath);

        return new PatchBuildResult(
            zipPath,
            updateIndexPath,
            zipSize,
            changedFileList.Count,
            deletedFiles.Count);
    }

    static BuildManifest BuildBuildManifest(
        string folder,
        string version,
        IProgress<string>? progress)
    {
        string manifestPath = Path.Combine(folder, "manifest.json");
        if (File.Exists(manifestPath))
        {
            progress?.Report($"Found existing target build manifest: {manifestPath}");

            BuildManifest? existing = LoadBuildManifest(manifestPath, progress);
            if (existing is not null)
            {
                progress?.Report("Reusing existing target build manifest.");
                return existing;
            }
        }

        var allFiles = GetFiles(folder);
        var files = new ConcurrentDictionary<string, PatchFileManifest>(
            StringComparer.OrdinalIgnoreCase);

        Parallel.ForEach(
            allFiles,
            file =>
            {
                string relativePath = file.Key;

                if (string.Equals(
                        relativePath,
                        "manifest.json",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                files.TryAdd(
                    relativePath,
                    new PatchFileManifest
                    {
                        Size = file.Value.Length,
                        Sha256 = CalculateSha256(file.Value.FullPath)
                    });
            });

        return new BuildManifest
        {
            Version = version,
            Files = files.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase)
        };
    }

    static BuildManifest? LoadBuildManifest(string manifestPath, IProgress<string>? progress)
    {
        try
        {
            string json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<BuildManifest>(json);
        }
        catch (Exception ex)
        {
            progress?.Report($"Could not reuse existing manifest. Recomputing. ({ex.Message})");
            return null;
        }
    }

    static async Task WriteJsonAsync(
        string path,
        object payload,
        bool writeIndented,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions
            {
                WriteIndented = writeIndented,
                PropertyNamingPolicy = null
            });

        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    static Dictionary<string, FileSnapshot> GetFiles(string folder)
    {
        var results = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (string filePath in Directory.EnumerateFiles(
                     folder,
                     "*",
                     new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true
                     }))
        {
            string relativePath = Path.GetRelativePath(folder, filePath)
                .Replace('\\', '/');

            if (PatchExclusions.IsExcluded(relativePath))
            {
                continue;
            }

            var info = new FileInfo(filePath);
            results[relativePath] = new FileSnapshot(
                relativePath,
                filePath,
                info.Length,
                info.LastWriteTimeUtc);
        }

        return results;
    }

    static string CalculateSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);

        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
