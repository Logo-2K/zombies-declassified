using System.Net.Http.Headers;
using System.Text.Json;

namespace ZDUpdater;

public sealed class GitHubAsset
{
    public string Name { get; init; } = "";
    public string BrowserDownloadUrl { get; init; } = "";
    public long Size { get; init; }
}

public sealed class GitHubRelease
{
    public string TagName { get; init; } = "";
    public IReadOnlyList<GitHubAsset> Assets { get; init; } = Array.Empty<GitHubAsset>();

    public GitHubAsset? FindAsset(string name) =>
        Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class GitHubApi
{
    public const string Owner = "Logo-2K";
    public const string Repo = "zombies-declassified";

    public static string AssetUrl(string tag, string assetName) =>
        $"https://github.com/{Owner}/{Repo}/releases/download/" +
        $"{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}";
    private readonly HttpClient _http;

    public GitHubApi(HttpClient http)
    {
        _http = http;

        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("ZombiesDeclassified-Updater", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<GitHubRelease> GetLatestReleaseAsync(CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {

            var reset = resp.Headers.TryGetValues("X-RateLimit-Reset", out var v)
                ? v.FirstOrDefault() : null;
            throw new UpdaterException(
                "GitHub API returned 403 — likely the 60 requests/hour unauthenticated " +
                "rate limit for this IP. " +
                (reset != null ? $"Resets at unix time {reset}." : "Try again later."));
        }
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new UpdaterException(
                $"GitHub reports no published release for {Owner}/{Repo}. " +
                "This fires when every release is still a draft or flagged pre-release " +
                "(the /releases/latest endpoint skips both) — nothing to install yet.");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new UpdaterException($"GitHub returned a response that was not JSON: {ex.Message}");
        }
        using (doc)
        {
        var root = doc.RootElement;
        var assets = new List<GitHubAsset>();
        if (root.TryGetProperty("assets", out var assetsEl))
        {
            foreach (var a in assetsEl.EnumerateArray())
            {
                assets.Add(new GitHubAsset
                {
                    Name = a.GetProperty("name").GetString() ?? "",
                    BrowserDownloadUrl = a.GetProperty("browser_download_url").GetString() ?? "",
                    Size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                });
            }
        }
        return new GitHubRelease
        {
            TagName = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "",
            Assets = assets,
        };
        }
    }
}

public sealed class UpdaterException : Exception
{
    public UpdaterException(string message) : base(message) { }
}
