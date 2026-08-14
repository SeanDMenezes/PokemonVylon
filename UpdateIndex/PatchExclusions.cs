namespace PokemonVylon.UpdateIndex;

/// <summary>
/// Files and folders that must never enter a patch payload. The updater applies
/// patches into its own directory, so shipping the running updater or bootstrap
/// binaries would fail with a sharing violation part-way through an update.
/// </summary>
public static class PatchExclusions
{
    private static readonly string[] ExcludedFileNames =
    [
        "manifest.json",
        "patch.json",
        "version.json",
        "Updater.exe",
        "UpdaterBootstrap.exe",
        UpdateIndexConstants.FileName
    ];

    private static readonly string[] ExcludedDirectoryNames =
    [
        ".updater-staging"
    ];

    /// <summary>
    /// Takes a forward-slash relative path within a build folder.
    /// </summary>
    public static bool IsExcluded(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        string fileName = Path.GetFileName(normalized);

        foreach (string excluded in ExcludedFileNames)
        {
            if (string.Equals(fileName, excluded, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Bootstrap leaves Updater.exe.bak behind after a self-update swap.
        if (fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string excluded in ExcludedDirectoryNames)
            {
                if (string.Equals(segment, excluded, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
