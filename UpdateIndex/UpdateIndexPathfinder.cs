namespace PokemonVylon.UpdateIndex;

public static class UpdateIndexPathfinder
{
    public static IReadOnlyList<UpdateIndexEdge>? FindShortestPath(
        UpdateIndexManifest index,
        string installedVersion,
        string latestVersion)
    {
        string installed = VersionComparer.Normalize(installedVersion);
        string latest = VersionComparer.Normalize(latestVersion);

        if (VersionComparer.Compare(installed, latest) >= 0)
        {
            return [];
        }

        if (!string.IsNullOrWhiteSpace(index.MinimumSupported)
            && VersionComparer.Compare(installed, index.MinimumSupported) < 0)
        {
            return null;
        }

        var adjacency = new Dictionary<string, List<UpdateIndexEdge>>(StringComparer.OrdinalIgnoreCase);
        foreach (UpdateIndexEdge edge in index.Edges)
        {
            string from = VersionComparer.Normalize(edge.FromVersion);
            if (!adjacency.TryGetValue(from, out List<UpdateIndexEdge>? edges))
            {
                edges = [];
                adjacency[from] = edges;
            }

            edges.Add(edge);
        }

        var queue = new Queue<string>();
        var previous = new Dictionary<string, UpdateIndexEdge>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        queue.Enqueue(installed);
        visited.Add(installed);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (string.Equals(current, latest, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!adjacency.TryGetValue(current, out List<UpdateIndexEdge>? outgoing))
            {
                continue;
            }

            foreach (UpdateIndexEdge edge in outgoing
                         .OrderBy(candidate => VersionComparer.Normalize(candidate.ToVersion), StringComparer.OrdinalIgnoreCase))
            {
                string next = VersionComparer.Normalize(edge.ToVersion);
                if (visited.Contains(next))
                {
                    continue;
                }

                visited.Add(next);
                previous[next] = edge;
                queue.Enqueue(next);
            }
        }

        if (!visited.Contains(latest))
        {
            return null;
        }

        var path = new List<UpdateIndexEdge>();
        string walk = latest;
        while (!string.Equals(walk, installed, StringComparison.OrdinalIgnoreCase))
        {
            if (!previous.TryGetValue(walk, out UpdateIndexEdge? edge))
            {
                return null;
            }

            path.Add(edge);
            walk = VersionComparer.Normalize(edge.FromVersion);
        }

        path.Reverse();
        return path;
    }
}
