using System.Text.Json;

namespace PokemonVylon.UpdateIndex;

public static class UpdateIndexBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static UpdateIndexManifest LoadOrCreate(string indexPath)
    {
        if (!File.Exists(indexPath))
        {
            return new UpdateIndexManifest();
        }

        string json = File.ReadAllText(indexPath);
        UpdateIndexManifest? manifest = JsonSerializer.Deserialize<UpdateIndexManifest>(json, JsonOptions);
        return manifest ?? new UpdateIndexManifest();
    }

    public static UpdateIndexEdge CreateEdge(
        string fromVersion,
        string toVersion,
        string assetName,
        string releaseTag,
        string? sha256)
    {
        return new UpdateIndexEdge
        {
            FromVersion = fromVersion.Trim(),
            ToVersion = toVersion.Trim(),
            AssetName = assetName,
            ReleaseTag = releaseTag,
            Sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.ToLowerInvariant()
        };
    }

    public static void UpsertEdge(UpdateIndexManifest manifest, UpdateIndexEdge edge)
    {
        manifest.Edges.RemoveAll(existing =>
            string.Equals(existing.FromVersion, edge.FromVersion, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.ToVersion, edge.ToVersion, StringComparison.OrdinalIgnoreCase));

        manifest.Edges.Add(edge);
        manifest.Edges.Sort(CompareEdges);
    }

    public static void ApplyRelease(UpdateIndexManifest manifest, string fromVersion, string toVersion)
    {
        string normalizedTo = VersionComparer.Normalize(toVersion);
        manifest.Latest = normalizedTo;

        if (string.IsNullOrWhiteSpace(manifest.MinimumSupported))
        {
            manifest.MinimumSupported = VersionComparer.Normalize(fromVersion);
        }
        else
        {
            string normalizedFrom = VersionComparer.Normalize(fromVersion);
            if (VersionComparer.Compare(normalizedFrom, manifest.MinimumSupported) < 0)
            {
                manifest.MinimumSupported = normalizedFrom;
            }
        }
    }

    public static async Task SaveAsync(UpdateIndexManifest manifest, string indexPath)
    {
        string? directory = Path.GetDirectoryName(indexPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(indexPath, json);
    }

    private static int CompareEdges(UpdateIndexEdge left, UpdateIndexEdge right)
    {
        int fromCompare = VersionComparer.Compare(left.FromVersion, right.FromVersion);
        if (fromCompare != 0)
        {
            return fromCompare;
        }

        return VersionComparer.Compare(left.ToVersion, right.ToVersion);
    }
}
