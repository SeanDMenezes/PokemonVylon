using System.Text.RegularExpressions;

namespace PatchBuilderGUI;

static partial class PatchValidation
{
    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionRegex();

    public static bool IsValidVersion(string value) =>
        !string.IsNullOrWhiteSpace(value) && VersionRegex().IsMatch(value.Trim());

    /// <summary>
    /// Returns null when inputs look good; otherwise a user-facing error message.
    /// </summary>
    public static string? Validate(PatchBuildRequest request)
    {
        string oldFolder = request.OldFolder.Trim();
        string newFolder = request.NewFolder.Trim();
        string fromVersion = request.FromVersion.Trim();
        string toVersion = request.ToVersion.Trim();

        if (string.IsNullOrWhiteSpace(oldFolder))
        {
            return "Choose the old (current live) game folder.";
        }

        if (string.IsNullOrWhiteSpace(newFolder))
        {
            return "Choose the new game folder.";
        }

        if (!IsValidVersion(fromVersion))
        {
            return "From version must look like 1.2.3 (numbers separated by dots).";
        }

        if (!IsValidVersion(toVersion))
        {
            return "To version must look like 1.2.3 (numbers separated by dots).";
        }

        if (string.Equals(fromVersion, toVersion, StringComparison.Ordinal))
        {
            return "From and to versions must be different.";
        }

        if (CompareVersions(fromVersion, toVersion) >= 0)
        {
            return "To version should be newer than from version.";
        }

        if (!Directory.Exists(oldFolder))
        {
            return $"Old folder does not exist:\n{oldFolder}";
        }

        if (!Directory.Exists(newFolder))
        {
            return $"New folder does not exist:\n{newFolder}";
        }

        string oldFull = Path.GetFullPath(oldFolder);
        string newFull = Path.GetFullPath(newFolder);

        if (string.Equals(oldFull, newFull, StringComparison.OrdinalIgnoreCase))
        {
            return "Old and new folders must be different directories.";
        }

        if (!Directory.EnumerateFileSystemEntries(oldFull).Any())
        {
            return "Old folder is empty. Pick the folder that contains the previous game build.";
        }

        if (!Directory.EnumerateFileSystemEntries(newFull).Any())
        {
            return "New folder is empty. Pick the folder that contains the new game build.";
        }

        string outputFolder = Path.Combine(
            Path.GetDirectoryName(newFull)!,
            $"patch-{toVersion}");

        if (IsPathInside(outputFolder, newFull))
        {
            return "Patch output would land inside the new build folder. Pick a different new-folder location.";
        }

        return null;
    }

    public static string GetZipPath(string newFolder, string fromVersion, string toVersion)
    {
        string newFull = Path.GetFullPath(newFolder.Trim());
        return Path.Combine(
            Path.GetDirectoryName(newFull)!,
            PokemonVylon.UpdateIndex.UpdateIndexNaming.GetPatchZipFileName(
                fromVersion.Trim(),
                toVersion.Trim()));
    }

    public static string GetUpdateIndexPath(string newFolder) =>
        PokemonVylon.UpdateIndex.UpdateIndexNaming.GetIndexPath(
            Path.GetDirectoryName(Path.GetFullPath(newFolder.Trim()))!);

    public static string GetOutputFolder(string newFolder, string toVersion)
    {
        string newFull = Path.GetFullPath(newFolder.Trim());
        return Path.Combine(
            Path.GetDirectoryName(newFull)!,
            $"patch-{toVersion.Trim()}");
    }

    public static int CompareVersions(string left, string right)
    {
        var leftParts = left.Split('.').Select(int.Parse).ToArray();
        var rightParts = right.Split('.').Select(int.Parse).ToArray();

        for (int i = 0; i < 3; i++)
        {
            int cmp = leftParts[i].CompareTo(rightParts[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return 0;
    }

    static bool IsPathInside(string candidatePath, string parentPath)
    {
        string candidateFull = Path.GetFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        string parentFull = Path.GetFullPath(parentPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return candidateFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase);
    }
}
