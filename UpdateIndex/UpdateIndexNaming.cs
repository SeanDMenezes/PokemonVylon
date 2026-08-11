namespace PokemonVylon.UpdateIndex;

public static class UpdateIndexNaming
{
    public static string GetPatchZipFileName(string fromVersion, string toVersion) =>
        $"PokemonVylon-v{fromVersion.Trim()}-to-v{toVersion.Trim()}.patch.zip";

    public static string GetDefaultReleaseTag(string toVersion)
    {
        string trimmed = toVersion.Trim();
        return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? trimmed : $"v{trimmed}";
    }

    public static string GetIndexPath(string outputDirectory) =>
        Path.Combine(outputDirectory, UpdateIndexConstants.FileName);

    public static string BuildReleaseDownloadUrl(
        string gitHubOwner,
        string gitHubRepo,
        UpdateIndexEdge edge)
    {
        string releaseTag = edge.ReleaseTag.Trim();
        if (!releaseTag.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            releaseTag = $"v{releaseTag}";
        }

        return $"https://github.com/{gitHubOwner}/{gitHubRepo}/releases/download/{releaseTag}/{edge.AssetName}";
    }
}
