using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class Program
{
    private const string GitHubOwner = "SeanDMenezes";
    private const string GitHubRepo = "PokemonVylon";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("Pokemon Vylon Updater - Phase 1 Core Implementation");

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

        string latestVersion = NormalizeVersion(latestRelease.TagName);
        Console.WriteLine($"Latest GitHub tag: {latestRelease.TagName} ({latestVersion})");

        if (CompareVersions(latestVersion, installedVersion) <= 0)
        {
            Console.WriteLine("No update is available for the installed version.");
            return 0;
        }

        GitHubAsset? asset = SelectPatchAsset(latestRelease, latestVersion);
        if (asset is null)
        {
            Console.Error.WriteLine("No direct release asset matching a patch package could be found for the latest release.");
            return 1;
        }

        Console.WriteLine($"Selected patch asset: {asset.Name}");

        string tempRoot = Path.Combine(Path.GetTempPath(), "PokemonVylonUpdater", latestVersion);
        Directory.CreateDirectory(tempRoot);

        string zipDownloadPath = Path.Combine(tempRoot, asset.Name);
        string extractedPatchDirectory = Path.Combine(tempRoot, "patch");

        try
        {
            await DownloadAssetAsync(asset.BrowserDownloadUrl, zipDownloadPath);

            VerifyAssetDigest(zipDownloadPath, asset.Digest);

            if (Directory.Exists(extractedPatchDirectory))
            {
                Directory.Delete(extractedPatchDirectory, true);
            }

            ZipFile.ExtractToDirectory(zipDownloadPath, extractedPatchDirectory, overwriteFiles: true);

            string patchJsonPath = Path.Combine(extractedPatchDirectory, "patch.json");
            if (!File.Exists(patchJsonPath))
            {
                throw new InvalidOperationException("The downloaded patch archive does not contain patch.json.");
            }

            PatchManifest patch = LoadPatchManifest(patchJsonPath);
            if (!string.Equals(patch.FromVersion, installedVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Patch manifest rejected: patch.fromVersion '{patch.FromVersion}' does not match installed version '{installedVersion}'.");
            }

            if (!string.Equals(patch.ToVersion, latestVersion, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(
                    $"Selected release target version '{latestVersion}' differs from patch manifest target '{patch.ToVersion}'. Continuing with manifest target '{patch.ToVersion}'.");
            }

            VerifyPatchFiles(extractedPatchDirectory, patch);

            ApplyPatch(gameDirectory, extractedPatchDirectory, patch);
            VerifyInstalledGameFiles(gameDirectory, patch);

            WriteInstalledVersion(gameDirectory, patch.ToVersion);

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

    private static GitHubAsset? SelectPatchAsset(GitHubRelease release, string latestVersion)
    {
        if (release.Assets is null || release.Assets.Count == 0)
        {
            return null;
        }

        string latestVersionNoV = NormalizeVersion(latestVersion);

        foreach (GitHubAsset asset in release.Assets)
        {
            if (!asset.Name.EndsWith(".patch.zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (asset.Name.Contains(latestVersionNoV, StringComparison.OrdinalIgnoreCase)
                || asset.Name.Contains(latestVersion, StringComparison.OrdinalIgnoreCase)
                || asset.Name.Contains("v" + latestVersionNoV, StringComparison.OrdinalIgnoreCase))
            {
                return asset;
            }
        }

        return release.Assets.FirstOrDefault(asset => asset.Name.EndsWith(".patch.zip", StringComparison.OrdinalIgnoreCase));
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

    private static string NormalizeVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string sanitized = value.Trim();
        if (sanitized.StartsWith('v') || sanitized.StartsWith('V'))
        {
            sanitized = sanitized.Substring(1);
        }

        return sanitized;
    }

    private static int CompareVersions(string left, string right)
    {
        string[] leftParts = NormalizeVersion(left).Split('.', StringSplitOptions.RemoveEmptyEntries);
        string[] rightParts = NormalizeVersion(right).Split('.', StringSplitOptions.RemoveEmptyEntries);

        int length = Math.Max(leftParts.Length, rightParts.Length);
        for (int i = 0; i < length; i++)
        {
            int leftNumber = i < leftParts.Length && int.TryParse(leftParts[i], out int leftParsed) ? leftParsed : 0;
            int rightNumber = i < rightParts.Length && int.TryParse(rightParts[i], out int rightParsed) ? rightParsed : 0;

            if (leftNumber < rightNumber)
            {
                return -1;
            }

            if (leftNumber > rightNumber)
            {
                return 1;
            }
        }

        return 0;
    }

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
