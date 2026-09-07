using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ZDUpdater;

public enum DriveDownloadResult { Ok, QuotaExceeded, NoPermission, Failed }

public sealed class DriveClient
{
    private readonly HttpClient _http;

    private const int MaxTextPeekBytes = 256 * 1024;

    private static readonly string[] QuotaMarkers =
    {
        "Google Drive - Quota exceeded",
        "Too many users have viewed or downloaded this file recently",
        "download quota for this file has been exceeded",
    };

    private static readonly string[] NoPermissionMarkers =
    {
        "hasn&#39;t given you permission",
        "hasn't given you permission",
        "Can&#39;t download file",
    };

    public DriveClient(HttpClient http) => _http = http;

    public async Task<DriveDownloadResult> DownloadAsync(
        string fileId, string destTempPath, long? resumeFromBytes, CancellationToken ct)
    {

        var interstitialUrl =
            $"https://drive.usercontent.google.com/download?id={Uri.EscapeDataString(fileId)}&export=download";

        using var probeResp = await _http.GetAsync(
            interstitialUrl, HttpCompletionOption.ResponseHeadersRead, ct);

        if (probeResp.Content.Headers.ContentDisposition != null)
            return await StreamToFileAsync(probeResp, destTempPath, resumeFromBytes, ct);

        var html = await PeekTextAsync(probeResp, ct);
        if (QuotaMarkers.Any(m => html.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return DriveDownloadResult.QuotaExceeded;
        if (NoPermissionMarkers.Any(m => html.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return DriveDownloadResult.NoPermission;

        var confirmedUrl = BuildConfirmedUrl(html, fileId);
        if (confirmedUrl == null)
            return DriveDownloadResult.Failed;

        using var req2 = new HttpRequestMessage(HttpMethod.Get, confirmedUrl);
        if (resumeFromBytes is > 0)
            req2.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFromBytes, null);

        using var resp2 = await _http.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, ct);

        if (resp2.Content.Headers.ContentDisposition == null)
        {
            var html2 = await PeekTextAsync(resp2, ct);
            if (QuotaMarkers.Any(m => html2.Contains(m, StringComparison.OrdinalIgnoreCase)))
                return DriveDownloadResult.QuotaExceeded;
            if (NoPermissionMarkers.Any(m => html2.Contains(m, StringComparison.OrdinalIgnoreCase)))
                return DriveDownloadResult.NoPermission;
            return DriveDownloadResult.Failed;
        }

        return await StreamToFileAsync(resp2, destTempPath, resumeFromBytes, ct);
    }

    internal static string? BuildConfirmedUrl(string html, string fileId)
    {
        var action = Regex.Match(html, @"<form[^>]*\baction=""([^""]+)""", RegexOptions.IgnoreCase);
        var actionUrl = action.Success
            ? WebUtility.HtmlDecode(action.Groups[1].Value)
            : "https://drive.usercontent.google.com/download";

        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match m in Regex.Matches(
                     html,
                     @"<input[^>]*\btype=""hidden""[^>]*\bname=""([^""]+)""[^>]*\bvalue=""([^""]*)""",
                     RegexOptions.IgnoreCase))
        {
            fields.Add(new KeyValuePair<string, string>(
                WebUtility.HtmlDecode(m.Groups[1].Value),
                WebUtility.HtmlDecode(m.Groups[2].Value)));
        }

        if (!fields.Any(f => f.Key.Equals("confirm", StringComparison.OrdinalIgnoreCase)))
            return null;
        if (!fields.Any(f => f.Key.Equals("id", StringComparison.OrdinalIgnoreCase)))
            fields.Add(new KeyValuePair<string, string>("id", fileId));

        var query = new StringBuilder();
        foreach (var f in fields)
        {
            query.Append(query.Length == 0 ? '?' : '&');
            query.Append(Uri.EscapeDataString(f.Key)).Append('=').Append(Uri.EscapeDataString(f.Value));
        }
        return actionUrl + query;
    }

    private static async Task<string> PeekTextAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxTextPeekBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0) break;
            total += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private async Task<DriveDownloadResult> StreamToFileAsync(
        HttpResponseMessage resp, string destTempPath, long? resumeFromBytes, CancellationToken ct)
    {
        resp.EnsureSuccessStatusCode();

        var didResume = resumeFromBytes is > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
        if (resumeFromBytes is > 0 && !didResume)
        {

            try { File.Delete(destTempPath); } catch (IOException) {  }
        }
        var mode = didResume ? FileMode.Append : FileMode.Create;
        Directory.CreateDirectory(Path.GetDirectoryName(destTempPath)!);
        await using var fs = new FileStream(destTempPath, mode, FileAccess.Write, FileShare.None);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await src.CopyToAsync(fs, ct);
        return DriveDownloadResult.Ok;
    }
}
