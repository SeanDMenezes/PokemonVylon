using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

if (args.Length != 4)
{
    Console.WriteLine(
        "Usage: PatchBuilder <old-folder> <new-folder> <from-version> <to-version>"
    );
    return;
}

string oldFolder = Path.GetFullPath(args[0]);
string newFolder = Path.GetFullPath(args[1]);
string fromVersion = args[2];
string toVersion = args[3];

if (!IsValidVersion(fromVersion))
{
    Console.WriteLine($"Invalid fromVersion: '{fromVersion}'");
    return;
}

if (!IsValidVersion(toVersion))
{
    Console.WriteLine($"Invalid toVersion: '{toVersion}'");
    return;
}

if (!Directory.Exists(oldFolder))
{
    Console.WriteLine($"Old folder does not exist: {oldFolder}");
    return;
}

if (!Directory.Exists(newFolder))
{
    Console.WriteLine($"New folder does not exist: {newFolder}");
    return;
}

if (Directory.EnumerateFileSystemEntries(oldFolder).Any() == false)
{
    Console.WriteLine($"Old folder is empty: {oldFolder}");
    return;
}

if (Directory.EnumerateFileSystemEntries(newFolder).Any() == false)
{
    Console.WriteLine($"New folder is empty: {newFolder}");
    return;
}

if (string.Equals(
        oldFolder,
        newFolder,
        StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Old and new folders must be different directories.");
    return;
}

string outputFolder = Path.Combine(
    Path.GetDirectoryName(newFolder)!,
    $"patch-{toVersion}"
);

if (IsPathInside(outputFolder, newFolder))
{
    Console.WriteLine("Patch output location must not be inside the new build directory.");
    return;
}

if (Directory.Exists(outputFolder))
{
    Directory.Delete(outputFolder, true);
}

Directory.CreateDirectory(outputFolder);

Console.WriteLine("Building patch...");
Console.WriteLine($"Old: {oldFolder}");
Console.WriteLine($"New: {newFolder}");
Console.WriteLine();

var oldFiles = GetFiles(oldFolder);
var newFiles = GetFiles(newFolder);

var changedFiles = new ConcurrentBag<string>();

var commonRelativePaths = new HashSet<string>(
    newFiles.Keys,
    StringComparer.OrdinalIgnoreCase
);

commonRelativePaths.IntersectWith(oldFiles.Keys);

var totalCommonFiles = commonRelativePaths.Count;
var processedFiles = 0;

Parallel.ForEach(
    commonRelativePaths,
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

        var completed = Interlocked.Increment(ref processedFiles);
        if (completed % 1000 == 0)
        {
            Console.WriteLine($"Compared {completed}/{totalCommonFiles} common files");
        }
    });

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

Console.WriteLine($"Changed/new files: {changedFileList.Count}");
Console.WriteLine($"Deleted files:      {deletedFiles.Count}");
Console.WriteLine();

// Copy changed files into patch directory
foreach (string relativePath in changedFileList)
{
    string source = Path.Combine(
        newFolder,
        relativePath.Replace('/', Path.DirectorySeparatorChar)
    );

    string destination = Path.Combine(
        outputFolder,
        relativePath.Replace('/', Path.DirectorySeparatorChar)
    );

    string? directory = Path.GetDirectoryName(destination);

    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    File.Copy(source, destination, true);

    Console.WriteLine($"Added: {relativePath}");
}

// Write the target build manifest for whole-game verification.
Console.WriteLine("Generating target build manifest...");
var manifestBuild = BuildBuildManifest(newFolder, toVersion);
Console.WriteLine("Target build manifest ready.");

await WriteJsonAsync(
    Path.Combine(newFolder, "manifest.json"),
    manifestBuild,
    WriteIndented: true);

// Create patch manifest with from/to version and file-level checksums.
PatchManifest manifest = new PatchManifest
{
    FromVersion = fromVersion,
    ToVersion = toVersion,
    Files = new Dictionary<string, PatchFileManifest>(StringComparer.OrdinalIgnoreCase),
    Deleted = deletedFiles
};

foreach (string relativePath in changedFileList)
{
    string fullPath = Path.Combine(
        newFolder,
        relativePath.Replace('/', Path.DirectorySeparatorChar)
    );

    FileInfo info = new FileInfo(fullPath);
    manifest.Files[relativePath] = new PatchFileManifest
    {
        Size = info.Length,
        Sha256 = CalculateSha256(fullPath)
    };
}

string manifestPath = Path.Combine(
    outputFolder,
    "patch.json"
);

await WriteJsonAsync(
    manifestPath,
    manifest,
    WriteIndented: true);

// Create ZIP
string zipPath = Path.Combine(
    Path.GetDirectoryName(newFolder)!,
    $"PokemonVylon-v{toVersion}.patch.zip"
);

if (File.Exists(zipPath))
{
    File.Delete(zipPath);
}

ZipFile.CreateFromDirectory(
    outputFolder,
    zipPath,
    CompressionLevel.Optimal,
    false
);

Console.WriteLine();
Console.WriteLine("Patch created!");
Console.WriteLine();
Console.WriteLine($"Patch: {zipPath}");
Console.WriteLine($"Size:  {new FileInfo(zipPath).Length / 1024.0 / 1024.0:F2} MB");

static bool IsValidVersion(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    return Regex.IsMatch(value, "^\\d+\\.\\d+\\.\\d+$");
}

static bool IsPathInside(string candidatePath, string parentPath)
{
    string candidateFull = Path.GetFullPath(candidatePath);
    string parentFull = Path.GetFullPath(parentPath);

    return candidateFull.StartsWith(
        parentFull,
        StringComparison.OrdinalIgnoreCase);
}

static async Task WriteJsonAsync(
    string path,
    object payload,
    bool WriteIndented)
{
    string json = JsonSerializer.Serialize(
        payload,
        new JsonSerializerOptions
        {
            WriteIndented = WriteIndented,
            PropertyNamingPolicy = null
        });

    await File.WriteAllTextAsync(path, json);
}

static BuildManifest BuildBuildManifest(string folder, string version)
{
    string manifestPath = Path.Combine(folder, "manifest.json");
    if (File.Exists(manifestPath))
    {
        Console.WriteLine($"Found existing target build manifest: {manifestPath}");

        BuildManifest? existing = LoadBuildManifest(manifestPath);
        if (existing is not null)
        {
            Console.WriteLine("Reusing existing target build manifest.");
            return existing;
        }
    }

    var allFiles = GetFiles(folder);

    ConcurrentDictionary<string, PatchFileManifest> files = new(
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

static BuildManifest? LoadBuildManifest(string manifestPath)
{
    try
    {
        string json = File.ReadAllText(manifestPath);
        BuildManifest? result = JsonSerializer.Deserialize<BuildManifest>(json);

        if (result is null)
        {
            return null;
        }

        return result;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not reuse existing manifest. Recomputing. ({ex.Message})");
        return null;
    }
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

        if (string.Equals(
                Path.GetFileName(relativePath),
                "manifest.json",
                StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (string.Equals(
                Path.GetFileName(relativePath),
                "patch.json",
                StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        FileInfo info = new FileInfo(filePath);

        results[relativePath] = new FileSnapshot(
            relativePath,
            filePath,
            info.Length,
            info.LastWriteTimeUtc
        );
    }

    return results;
}

static string CalculateSha256(string path)
{
    using FileStream stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        1024 * 1024,
        FileOptions.SequentialScan |
            FileOptions.Asynchronous);

    byte[] hash = SHA256.HashData(stream);

    return Convert.ToHexString(hash)
        .ToLowerInvariant();
}

class FileSnapshot
{
    public FileSnapshot(
        string relativePath,
        string fullPath,
        long length,
        DateTime lastWriteUtc)
    {
        RelativePath = relativePath;
        FullPath = fullPath;
        Length = length;
        LastWriteUtc = lastWriteUtc;
    }

    public string RelativePath { get; }

    public string FullPath { get; }

    public long Length { get; }

    public DateTime LastWriteUtc { get; }
}

class PatchFileManifest
{
    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}

class PatchManifest
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

class BuildManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("files")]
    public Dictionary<string, PatchFileManifest> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
