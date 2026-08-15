using System.Text.RegularExpressions;
using PokemonVylon.UpdateIndex;

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

    /// <summary>
    /// Problems that still produce a valid-looking patch, but strand players once the index
    /// is uploaded. They are warnings rather than errors because a deliberate rebuild of an
    /// older edge is legitimate.
    /// </summary>
    public static IReadOnlyList<string> GetWarnings(PatchBuildRequest request)
    {
        var warnings = new List<string>();

        string indexPath;
        try
        {
            indexPath = GetUpdateIndexPath(request.NewFolder);
        }
        catch
        {
            return warnings;
        }

        if (!File.Exists(indexPath))
        {
            warnings.Add(
                "No update-index.json was found at:\n" +
                indexPath + "\n\n" +
                "Building now starts a brand-new index containing only this one patch. " +
                "Uploading that would erase every existing update route, and players on older " +
                "versions would be told to reinstall the game from scratch.\n\n" +
                "Download update-index.json from the latest GitHub release into that folder first.");

            return warnings;
        }

        UpdateIndexManifest index;
        try
        {
            index = UpdateIndexBuilder.LoadOrCreate(indexPath);
        }
        catch (Exception ex)
        {
            warnings.Add($"update-index.json could not be read ({ex.Message}). Building would replace it.");
            return warnings;
        }

        string latest = VersionComparer.Normalize(index.Latest);
        string fromVersion = VersionComparer.Normalize(request.FromVersion.Trim());
        string toVersion = request.ToVersion.Trim();

        if (!string.IsNullOrWhiteSpace(latest)
            && !string.Equals(fromVersion, latest, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(
                $"This patch starts from {fromVersion}, but the newest version in the index is {latest}.\n\n" +
                $"Nothing would lead out of {latest}, so anyone already running it could not reach " +
                $"{toVersion} and their update would fail.\n\n" +
                $"Unless your old folder is really {latest}, the from version should be {latest}.");
        }

        return warnings;
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
