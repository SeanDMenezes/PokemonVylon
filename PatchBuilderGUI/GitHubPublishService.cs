using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PokemonVylon.UpdateIndex;

namespace PatchBuilderGUI;

/// <summary>
/// Publishes a release as a draft. Drafts are invisible to players and are never treated as
/// "latest", so the updater cannot observe a release whose assets are still uploading — a
/// half-uploaded release would otherwise fail every player's update with a missing
/// update-index.json.
/// </summary>
static class GitHubPublishService
{
    const string ApiRoot = "https://api.github.com";
    const string UploadRoot = "https://uploads.github.com";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<GitHubPublishResult> PublishDraftAsync(
        GitHubPublishRequest request,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        string tag = UpdateIndexNaming.GetDefaultReleaseTag(request.ToVersion);

        using HttpClient client = CreateClient(request.Token);

        GitHubRelease release = await FindOrCreateDraftReleaseAsync(
            client, tag, progress, cancellationToken);

        var uploaded = new List<string>();
        var skipped = new List<string>();

        await UploadFileAssetAsync(
            client, release, request.PatchZipPath, uploaded, progress, cancellationToken);

        await UploadFileAssetAsync(
            client, release, request.UpdateIndexPath, uploaded, progress, cancellationToken);

        if (request.IncludeMigrationUpdater)
        {
            await CopyUpdaterFromToolsAsync(
                client, release, uploaded, skipped, progress, cancellationToken);
        }

        return new GitHubPublishResult(
            tag,
            release.HtmlUrl,
            release.WasExisting,
            AlreadyPublished: !release.Draft,
            uploaded,
            skipped);
    }

    static HttpClient CreateClient(string token)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PokemonVylonPatchBuilder/1.0");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    static async Task<GitHubRelease> FindOrCreateDraftReleaseAsync(
        HttpClient client,
        string tag,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        // Published releases resolve by tag at any age. Drafts are absent from that endpoint,
        // so they are found by listing instead — which is safe because a draft awaiting
        // upload is always among the most recent releases.
        string tagUrl = $"{ApiRoot}/repos/{UpdateIndexConstants.GitHubOwner}/{UpdateIndexConstants.GitHubRepo}/releases/tags/{Uri.EscapeDataString(tag)}";
        GitHubRelease? existing = await GetJsonOrNullAsync<GitHubRelease>(
            client, tagUrl, cancellationToken);

        if (existing is null)
        {
            string listUrl = $"{ApiRoot}/repos/{UpdateIndexConstants.GitHubOwner}/{UpdateIndexConstants.GitHubRepo}/releases?per_page=100";
            List<GitHubRelease>? releases = await GetJsonAsync<List<GitHubRelease>>(
                client, listUrl, cancellationToken);

            existing = releases?.FirstOrDefault(r =>
                string.Equals(r.TagName, tag, StringComparison.OrdinalIgnoreCase));
        }

        if (existing is not null)
        {
            existing.WasExisting = true;
            progress.Report(existing.Draft
                ? $"Reusing existing draft release {tag}."
                : $"WARNING: release {tag} is already published and visible to players.");
            return existing;
        }

        progress.Report($"Creating draft release {tag}...");

        var payload = new
        {
            tag_name = tag,
            name = tag,
            draft = true,
            body = $"Patch release {tag}."
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        string createUrl = $"{ApiRoot}/repos/{UpdateIndexConstants.GitHubOwner}/{UpdateIndexConstants.GitHubRepo}/releases";
        using HttpResponseMessage response = await client.PostAsync(createUrl, content, cancellationToken);
        await EnsureSuccessAsync(response, "create release", cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        GitHubRelease created = JsonSerializer.Deserialize<GitHubRelease>(body, JsonOptions)
            ?? throw new InvalidOperationException("GitHub returned an empty release payload.");

        progress.Report($"Draft release created: {created.HtmlUrl}");
        return created;
    }

    static async Task UploadFileAssetAsync(
        HttpClient client,
        GitHubRelease release,
        string filePath,
        List<string> uploaded,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Asset to upload was not found: {filePath}");
        }

        string assetName = Path.GetFileName(filePath);
        await DeleteAssetIfPresentAsync(client, release, assetName, progress, cancellationToken);

        var info = new FileInfo(filePath);
        progress.Report($"Uploading {assetName} ({FormatSize(info.Length)})...");

        await using FileStream source = File.OpenRead(filePath);
        await UploadStreamAsync(
            client, release, assetName, source, info.Length, progress, cancellationToken);

        uploaded.Add(assetName);
        progress.Report($"Uploaded {assetName}.");
    }

    /// <summary>
    /// Copies Updater.exe from the tools release rather than a local build. Taking the exact
    /// bytes players will later hash against tools guarantees they never see a mismatch and
    /// re-download the updater a second time.
    /// </summary>
    static async Task CopyUpdaterFromToolsAsync(
        HttpClient client,
        GitHubRelease release,
        List<string> uploaded,
        List<string> skipped,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        string assetName = UpdateIndexConstants.WindowsUpdaterAssetName;
        string toolsUrl = $"{ApiRoot}/repos/{UpdateIndexConstants.GitHubOwner}/{UpdateIndexConstants.GitHubRepo}/releases/tags/{UpdateIndexConstants.ToolsReleaseTag}";

        GitHubRelease? tools = await GetJsonOrNullAsync<GitHubRelease>(client, toolsUrl, cancellationToken);
        GitHubAsset? source = tools?.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));

        if (source is null)
        {
            skipped.Add($"{assetName} (not present on the '{UpdateIndexConstants.ToolsReleaseTag}' release)");
            progress.Report($"Skipping {assetName}: not found on the '{UpdateIndexConstants.ToolsReleaseTag}' release.");
            return;
        }

        GitHubAsset? alreadyThere = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));

        if (alreadyThere is not null
            && !string.IsNullOrWhiteSpace(source.Digest)
            && string.Equals(alreadyThere.Digest, source.Digest, StringComparison.OrdinalIgnoreCase))
        {
            skipped.Add($"{assetName} (already on the release and identical to tools)");
            progress.Report($"Skipping {assetName}: already present and identical to tools.");
            return;
        }

        progress.Report($"Fetching {assetName} from the '{UpdateIndexConstants.ToolsReleaseTag}' release ({FormatSize(source.Size)})...");

        string tempPath = Path.Combine(Path.GetTempPath(), $"vylon-{Guid.NewGuid():N}-{assetName}");
        try
        {
            // Downloaded without the auth header: the redirect target rejects forwarded
            // Authorization headers, and the repository is public.
            using (var anonymous = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            {
                anonymous.DefaultRequestHeaders.UserAgent.ParseAdd("PokemonVylonPatchBuilder/1.0");

                await using Stream download = await anonymous.GetStreamAsync(
                    source.BrowserDownloadUrl, cancellationToken);
                await using FileStream target = File.Create(tempPath);
                await download.CopyToAsync(target, cancellationToken);
            }

            await DeleteAssetIfPresentAsync(client, release, assetName, progress, cancellationToken);

            var info = new FileInfo(tempPath);
            progress.Report($"Uploading {assetName} ({FormatSize(info.Length)})...");

            await using (FileStream upload = File.OpenRead(tempPath))
            {
                await UploadStreamAsync(
                    client, release, assetName, upload, info.Length, progress, cancellationToken);
            }

            uploaded.Add(assetName);
            progress.Report($"Uploaded {assetName} (byte-identical to tools).");
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
                // A leftover temp file is harmless.
            }
        }
    }

    static async Task UploadStreamAsync(
        HttpClient client,
        GitHubRelease release,
        string assetName,
        Stream source,
        long totalBytes,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        string url =
            $"{UploadRoot}/repos/{UpdateIndexConstants.GitHubOwner}/{UpdateIndexConstants.GitHubRepo}/releases/{release.Id}/assets?name={Uri.EscapeDataString(assetName)}";

        await using var reporting = new ProgressStream(source, totalBytes, assetName, progress);
        using var content = new StreamContent(reporting);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = totalBytes;

        using HttpResponseMessage response = await client.PostAsync(url, content, cancellationToken);
        await EnsureSuccessAsync(response, $"upload {assetName}", cancellationToken);
    }

    static async Task DeleteAssetIfPresentAsync(
        HttpClient client,
        GitHubRelease release,
        string assetName,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        GitHubAsset? existing = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            return;
        }

        // GitHub rejects a duplicate asset name rather than replacing it.
        progress.Report($"Replacing existing {assetName} on the release...");

        string url = $"{ApiRoot}/repos/{UpdateIndexConstants.GitHubOwner}/{UpdateIndexConstants.GitHubRepo}/releases/assets/{existing.Id}";
        using HttpResponseMessage response = await client.DeleteAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, $"delete existing {assetName}", cancellationToken);

        release.Assets.Remove(existing);
    }

    static async Task<T?> GetJsonOrNullAsync<T>(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return default;
        }

        await EnsureSuccessAsync(response, $"GET {url}", cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    static async Task<T?> GetJsonAsync<T>(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, $"GET {url}", cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        string hint = (int)response.StatusCode switch
        {
            401 => " The token was rejected — check it has not expired.",
            403 => " The token lacks permission. It needs Contents: Read and write on this repository.",
            404 => " Not found, which usually also means the token cannot see this repository.",
            422 => " GitHub rejected the request as invalid — often a tag that already exists in another state.",
            _ => ""
        };

        throw new InvalidOperationException(
            $"Could not {operation}: GitHub returned {(int)response.StatusCode}.{hint}\n\n{body}");
    }

    static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0:F1} MB"
        : $"{bytes / 1024.0:F0} KB";

    sealed class GitHubRelease
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = [];

        [JsonIgnore]
        public bool WasExisting { get; set; }
    }

    sealed class GitHubAsset
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }
}

/// <summary>
/// Reports upload percentage. A 70 MB asset on a slow connection is otherwise several
/// minutes of silence.
/// </summary>
sealed class ProgressStream(
    Stream inner,
    long totalBytes,
    string label,
    IProgress<string> progress) : Stream
{
    long _sent;
    int _lastReportedPercent = -1;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => totalBytes;

    public override long Position
    {
        get => _sent;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        Advance(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken);
        Advance(read);
        return read;
    }

    void Advance(int read)
    {
        if (read <= 0 || totalBytes <= 0)
        {
            return;
        }

        _sent += read;
        int percent = (int)(_sent * 100 / totalBytes);
        if (percent >= _lastReportedPercent + 10 && percent < 100)
        {
            _lastReportedPercent = percent;
            progress.Report($"  {label}: {percent}%");
        }
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
