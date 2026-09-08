using System.Text.Json;

namespace ZDUpdater;

public static class Program
{
    private const string ManifestAssetName = "manifest.json";
    private static readonly string CarriageReturn = new string((char)13, 1);

    public static async Task<int> Main(string[] args)
    {
        var report = !args.Contains("--elevated-apply");
        if (report) SupportReport.Start(args);
        var code = 1;
        try
        {
            code = await MainInner(args);
            return code;
        }
        finally
        {
            if (report) SupportReport.Finish(code, code == 0 ? "completed" : "finished with errors");
        }
    }

    private static async Task<int> MainInner(string[] args)
    {
        try
        {

            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }

            using var wizardHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            if (args.Length == 0)
                return await Interactive.RunWizardAsync(wizardHttp);

            var opts = CliOptions.Parse(args);

            if (opts.ElevatedApplyJobPath != null)
                return await Elevation.RunAsElevatedChildAsync(
                    opts.ElevatedApplyJobPath, opts.AllowRoot, opts.AllowTemp);

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(10);

            if (!opts.DryRun && opts.Command is Command.Install or Command.Update or Command.Uninstall && Interactive.GameRunning())
            {
                Console.Error.WriteLine("Plutonium is running. Close the game and the launcher, then run this again.");
                return 3;
            }

            return opts.Command switch
            {
                Command.Install => await RunInstallOrUpdateAsync(opts, http, isUpdate: false),
                Command.Update => await RunInstallOrUpdateAsync(opts, http, isUpdate: true),
                Command.Verify => await RunVerifyAsync(opts),
                Command.Uninstall => await RunUninstallAsync(opts),
                Command.Paths => RunPaths(opts),
                _ => PrintUsageAndFail(),
            };
        }
        catch (UpdaterException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine($"manifest error: {ex.Message}");
            return 1;
        }
        catch (System.Text.Json.JsonException ex)
        {

            Console.Error.WriteLine($"error: unreadable JSON ({ex.Message})");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine(
                $"network error: {ex.Message} - nothing was changed on disk by this run.");
            return 1;
        }
        catch (TaskCanceledException)
        {
            Console.Error.WriteLine(
                "network timeout - nothing was changed on disk by this run. " +
                "Re-running resumes any partially downloaded files.");
            return 1;
        }
    }

    private static int PrintUsageAndFail()
    {
        Console.Error.WriteLine(
            "usage: ZombiesDeclassified-Updater.exe <install|update|verify|uninstall|paths> " +
            "[--dry-run] [--bo2-path <dir>] [--pluto-path <dir>] [--manifest <path|url>] [--force]");
        return 2;
    }

    internal static async Task<int> RunInstallOrUpdateAsync(CliOptions opts, HttpClient http, bool isUpdate)
    {
        GitHubRelease release;
        string manifestJson;
        if (opts.ManifestOverride != null)
        {

            release = new GitHubRelease();
            manifestJson = opts.ManifestOverride.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || opts.ManifestOverride.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? await http.GetStringAsync(opts.ManifestOverride)
                : await File.ReadAllTextAsync(opts.ManifestOverride);
            Console.WriteLine($"(using explicit manifest: {opts.ManifestOverride})");
        }
        else
        {
            var gh = new GitHubApi(http);
            release = await gh.GetLatestReleaseAsync(default);
            var manifestAsset = release.FindAsset(ManifestAssetName)
                ?? throw new UpdaterException(
                    $"latest release '{release.TagName}' has no {ManifestAssetName} asset");
            manifestJson = await http.GetStringAsync(manifestAsset.BrowserDownloadUrl);
            await MaybeReportSelfUpdateAsync(release, http);
        }
        var manifest = Manifest.Parse(manifestJson);

        var priorState = LocalState.Load();
        if (isUpdate && priorState == null)
            throw new UpdaterException("no prior install found (state.json missing) — run 'install' first");

        var plutoRoot = opts.PlutoPathOverride ?? PathDetection.ResolvePlutoT6();
        var needsBo2 = manifest.Files.Any(f => f.NeedsElevation);
        string? bo2Root = opts.Bo2PathOverride ?? priorState?.Bo2InstallDir ?? PathDetection.DetectBo2InstallDir();
        if (needsBo2 && bo2Root == null)
        {
            Console.Error.WriteLine(
                "Could not auto-detect the Black Ops II install directory " +
                "(registry InstallPath + libraryfolders.vdf scan found no appmanifest_202990.acf).");
            Console.Error.WriteLine("Re-run with --bo2-path \"<...>\\Call of Duty Black Ops II\"");
            return 1;
        }
        var roots = new ResolvedRoots { PlutoT6 = plutoRoot, Bo2 = bo2Root };

        var diff = InstallPlanner.BuildDiff(manifest, priorState, roots);
        PrintPlan(diff);

        if (opts.DryRun)
        {
            Console.WriteLine("(--dry-run: no files were downloaded, placed, or backed up.)");
            PrintBanner(manifest);
            return 0;
        }

        var toFetch = diff.Where(r => r.Action is RowAction.New or RowAction.Changed).ToList();
        var toRemove = diff.Where(r => r.Action == RowAction.Removed).ToList();
        var driftedForeign = diff.Where(r => r.Action == RowAction.ForeignDrift).ToList();

        if (driftedForeign.Count > 0 && !opts.Force)
        {
            Console.WriteLine(
                $"{driftedForeign.Count} file(s) on disk do not match our last-known install AND do not " +
                "match the new release (something else changed them). Skipping those rows — pass --force " +
                "to back them up under backups\\foreign\\ and overwrite anyway.");
            toFetch = toFetch.Where(r => !driftedForeign.Contains(r)).ToList();
        }
        else if (opts.Force)
        {
            toFetch.AddRange(driftedForeign);
        }

        var runTempDir = Path.Combine(Path.GetTempPath(), "ZDUpdater", Guid.NewGuid().ToString("N"));
        var downloader = new ChannelDownloader(http, release, runTempDir);

        int fileIndex = 0;
        if (toFetch.Count > 0)
        {
            var totalMb = toFetch.Sum(r => r.Entry.Size) / 1048576.0;
            Console.WriteLine();
            Console.WriteLine($"Downloading {toFetch.Count} file(s), {totalMb:F0} MB total:");
        }
        var placedNonElevated = new List<(FileEntry entry, string tempPath)>();
        var elevatedItems = new List<ElevatedJobItem>();
        var failed = new List<(FileEntry entry, string reason)>();

        foreach (var row in toFetch)
        {

            if (row.Action == RowAction.Changed || row.Action == RowAction.ForeignDrift)
                InstallPlanner.BackupIfPresent(row, roots, priorState?.ReleaseTag ?? "pre-install");

            fileIndex++;
            var sizeMb = row.Entry.Size / 1048576.0;
            Console.Write($"  [{fileIndex}/{toFetch.Count}] {row.Entry.RelPath} ({sizeMb:F1} MB) ");
            var lastPct = -1;
            downloader.BytesProgress = new Progress<long>(done =>
            {
                if (row.Entry.Size <= 0) return;
                var pct = (int)(100.0 * done / row.Entry.Size);
                if (pct == lastPct || pct > 100) return;
                lastPct = pct;
                Console.Write($"{CarriageReturn}  [{fileIndex}/{toFetch.Count}] {row.Entry.RelPath} " +
                              $"({sizeMb:F1} MB) {pct,3}% ");
            });

            var outcome = await downloader.FetchAsync(row.Entry, log: null, default);
            downloader.BytesProgress = null;
            Console.WriteLine(outcome.Success ? "ok " : "FAILED ");
            if (!outcome.Success)
            {
                failed.Add((row.Entry, outcome.FailureReason ?? "unknown failure"));
                continue;
            }

            if (row.Entry.NeedsElevation)
            {
                var destPath = Path.Combine(roots.Resolve(row.Entry.Root)!, row.Entry.RelPath);
                elevatedItems.Add(new ElevatedJobItem
                {
                    TempPath = outcome.PlacedTempPath!,
                    DestPath = destPath,
                    Sha256 = row.Entry.Sha256,
                });
            }
            else
            {
                placedNonElevated.Add((row.Entry, outcome.PlacedTempPath!));
            }
        }

        var placedOk = new List<FileEntry>();
        foreach (var (entry, tempPath) in placedNonElevated)
        {
            var destPath = Path.Combine(roots.Resolve(entry.Root)!, entry.RelPath);
            HashUtil.AtomicPlace(tempPath, destPath);
            if (HashUtil.Sha256Matches(destPath, entry.Sha256))
                placedOk.Add(entry);
            else
                failed.Add((entry, "post-move hash mismatch"));
        }

        if (elevatedItems.Count > 0)
        {
            Console.WriteLine(
                $"{elevatedItems.Count} file(s) need to be placed under the Black Ops II folder " +
                "(elevation required) — a UAC prompt will appear.");
            var exePath = Environment.ProcessPath
                ?? throw new UpdaterException("could not determine this exe's own path for elevation relaunch");
            var results = await Elevation.RunElevatedPlacementAsync(
                exePath, elevatedItems, runTempDir, bo2Root!, default);
            foreach (var r in results)
            {
                var entry = manifest.Files.First(f =>
                    Path.Combine(roots.Resolve(f.Root)!, f.RelPath) == r.DestPath);
                if (r.Ok) placedOk.Add(entry);
                else failed.Add((entry, r.Error ?? "elevated placement failed"));
            }
        }

        int removedCount = 0;
        foreach (var row in toRemove)
        {
            InstallPlanner.BackupIfPresent(row, roots, row.Legacy ? "legacy" : (priorState?.ReleaseTag ?? "pre-install"));
            var path = Path.Combine(roots.Resolve(row.Entry.Root) ?? "", row.Entry.RelPath);
            if (File.Exists(path)) { File.Delete(path); removedCount++; }
        }

        var newState = new LocalState
        {
            ReleaseTag = manifest.ReleaseTag,
            InstalledUtc = DateTime.UtcNow.ToString("o"),
            Bo2InstallDir = bo2Root,
            Manifest = manifest,
        };
        newState.Save();

        Console.WriteLine();
        Console.WriteLine($"Placed: {placedOk.Count}  Failed: {failed.Count}  Removed: {removedCount}  " +
                           $"Unchanged: {diff.Count(r => r.Action == RowAction.Unchanged)}");
        foreach (var (entry, reason) in failed)
            Console.WriteLine($"  FAILED {entry.RelPath}: {reason}");
        PrintBanner(manifest);

        try { Directory.Delete(runTempDir, recursive: true); } catch {  }

        return failed.Count > 0 ? 1 : 0;
    }

    private static int RunPaths(CliOptions opts)
    {
        var pluto = opts.PlutoPathOverride ?? PathDetection.ResolvePlutoT6();
        var detected = PathDetection.DetectBo2InstallDir();
        var bo2 = opts.Bo2PathOverride ?? detected;
        Console.WriteLine($"plutonium-storage : {pluto}");
        Console.WriteLine($"  exists          : {Directory.Exists(pluto)}");
        Console.WriteLine($"black-ops-2       : {detected ?? "(not detected)"}");
        if (opts.Bo2PathOverride != null)
            Console.WriteLine($"  override        : {opts.Bo2PathOverride}");
        Console.WriteLine($"  usable          : {bo2 != null && PathDetection.LooksLikeBo2(bo2)}");
        var state = SafeState();
        Console.WriteLine($"installed-release : {state?.ReleaseTag ?? "(nothing installed)"}");
        Console.WriteLine($"install-record    : {LocalState.StatePath()}");
        return bo2 != null && PathDetection.LooksLikeBo2(bo2) ? 0 : 1;
    }

    private static LocalState? SafeState()
    {
        try { return LocalState.Load(); } catch (UpdaterException) { return null; }
    }

    internal static Task<int> RunVerifyAsync(CliOptions opts)
    {
        var state = LocalState.Load()
            ?? throw new UpdaterException("no local install found (state.json missing)");
        var manifest = state.Manifest ?? throw new UpdaterException("local state has no manifest recorded");
        var roots = new ResolvedRoots
        {
            PlutoT6 = opts.PlutoPathOverride ?? PathDetection.ResolvePlutoT6(),
            Bo2 = opts.Bo2PathOverride ?? state.Bo2InstallDir,
        };

        int match = 0, missing = 0, drifted = 0;
        foreach (var entry in manifest.Files)
        {
            var root = roots.Resolve(entry.Root);
            var path = root == null ? null : Path.Combine(root, entry.RelPath);
            if (path == null || !File.Exists(path))
            {
                Console.WriteLine($"  MISSING  {entry.RelPath}");
                missing++;
                continue;
            }
            var sha = HashUtil.Sha256File(path);
            if (sha.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) { match++; continue; }
            Console.WriteLine($"  DRIFTED  {entry.RelPath}");
            drifted++;
        }

        Console.WriteLine();
        Console.WriteLine($"Match: {match}  Missing: {missing}  Drifted: {drifted}");
        PrintBanner(manifest);
        return Task.FromResult((missing == 0 && drifted == 0) ? 0 : 1);
    }

    internal static async Task<int> RunUninstallAsync(CliOptions opts)
    {
        var state = LocalState.Load()
            ?? throw new UpdaterException("no local install found (state.json missing) — nothing to uninstall");
        var roots = new ResolvedRoots
        {
            PlutoT6 = opts.PlutoPathOverride ?? PathDetection.ResolvePlutoT6(),
            Bo2 = opts.Bo2PathOverride ?? state.Bo2InstallDir,
        };

        if (opts.DryRun)
        {
            Console.WriteLine($"(--dry-run) Would remove up to {state.Manifest?.Files.Count ?? 0} manifest-listed " +
                               "files (skipping any that have drifted from our recorded hash) and restore any " +
                               "pre-existing files recorded in the foreign backup tree. Our own previous-version " +
                               "backups are never restored by uninstall.");
            return 0;
        }

        var (deleted, skippedDrifted, restoredForeign) = InstallPlanner.Uninstall(state, roots);
        Console.WriteLine(
            $"Uninstalled: {deleted} file(s) removed, {skippedDrifted} skipped (drifted since install), " +
            $"{restoredForeign} pre-existing file(s) restored from the foreign backup tree.");

        File.Delete(LocalState.StatePath());
        await Task.CompletedTask;
        return 0;
    }

    private static async Task MaybeReportSelfUpdateAsync(GitHubRelease release, HttpClient http)
    {
        var versionAsset = release.FindAsset("updater-version.json");
        if (versionAsset == null) return;
        try
        {
            var json = await http.GetStringAsync(versionAsset.BrowserDownloadUrl);
            using var doc = JsonDocument.Parse(json);
            var latestVersion = doc.RootElement.GetProperty("version").GetString();
            var running = Interactive.Version();
            if (latestVersion != null && Version.TryParse(latestVersion, out var latest) && Version.TryParse(running, out var mine) && latest > mine)
            {
                Console.WriteLine(
                    $"A newer installer ({latestVersion}) is available; this is {running}. " +
                    "Download ZombiesDeclassified-Updater.exe from the releases page for next time.");
            }
        }
        catch
        {

        }
    }

    private static void PrintPlan(List<DiffRow> diff)
    {
        var byAction = diff.GroupBy(r => r.Action).ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine("Plan:");
        foreach (var action in Enum.GetValues<RowAction>())
            if (byAction.TryGetValue(action, out var n) && n > 0)
                Console.WriteLine($"  {action,-13} {n}");
        var legacy = diff.Count(r => r.Legacy);
        if (legacy > 0)
            Console.WriteLine($"  (of the Removed rows, {legacy} are files an earlier package placed - backed up under backups\\legacy\\)");
    }

    private static void PrintBanner(Manifest manifest)
    {
        Console.WriteLine();
        Console.WriteLine($"Zombies Declassified pack: {manifest.ReleaseTag} ({manifest.ShortHash})");
    }
}

public enum Command { Install, Update, Verify, Uninstall, Paths }

public sealed class CliOptions
{
    public Command Command { get; private set; }
    public bool DryRun { get; private set; }
    public bool Force { get; private set; }
    public string? Bo2PathOverride { get; private set; }
    public string? PlutoPathOverride { get; private set; }
    public string? ManifestOverride { get; private set; }
    public string? ElevatedApplyJobPath { get; private set; }
    public string? AllowRoot { get; private set; }
    public string? AllowTemp { get; private set; }

    public static CliOptions ForWizard(Command command, string bo2Path) =>
        new() { Command = command, Bo2PathOverride = bo2Path };

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        if (args.Length == 0)
            throw new UpdaterException("no command given (install|update|verify|uninstall|paths)");

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "install": o.Command = Command.Install; break;
                case "update": o.Command = Command.Update; break;
                case "verify": o.Command = Command.Verify; break;
                case "uninstall": o.Command = Command.Uninstall; break;
                case "paths": o.Command = Command.Paths; break;
                case "--dry-run": o.DryRun = true; break;
                case "--force": o.Force = true; break;
                case "--bo2-path":
                    o.Bo2PathOverride = RequireValue(args, ++i, "--bo2-path");
                    break;
                case "--pluto-path":
                    o.PlutoPathOverride = RequireValue(args, ++i, "--pluto-path");
                    break;
                case "--manifest":
                    o.ManifestOverride = RequireValue(args, ++i, "--manifest");
                    break;
                case "--elevated-apply":
                    o.ElevatedApplyJobPath = RequireValue(args, ++i, "--elevated-apply");
                    break;
                case "--allow-root":
                    o.AllowRoot = RequireValue(args, ++i, "--allow-root");
                    break;
                case "--allow-temp":
                    o.AllowTemp = RequireValue(args, ++i, "--allow-temp");
                    break;
                default:
                    throw new UpdaterException($"unrecognized argument '{args[i]}'");
            }
        }
        return o;
    }

    private static string RequireValue(string[] args, int i, string flag) =>
        i < args.Length ? args[i] : throw new UpdaterException($"{flag} requires a value");
}
