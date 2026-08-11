using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PokemonVylon.UpdateIndex;

internal static class Program
{
    private const string GitHubOwner = "SeanDMenezes";
    private const string GitHubRepo = "PokemonVylon";
    private const string UpdaterStagingDirectoryName = ".updater-staging";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("Pokemon Vylon Updater - Phase 2 Update Index");

        string gameDirectory = AppContext.BaseDirectory;
        string gameExePath = Path.Combine(gameDirectory, "Game.exe");
        string installedVersionPath = Path.Combine(gameDirectory, "version.json");

        if (!File.Exists(gameExePath))
        {
            Console.Error.WriteLine($"Cannot locate Game.exe in the installation directory: {gameExePath}");
            return 1;
        }

        if (!File.Exists(installedVersionPath))
        {
            Console.Error.WriteLine($"Cannot locate installed version manifest: {installedVersionPath}");
            return 1;
        }

        if (IsGameRunning(gameExePath))
        {
            Console.Error.WriteLine("Game.exe is currently running. Close the game before applying an update.");
            return 1;
        }

        string installedVersion;
        try
        {
            installedVersion = ReadInstalledVersion(installedVersionPath);
            Console.WriteLine($"Installed version: {installedVersion}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not read installed version file: {ex.Message}");
            return 1;
        }

        GitHubRelease? latestRelease;
        try
        {
            latestRelease = await QueryLatestReleaseAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not query GitHub release metadata: {ex.Message}");
            return 1;
        }

        if (latestRelease is null)
        {
            Console.Error.WriteLine("No GitHub latest release was returned.");
            return 1;
        }

        string latestVersion = VersionComparer.Normalize(latestRelease.TagName);
        Console.WriteLine($"Latest GitHub tag: {latestRelease.TagName} ({latestVersion})");

        if (VersionComparer.Compare(latestVersion, installedVersion) <= 0)
        {
            Console.WriteLine("No update is available for the installed version.");
            return 0;
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "PokemonVylonUpdater", latestVersion);
        Directory.CreateDirectory(tempRoot);

        try
        {
            UpdateIndexManifest updateIndex = await LoadUpdateIndexAsync(latestRelease, tempRoot);
            Console.WriteLine(
                $"Loaded update index with {updateIndex.Edges.Count} edge(s). Minimum supported: {updateIndex.MinimumSupported}");

            if (!string.IsNullOrWhiteSpace(updateIndex.MinimumSupported)
                && VersionComparer.Compare(installedVersion, updateIndex.MinimumSupported) < 0)
            {
                Console.Error.WriteLine(
                    $"Installed version '{installedVersion}' is older than the minimum supported version '{updateIndex.MinimumSupported}'. Reinstall the game from scratch.");
                return 1;
            }

            IReadOnlyList<UpdateIndexEdge>? updatePath = UpdateIndexPathfinder.FindShortestPath(
                updateIndex,
                installedVersion,
                latestVersion);

            if (updatePath is null || updatePath.Count == 0)
            {
                Console.Error.WriteLine(
                    $"No update path could be resolved from installed version '{installedVersion}' to latest '{latestVersion}'.");
                return 1;
            }

            Console.WriteLine($"Resolved update path with {updatePath.Count} hop(s):");
            foreach (UpdateIndexEdge hop in updatePath)
            {
                Console.WriteLine($"  {hop.FromVersion} -> {hop.ToVersion} ({hop.AssetName})");
            }

            string currentVersion = installedVersion;
            for (int hopIndex = 0; hopIndex < updatePath.Count; hopIndex++)
            {
                UpdateIndexEdge hop = updatePath[hopIndex];
                Console.WriteLine();
                Console.WriteLine($"Applying hop {hopIndex + 1}/{updatePath.Count}: {hop.FromVersion} -> {hop.ToVersion}");

                if (!string.Equals(hop.FromVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Update path mismatch: expected to apply from '{currentVersion}', but the next hop starts at '{hop.FromVersion}'.");
                }

                currentVersion = await ApplyIndexedPatchHopAsync(
                    gameDirectory,
                    hop,
                    tempRoot,
                    hopIndex);
            }

            if (VersionComparer.Compare(currentVersion, latestVersion) < 0)
            {
                throw new InvalidOperationException(
                    $"Update path completed at '{currentVersion}', but latest release is '{latestVersion}'.");
            }

            await StageSelfUpdateBinaryAsync(latestRelease, gameDirectory);

            string gameExeFinalPath = Path.Combine(gameDirectory, "Game.exe");
            if (File.Exists(gameExeFinalPath))
            {
                Console.WriteLine($"Launching {gameExeFinalPath}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = gameExeFinalPath,
                    UseShellExecute = true
                });
            }
            else
            {
                throw new FileNotFoundException("Game.exe was missing after the update process.");
            }

            Console.WriteLine("Update completed successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Update failed: {ex.Message}");
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
            catch (Exception cleanupEx)
            {
                Console.Error.WriteLine($"Cleanup warning: {cleanupEx.Message}");
            }
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Temporary cleanup warning: {ex.Message}");
            }
        }
    }

    private static string ReadInstalledVersion(string versionJsonPath)
    {
        string json = File.ReadAllText(versionJsonPath);
        InstalledVersionManifest? manifest = JsonSerializer.Deserialize<InstalledVersionManifest>(json, JsonOptions);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidOperationException("version.json does not contain a valid version string.");
        }

        return manifest.Version;
    }

    private static void WriteInstalledVersion(string gameDirectory, string toVersion)
    {
        string versionJsonPath = Path.Combine(gameDirectory, "version.json");
        var manifest = new InstalledVersionManifest
        {
            Version = toVersion
        };

        string json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(versionJsonPath, json);
    }

    private static async Task<GitHubRelease?> QueryLatestReleaseAsync()
    {
        using HttpClient client = new();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PokemonVylonUpdater/1.0");

        string url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
        HttpResponseMessage response = await client.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            string text = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"GitHub API returned {(int)response.StatusCode}: {text}");
        }

        string payload = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitHubRelease>(payload, JsonOptions);
    }

    private static async Task<UpdateIndexManifest> LoadUpdateIndexAsync(GitHubRelease release, string tempRoot)
    {
        GitHubAsset? indexAsset = release.Assets.FirstOrDefault(asset =>
            asset.Name.Equals(UpdateIndexConstants.FileName, StringComparison.OrdinalIgnoreCase));

        if (indexAsset is null)
        {
            throw new InvalidOperationException(
                $"Latest release is missing required asset '{UpdateIndexConstants.FileName}'.");
        }

        string indexDownloadPath = Path.Combine(tempRoot, UpdateIndexConstants.FileName);
        await DownloadAssetAsync(indexAsset.BrowserDownloadUrl, indexDownloadPath);

        string json = await File.ReadAllTextAsync(indexDownloadPath);
        UpdateIndexManifest? index = JsonSerializer.Deserialize<UpdateIndexManifest>(json, JsonOptions);
        if (index is null || index.Edges.Count == 0)
        {
            throw new InvalidOperationException("update-index.json is missing or contains no update edges.");
        }

        return index;
    }

    private static async Task<string> ApplyIndexedPatchHopAsync(
        string gameDirectory,
        UpdateIndexEdge hop,
        string tempRoot,
        int hopIndex)
    {
        string hopDirectory = Path.Combine(tempRoot, $"hop-{hopIndex}-{hop.ToVersion}");
        string zipDownloadPath = Path.Combine(hopDirectory, hop.AssetName);
        string extractedPatchDirectory = Path.Combine(hopDirectory, "extract");

        if (Directory.Exists(hopDirectory))
        {
            Directory.Delete(hopDirectory, true);
        }

        Directory.CreateDirectory(hopDirectory);

        string downloadUrl = UpdateIndexNaming.BuildReleaseDownloadUrl(GitHubOwner, GitHubRepo, hop);
        Console.WriteLine($"Downloading patch asset: {hop.AssetName}");
        await DownloadAssetAsync(downloadUrl, zipDownloadPath);

        if (!string.IsNullOrWhiteSpace(hop.Sha256))
        {
            string actual = CalculateSha256(zipDownloadPath);
            if (!string.Equals(actual, hop.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Patch asset SHA-256 mismatch for '{hop.AssetName}'. Expected {hop.Sha256}, got {actual}.");
            }

            Console.WriteLine("Patch asset SHA-256 verified from update index.");
        }
        else
        {
            Console.WriteLine("Patch asset SHA-256 is not listed in update-index.json; digest verification is skipped.");
        }

        ZipFile.ExtractToDirectory(zipDownloadPath, extractedPatchDirectory, overwriteFiles: true);

        string patchJsonPath = Path.Combine(extractedPatchDirectory, "patch.json");
        if (!File.Exists(patchJsonPath))
        {
            throw new InvalidOperationException("The downloaded patch archive does not contain patch.json.");
        }

        PatchManifest patch = LoadPatchManifest(patchJsonPath);
        if (!string.Equals(patch.FromVersion, hop.FromVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Patch manifest rejected: patch.fromVersion '{patch.FromVersion}' does not match expected '{hop.FromVersion}'.");
        }

        if (!string.Equals(patch.ToVersion, hop.ToVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Patch manifest rejected: patch.toVersion '{patch.ToVersion}' does not match expected '{hop.ToVersion}'.");
        }

        VerifyPatchFiles(extractedPatchDirectory, patch);
        ApplyPatch(gameDirectory, extractedPatchDirectory, patch);
        VerifyInstalledGameFiles(gameDirectory, patch);
        WriteInstalledVersion(gameDirectory, patch.ToVersion);

        return patch.ToVersion;
    }

    private static async Task DownloadAssetAsync(string url, string destinationPath)
    {
        using HttpClient client = new();
        using HttpResponseMessage response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Patch asset download failed: {(int)response.StatusCode}");
        }

        await using FileStream output = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(output);
    }

    private static void VerifyAssetDigest(string zipPath, string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            Console.WriteLine("GitHub release asset digest is not available; pre-download asset verification is skipped.");
            return;
        }

        string actual = CalculateSha256(zipPath);
        string expected = NormalizeHexDigest(digest);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Downloaded ZIP SHA-256 mismatch. Expected {expected}, got {actual}.");
        }

        Console.WriteLine("GitHub release asset SHA-256 digest verified.");
    }

    private static string NormalizeHexDigest(string digest)
    {
        string value = digest.Trim().Trim('"');

        int colonIndex = value.IndexOf(':');
        if (colonIndex >= 0)
        {
            value = value[(colonIndex + 1)..];
        }

        if (value.Length == 44 && value.EndsWith("==", StringComparison.Ordinal) && value.Contains("="))
        {
            byte[] bytes = Convert.FromBase64String(value);
            value = Convert.ToHexString(bytes).ToLowerInvariant();
        }

        return value.ToLowerInvariant();
    }

    private static PatchManifest LoadPatchManifest(string patchManifestPath)
    {
        string json = File.ReadAllText(patchManifestPath);
        PatchManifest? manifest = JsonSerializer.Deserialize<PatchManifest>(json, JsonOptions);
        if (manifest is null)
        {
            throw new InvalidOperationException("patch.json is not a valid PatchManifest.");
        }

        if (string.IsNullOrWhiteSpace(manifest.FromVersion) || string.IsNullOrWhiteSpace(manifest.ToVersion))
        {
            throw new InvalidOperationException("patch.json is missing fromVersion or toVersion.");
        }

        return manifest;
    }

    private static void VerifyPatchFiles(string extractedPatchDirectory, PatchManifest patch)
    {
        foreach (KeyValuePair<string, PatchFileManifest> file in patch.Files)
        {
            string relPath = file.Key.Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.Combine(extractedPatchDirectory, relPath);

            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException($"Patch file is missing from extracted payload: {file.Key}");
            }

            FileInfo info = new FileInfo(fullPath);
            if (info.Length != file.Value.Size)
            {
                throw new InvalidOperationException(
                    $"Patch file size mismatch for '{file.Key}': expected {file.Value.Size}, got {info.Length}.");
            }

            string actualSha = CalculateSha256(fullPath);
            if (!string.Equals(actualSha, file.Value.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Patch file SHA-256 mismatch for '{file.Key}': expected {file.Value.Sha256}, got {actualSha}.");
            }
        }

        Console.WriteLine("Patch manifest file hashes and sizes verified from extracted files.");
    }

    private static void ApplyPatch(string gameDirectory, string extractedPatchDirectory, PatchManifest patch)
    {
        foreach (KeyValuePair<string, PatchFileManifest> file in patch.Files)
        {
            string sourcePath = Path.Combine(extractedPatchDirectory, file.Key.Replace('/', Path.DirectorySeparatorChar));
            string destinationPath = Path.Combine(gameDirectory, file.Key.Replace('/', Path.DirectorySeparatorChar));

            string? destinationFolder = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationFolder))
            {
                Directory.CreateDirectory(destinationFolder);
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        foreach (string deletedPath in patch.Deleted)
        {
            string fullPath = Path.Combine(gameDirectory, deletedPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
            else if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }

        Console.WriteLine("Patch files applied and deleted paths removed.");
    }

    private static void VerifyInstalledGameFiles(string gameDirectory, PatchManifest patch)
    {
        foreach (KeyValuePair<string, PatchFileManifest> file in patch.Files)
        {
            string fullPath = Path.Combine(gameDirectory, file.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException($"Installed game file is missing after update: {file.Key}");
            }

            FileInfo info = new FileInfo(fullPath);
            if (info.Length != file.Value.Size)
            {
                throw new InvalidOperationException(
                    $"Verified installed file size mismatch for '{file.Key}': expected {file.Value.Size}, got {info.Length}.");
            }

            string actualSha = CalculateSha256(fullPath);
            if (!string.Equals(actualSha, file.Value.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Verified installed file SHA-256 mismatch for '{file.Key}': expected {file.Value.Sha256}, got {actualSha}.");
            }
        }

        Console.WriteLine("Installed files verified against patch manifest.");
    }

    private static bool IsGameRunning(string gameExePath)
    {
        string expectedExeName = Path.GetFileName(gameExePath);
        Process[] processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expectedExeName));
        foreach (Process process in processes)
        {
            try
            {
                string fullPath = process.MainModule?.FileName ?? string.Empty;
                if (string.Equals(Path.GetFullPath(fullPath), Path.GetFullPath(gameExePath), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore inaccessible process metadata.
            }
        }

        return false;
    }

    private static async Task StageSelfUpdateBinaryAsync(GitHubRelease release, string gameDirectory)
    {
        GitHubAsset? updaterAsset = release.Assets.FirstOrDefault(asset =>
            asset.Name.Equals("Updater.exe", StringComparison.OrdinalIgnoreCase));

        if (updaterAsset is null)
        {
            Console.WriteLine("No Updater.exe self-update asset is present in the latest GitHub release.");
            return;
        }

        string stagingDirectory = Path.Combine(gameDirectory, UpdaterStagingDirectoryName);
        Directory.CreateDirectory(stagingDirectory);
        string stagedUpdaterPath = Path.Combine(stagingDirectory, updaterAsset.Name);

        await DownloadAssetAsync(updaterAsset.BrowserDownloadUrl, stagedUpdaterPath);
        VerifyAssetDigest(stagedUpdaterPath, updaterAsset.Digest);

        string bootstrapExe = Path.Combine(gameDirectory, "UpdaterBootstrap.exe");
        if (!File.Exists(bootstrapExe))
        {
            Console.WriteLine(
                $"Bootstrapper '{bootstrapExe}' is not present in the game directory. Self-update payload was downloaded and staged at '{stagedUpdaterPath}' but cannot be swapped into place.");
            return;
        }

        string? currentUpdaterPath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(currentUpdaterPath))
        {
            currentUpdaterPath = Path.Combine(gameDirectory, "Updater.exe");
        }

        int currentProcessId = Environment.ProcessId;

        Console.WriteLine($"Launching bootstrap handoff for '{currentUpdaterPath}' -> '{stagedUpdaterPath}' (current pid {currentProcessId})");

        Process.Start(new ProcessStartInfo
        {
            FileName = bootstrapExe,
            Arguments = $"\"{currentUpdaterPath}\" \"{stagedUpdaterPath}\" {currentProcessId} --no-relaunch",
            UseShellExecute = true
        });

        Console.WriteLine($"Staged new updater binary: {stagedUpdaterPath}");
    }

    private static string NormalizeVersion(string value) => VersionComparer.Normalize(value);

    private static int CompareVersions(string left, string right) => VersionComparer.Compare(left, right);

    private static string CalculateSha256(string path)
    {
        using FileStream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

internal sealed class InstalledVersionManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = new();
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

internal sealed class PatchManifest
{
    [JsonPropertyName("fromVersion")]
    public string FromVersion { get; set; } = "";

    [JsonPropertyName("toVersion")]
    public string ToVersion { get; set; } = "";

    [JsonPropertyName("files")]
    public Dictionary<string, PatchFileManifest> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("deleted")]
    public List<string> Deleted { get; set; } = new();
}

internal sealed class PatchFileManifest
{
    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}
