using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZDUpdater;

public sealed class ElevatedJobItem
{
    [JsonPropertyName("tempPath")] public string TempPath { get; set; } = "";
    [JsonPropertyName("destPath")] public string DestPath { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

public sealed class ElevatedJob
{
    [JsonPropertyName("items")] public List<ElevatedJobItem> Items { get; set; } = new();
    [JsonPropertyName("resultPath")] public string ResultPath { get; set; } = "";
}

public sealed class ElevatedResultItem
{
    [JsonPropertyName("destPath")] public string DestPath { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public static class Elevation
{

    private static readonly string[] AllowedDestExtensions = { ".ipak", ".sabl", ".sabs", ".ff" };

    public static async Task<List<ElevatedResultItem>> RunElevatedPlacementAsync(
        string exePath, List<ElevatedJobItem> items, string runTempDir, string bo2Root,
        CancellationToken ct)
    {
        if (items.Count == 0) return new List<ElevatedResultItem>();

        var jobPath = Path.Combine(runTempDir, "elevated-job.json");
        var resultPath = Path.Combine(runTempDir, "elevated-result.json");
        var job = new ElevatedJob { Items = items, ResultPath = resultPath };
        await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job), ct);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,

            Arguments = $"--elevated-apply \"{jobPath}\" --allow-root \"{bo2Root}\" --allow-temp \"{runTempDir}\"",
            UseShellExecute = true,
            Verb = "runas",
        };

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {

            throw new UpdaterException(
                "Elevation was declined — the Steam-side files (ipaks/sound) were NOT placed. " +
                "Re-run and accept the UAC prompt to complete the BO2-side install.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdaterException(
                "The elevated placement step did not finish within 30 minutes — " +
                "treat the BO2-side files as NOT confirmed placed and re-run.");
        }

        if (!File.Exists(resultPath))
            throw new UpdaterException(
                $"Elevated placement process exited (code {proc.ExitCode}) without writing a result file — " +
                "treat the BO2-side files as NOT confirmed placed and re-run.");

        var resultJson = await File.ReadAllTextAsync(resultPath, ct);
        try
        {
            return JsonSerializer.Deserialize<List<ElevatedResultItem>>(resultJson) ?? new();
        }
        catch (JsonException ex)
        {
            throw new UpdaterException($"elevated result file was not readable JSON: {ex.Message}");
        }
    }

    public static async Task<int> RunAsElevatedChildAsync(string jobPath, string? allowRoot, string? allowTemp)
    {
        if (string.IsNullOrWhiteSpace(allowRoot) || string.IsNullOrWhiteSpace(allowTemp))
            throw new UpdaterException(
                "--elevated-apply requires --allow-root and --allow-temp (refusing to place files " +
                "without an explicit destination boundary)");

        var root = ValidateBo2Root(allowRoot);
        var tempRoot = CanonicalDir(allowTemp);
        if (!IsUnder(tempRoot, CanonicalDir(Path.GetTempPath())))
            throw new UpdaterException("staging directory is not under the user temp directory — refusing");

        ElevatedJob job;
        try
        {
            job = JsonSerializer.Deserialize<ElevatedJob>(await File.ReadAllTextAsync(jobPath))
                  ?? throw new UpdaterException("empty elevated job file");
        }
        catch (JsonException ex)
        {
            throw new UpdaterException($"elevated job file was not readable JSON: {ex.Message}");
        }

        var resultPath = Path.Combine(tempRoot, "elevated-result.json");

        var results = new List<ElevatedResultItem>();
        foreach (var item in job.Items)
        {
            try
            {
                var dest = ValidateDestination(item.DestPath, root);
                var src = ValidateSource(item.TempPath, tempRoot);

                if (!HashUtil.Sha256Matches(src, item.Sha256))
                    throw new UpdaterException("source hash no longer matches — refusing to place");

                HashUtil.AtomicPlace(src, dest);

                if (!HashUtil.Sha256Matches(dest, item.Sha256))
                    throw new UpdaterException("post-move hash mismatch at destination");

                results.Add(new ElevatedResultItem { DestPath = dest, Ok = true });
            }
            catch (Exception ex)
            {
                results.Add(new ElevatedResultItem { DestPath = item.DestPath, Ok = false, Error = ex.Message });
            }
        }

        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(results));
        return results.All(r => r.Ok) ? 0 : 1;
    }

    private static string ValidateBo2Root(string candidate)
    {
        var root = CanonicalDir(candidate);
        if (!Directory.Exists(root))
            throw new UpdaterException($"destination root does not exist: {root}");
        if (HasReparsePoint(root))
            throw new UpdaterException("destination root is a reparse point — refusing");

        var looksLikeBo2 =
            Directory.Exists(Path.Combine(root, "zone")) &&
            Directory.Exists(Path.Combine(root, "sound"));
        if (!looksLikeBo2)
            throw new UpdaterException(
                $"destination root does not look like a Black Ops II install (no zone\\ + sound\\): {root}");

        var detected = SafeDetect();
        if (detected != null && !PathsEqual(CanonicalDir(detected), root))
            Console.Error.WriteLine(
                $"note: placing into '{root}', which is not the auto-detected install '{detected}'.");

        return root;
    }

    private static string? SafeDetect()
    {
        try { return PathDetection.DetectBo2InstallDir(); }
        catch { return null; }
    }

    private static string ValidateDestination(string destPath, string root)
    {
        if (string.IsNullOrWhiteSpace(destPath))
            throw new UpdaterException("empty destination path");

        var full = Path.GetFullPath(destPath);
        if (!IsUnder(full, root))
            throw new UpdaterException($"destination is outside the Black Ops II folder — refusing: {full}");

        var ext = Path.GetExtension(full);
        if (!AllowedDestExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw new UpdaterException(
                $"destination extension '{ext}' is not placeable through the elevated step " +
                $"(allowed: {string.Join(", ", AllowedDestExtensions)})");

        var dir = Path.GetDirectoryName(full)
                  ?? throw new UpdaterException($"destination has no directory component: {full}");
        for (var d = dir; d != null && IsUnder(d, root) && !PathsEqual(d, root); d = Path.GetDirectoryName(d))
            if (Directory.Exists(d) && HasReparsePoint(d))
                throw new UpdaterException($"destination path crosses a reparse point — refusing: {d}");

        if (File.Exists(full) && HasReparsePoint(full))
            throw new UpdaterException($"destination file is a reparse point — refusing: {full}");

        return full;
    }

    private static string ValidateSource(string tempPath, string tempRoot)
    {
        if (string.IsNullOrWhiteSpace(tempPath))
            throw new UpdaterException("empty source path");
        var full = Path.GetFullPath(tempPath);
        if (!IsUnder(full, tempRoot))
            throw new UpdaterException($"source file is outside this run's staging directory — refusing: {full}");
        if (!File.Exists(full))
            throw new UpdaterException($"source file missing: {full}");
        if (HasReparsePoint(full))
            throw new UpdaterException($"source file is a reparse point — refusing: {full}");
        return full;
    }

    private static bool HasReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static string CanonicalDir(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsEqual(string a, string b) =>
        string.Equals(CanonicalDir(a), CanonicalDir(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string root)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase)) return true;
        return p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
