using System.Text.Json.Serialization;

namespace PatchBuilderGUI;

sealed class FileSnapshot(
    string relativePath,
    string fullPath,
    long length,
    DateTime lastWriteUtc)
{
    public string RelativePath { get; } = relativePath;
    public string FullPath { get; } = fullPath;
    public long Length { get; } = length;
    public DateTime LastWriteUtc { get; } = lastWriteUtc;
}

sealed class PatchFileManifest
{
    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}

sealed class PatchManifest
{
    [JsonPropertyName("fromVersion")]
    public string FromVersion { get; set; } = "";

    [JsonPropertyName("toVersion")]
    public string ToVersion { get; set; } = "";

    [JsonPropertyName("files")]
    public Dictionary<string, PatchFileManifest> Files { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("deleted")]
    public List<string> Deleted { get; set; } = [];
}

sealed class BuildManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("files")]
    public Dictionary<string, PatchFileManifest> Files { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

sealed record PatchBuildRequest(
    string OldFolder,
    string NewFolder,
    string FromVersion,
    string ToVersion);

sealed record PatchBuildResult(
    string ZipPath,
    string UpdateIndexPath,
    long ZipSizeBytes,
    int ChangedOrNewFileCount,
    int DeletedFileCount);

sealed record GitHubPublishRequest(
    string Token,
    string ToVersion,
    string PatchZipPath,
    string UpdateIndexPath,
    bool IncludeMigrationUpdater);

sealed record GitHubPublishResult(
    string ReleaseTag,
    string ReleaseUrl,
    bool WasExistingRelease,
    bool AlreadyPublished,
    IReadOnlyList<string> UploadedAssets,
    IReadOnlyList<string> SkippedAssets);
