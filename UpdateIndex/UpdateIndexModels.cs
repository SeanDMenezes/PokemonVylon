using System.Text.Json.Serialization;

namespace PokemonVylon.UpdateIndex;

public static class UpdateIndexConstants
{
    public const string FileName = "update-index.json";

    public const string GitHubOwner = "SeanDMenezes";
    public const string GitHubRepo = "PokemonVylon";

    /// <summary>
    /// Fixed release tag hosting the updater and builder binaries. Kept separate from the
    /// game version sequence so a player who skips releases still converges on the current
    /// updater.
    /// </summary>
    public const string ToolsReleaseTag = "tools";

    public const string WindowsUpdaterAssetName = "Updater.exe";
    public const string LinuxUpdaterAssetName = "Updater-linux-x64";
}

public sealed class UpdateIndexManifest
{
    [JsonPropertyName("latest")]
    public string Latest { get; set; } = "";

    [JsonPropertyName("minimumSupported")]
    public string MinimumSupported { get; set; } = "";

    [JsonPropertyName("edges")]
    public List<UpdateIndexEdge> Edges { get; set; } = [];
}

public sealed class UpdateIndexEdge
{
    [JsonPropertyName("fromVersion")]
    public string FromVersion { get; set; } = "";

    [JsonPropertyName("toVersion")]
    public string ToVersion { get; set; } = "";

    [JsonPropertyName("assetName")]
    public string AssetName { get; set; } = "";

    [JsonPropertyName("releaseTag")]
    public string ReleaseTag { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}
