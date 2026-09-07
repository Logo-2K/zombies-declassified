using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

namespace ZDUpdater;

public sealed record DownloadOutcome(bool Success, string? PlacedTempPath, string? FailureReason);

public sealed class ChannelDownloader
{
    private readonly HttpClient _http;
    private readonly GitHubRelease _release;
    private readonly DriveClient _drive;
    private readonly string _runTempDir;
    private readonly Dictionary<string, string> _extractedArchiveCache = new();
    private readonly Dictionary<string, bool> _rangeSupportCache = new();

    public ChannelDownloader(HttpClient http, GitHubRelease release, string runTempDir)
    {
        _http = http;
        _release = release;
        _drive = new DriveClient(http);
        _runTempDir = runTempDir;
        Directory.CreateDirectory(_runTempDir);
    }

    public IProgress<long>? BytesProgress { get; set; }

    public async Task<DownloadOutcome> FetchAsync(FileEntry entry, IProgress<string>? log, CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var channel in entry.Channels)
        {
            try
            {
                var tempPath = await FetchOneChannelAsync(entry, channel, log, ct);
                if (tempPath == null) { failures.Add($"{channel.Type}: no bytes produced"); continue; }

                if (!HashUtil.Sha256Matches(tempPath, entry.Sha256))
                {
                    failures.Add($"{channel.Type}: sha256 mismatch after download");
                    TryDelete(tempPath);
                    continue;
                }
                return new DownloadOutcome(true, tempPath, null);
            }
            catch (DriveNoPermissionException ex)
            {

                failures.Add($"{channel.Type}: Drive mirror {ex.FileId} is not publicly downloadable " +
                             "(\"the owner hasn't given you permission\") — next channel tried");
            }
            catch (DriveQuotaException)
            {
                failures.Add($"{channel.Type}: Drive quota exceeded — not retried this run, next channel tried");

            }
            catch (Exception ex)
            {
                failures.Add($"{channel.Type}: {ex.Message}");
            }
        }
        return new DownloadOutcome(false, null, string.Join(" | ", failures));
    }

    private async Task<string?> FetchOneChannelAsync(
        FileEntry entry, Channel channel, IProgress<string>? log, CancellationToken ct)
    {
        switch (channel.Type)
        {
            case "github-release":
            {

                var tag = channel.Tag ?? _release.TagName;
                if (string.IsNullOrEmpty(tag))
                    throw new UpdaterException(
                        "a github-release channel needs a tag (none in the manifest row, and no " +
                        "release tag known for this run)");
                var url = GitHubApi.AssetUrl(tag, channel.AssetName!);
                var ghDest = PartPath(entry, "ghrel-" + tag + "-" + channel.AssetName);
                await DownloadWholeAsync(url, ghDest, BytesProgress, ct);
                return ghDest;
            }

            case "github-asset":
                return await FetchGitHubAssetAsync(entry, channel.AssetName!, ct);

            case "drive":
                return await FetchDriveAsync(entry, channel.FileId!, ct);

            case "archive":
            {

                string archiveUrl;
                string cacheKey;
                if (!string.IsNullOrEmpty(channel.Tag))
                {
                    archiveUrl = GitHubApi.AssetUrl(channel.Tag, channel.AssetName!);
                    cacheKey = "ghrel:" + channel.Tag + ":" + channel.AssetName;
                }
                else
                {
                    var asset = _release.FindAsset(channel.AssetName!)
                        ?? throw new UpdaterException($"release has no asset named '{channel.AssetName}'");
                    archiveUrl = asset.BrowserDownloadUrl;
                    cacheKey = "gh:" + asset.Name;
                }
                var archiveDir = await GetOrExtractArchiveAsync(
                    cacheKey,
                    downloader: (dest, tok) => DownloadWholeAsync(archiveUrl, dest, BytesProgress, tok),
                    ct);
                return LocateExtractedMember(archiveDir, channel.Member!);
            }

            case "drive-archive":
            {
                var archiveDir = await GetOrExtractArchiveAsync(
                    cacheKey: "drive:" + channel.FileId,
                    downloader: async (dest, tok) =>
                    {
                        var r = await _drive.DownloadAsync(channel.FileId!, dest, resumeFromBytes: null, tok);
                        if (r == DriveDownloadResult.QuotaExceeded) throw new DriveQuotaException();
                        if (r == DriveDownloadResult.NoPermission)
                            throw new DriveNoPermissionException(channel.FileId!);
                        if (r != DriveDownloadResult.Ok) throw new UpdaterException("drive-archive fetch failed");
                    },
                    ct);
                return LocateExtractedMember(archiveDir, channel.Member!);
            }

            case "cdn":

                if (string.IsNullOrEmpty(channel.Url)) return null;
                var dest = PartPath(entry, "cdn");
                await DownloadWholeAsync(channel.Url, dest, BytesProgress, ct);
                return dest;

            default:
                throw new UpdaterException($"unknown channel type '{channel.Type}'");
        }
    }

    private async Task<string> FetchGitHubAssetAsync(FileEntry entry, string assetName, CancellationToken ct)
    {
        var asset = _release.FindAsset(assetName)
            ?? throw new UpdaterException($"release has no asset named '{assetName}'");
        var dest = PartPath(entry, "gh-" + assetName);
        await DownloadWholeAsync(asset.BrowserDownloadUrl, dest, BytesProgress, ct);
        return dest;
    }

    private async Task<string> FetchDriveAsync(FileEntry entry, string fileId, CancellationToken ct)
    {
        var dest = PartPath(entry, "drive-" + fileId);
        long? resumeFrom = File.Exists(dest) ? new FileInfo(dest).Length : null;
        var result = await _drive.DownloadAsync(fileId, dest, resumeFrom, ct);
        if (result == DriveDownloadResult.QuotaExceeded) throw new DriveQuotaException();
        if (result == DriveDownloadResult.NoPermission) throw new DriveNoPermissionException(fileId);
        if (result != DriveDownloadResult.Ok) throw new UpdaterException("drive fetch failed");
        return dest;
    }

    private async Task DownloadWholeAsync(string url, string destPath, CancellationToken ct)
    {
        await DownloadWholeAsync(url, destPath, null, ct);
    }

    private async Task DownloadWholeAsync(
        string url, string destPath, IProgress<long>? bytesProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        long resumeFrom = File.Exists(destPath) ? new FileInfo(destPath).Length : 0;

        if (resumeFrom > 0 && !await SupportsRangeAsync(url, ct))
        {
            resumeFrom = 0;
            File.Delete(destPath);
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0)
            req.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        var actuallyResumed = resumeFrom > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
        if (resumeFrom > 0 && !actuallyResumed)
        {

            File.Delete(destPath);
        }
        resp.EnsureSuccessStatusCode();

        var mode = actuallyResumed ? FileMode.Append : FileMode.Create;
        await using var fs = new FileStream(destPath, mode, FileAccess.Write, FileShare.None);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await CopyWithProgressAsync(src, fs, actuallyResumed ? resumeFrom : 0, bytesProgress, ct);
    }

    private static async Task CopyWithProgressAsync(
        Stream src, Stream dst, long alreadyOnDisk, IProgress<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        long total = alreadyOnDisk;
        int read;
        var lastReport = DateTime.UtcNow;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
            if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 250)
            {
                progress.Report(total);
                lastReport = DateTime.UtcNow;
            }
        }
        progress?.Report(total);
    }

    private async Task<bool> SupportsRangeAsync(string url, CancellationToken ct)
    {
        if (_rangeSupportCache.TryGetValue(url, out var cached)) return cached;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.Range = new RangeHeaderValue(0, 0);
            using var resp = await _http.SendAsync(req, ct);
            var supported = resp.StatusCode == HttpStatusCode.PartialContent
                             || (resp.Headers.AcceptRanges?.Contains("bytes") ?? false);
            _rangeSupportCache[url] = supported;
            return supported;
        }
        catch
        {
            _rangeSupportCache[url] = false;
            return false;
        }
    }

    private async Task<string> GetOrExtractArchiveAsync(
        string cacheKey, Func<string, CancellationToken, Task> downloader, CancellationToken ct)
    {
        if (_extractedArchiveCache.TryGetValue(cacheKey, out var cachedDir) && Directory.Exists(cachedDir))
            return cachedDir;

        var zipPath = Path.Combine(_runTempDir, "archives", SafeName(cacheKey) + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        if (!File.Exists(zipPath))
            await downloader(zipPath, ct);

        var extractDir = Path.Combine(_runTempDir, "extracted", SafeName(cacheKey));
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
        Directory.CreateDirectory(extractDir);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, extractDir);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {

            TryDelete(zipPath);
            try { Directory.Delete(extractDir, recursive: true); } catch (IOException) { }
            Directory.CreateDirectory(extractDir);
            await downloader(zipPath, ct);
            ZipFile.ExtractToDirectory(zipPath, extractDir);
        }

        _extractedArchiveCache[cacheKey] = extractDir;
        return extractDir;
    }

    private static string? LocateExtractedMember(string extractDir, string member)
    {
        var path = Path.Combine(extractDir, member.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }

    private string PartPath(FileEntry entry, string channelTag) =>
        Path.Combine(_runTempDir, SafeName(entry.Key) + "." + SafeName(channelTag) + ".part");

    private static string SafeName(string relpath) =>
        string.Concat(relpath.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static void TryDelete(string path) { try { File.Delete(path); } catch {  } }
}

public sealed class DriveQuotaException : Exception { }

public sealed class DriveNoPermissionException : Exception
{
    public string FileId { get; }
    public DriveNoPermissionException(string fileId) : base($"drive file {fileId} is not shared publicly")
        => FileId = fileId;
}
