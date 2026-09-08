namespace ZDUpdater;

public sealed class ResolvedRoots
{
    public required string PlutoT6 { get; init; }
    public string? Bo2 { get; init; }

    public string? Resolve(string rootName) => rootName switch
    {
        "PLUTO_T6" => PlutoT6,
        "BO2" => Bo2,
        _ => null,
    };
}

public static class InstallPlanner
{

    public static List<DiffRow> BuildDiff(Manifest newManifest, LocalState? priorState, ResolvedRoots roots)
    {
        var priorByKey = priorState?.Manifest?.ByKey() ?? new Dictionary<string, FileEntry>();
        var rows = new List<DiffRow>();

        foreach (var entry in newManifest.Files)
        {
            var root = roots.Resolve(entry.Root);
            if (root == null)
            {

                rows.Add(new DiffRow { Entry = entry, Action = RowAction.New });
                continue;
            }

            var destPath = Path.Combine(root, entry.RelPath);
            string? onDiskSha = File.Exists(destPath) ? HashUtil.Sha256File(destPath) : null;
            var hadPrior = priorByKey.TryGetValue(entry.Key, out var priorEntry);

            RowAction action;
            if (onDiskSha == null)
            {
                action = RowAction.New;
            }
            else if (onDiskSha.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                action = RowAction.Unchanged;
            }
            else if (hadPrior && onDiskSha.Equals(priorEntry!.Sha256, StringComparison.OrdinalIgnoreCase))
            {

                action = RowAction.Changed;
            }
            else if (entry.PriorSha256.Any(h => h.Equals(onDiskSha, StringComparison.OrdinalIgnoreCase)))
            {

                action = RowAction.Changed;
            }
            else
            {

                action = RowAction.ForeignDrift;
            }

            rows.Add(new DiffRow { Entry = entry, Action = action, OnDiskSha256 = onDiskSha });
        }

        var newKeys = newManifest.Files.Select(f => f.Key).ToHashSet();
        foreach (var (key, oldEntry) in priorByKey)
        {
            if (!newKeys.Contains(key))
                rows.Add(new DiffRow { Entry = oldEntry, Action = RowAction.Removed });
        }

        var seen = rows.Select(r => r.Entry.Key).ToHashSet();
        foreach (var r in newManifest.Retire)
        {
            if (seen.Contains(r.Key)) continue;
            var root = roots.Resolve(r.Root);
            if (root == null) continue;
            if (!File.Exists(Path.Combine(root, r.RelPath))) continue;
            rows.Add(new DiffRow
            {
                Entry = new FileEntry { RelPath = r.RelPath, Root = r.Root },
                Action = RowAction.Removed,
                Legacy = true,
            });
        }

        return rows;
    }

    public static void BackupIfPresent(DiffRow row, ResolvedRoots roots, string tagForBackupDir)
    {
        var root = roots.Resolve(row.Entry.Root);
        if (root == null) return;
        var srcPath = Path.Combine(root, row.Entry.RelPath);
        if (!File.Exists(srcPath)) return;

        var dirTag = row.Action == RowAction.ForeignDrift ? "foreign" : tagForBackupDir;
        var backupPath = Path.Combine(LocalState.BackupsDir(), dirTag, row.Entry.RelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Copy(srcPath, backupPath, overwrite: true);
    }

    public static (int deleted, int skippedDrifted, int restoredForeign) Uninstall(
        LocalState state, ResolvedRoots roots)
    {
        if (state.Manifest == null) return (0, 0, 0);
        int deleted = 0, skipped = 0;
        var touchedDirs = new HashSet<string>();

        foreach (var entry in state.Manifest.Files)
        {
            var root = roots.Resolve(entry.Root);
            if (root == null) continue;
            var path = Path.Combine(root, entry.RelPath);
            if (!File.Exists(path)) continue;

            var onDisk = HashUtil.Sha256File(path);
            if (!onDisk.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }
            File.Delete(path);
            deleted++;
            var dir = Path.GetDirectoryName(path);
            if (dir != null) touchedDirs.Add(dir);
        }

        foreach (var dir in touchedDirs.OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (IOException) {  }
        }

        var foreignBackup = Path.Combine(LocalState.BackupsDir(), "foreign");
        int restored = 0;
        if (Directory.Exists(foreignBackup))
            restored = RestoreBackupTree(foreignBackup, roots, state.Manifest);

        return (deleted, skipped, restored);
    }

    private static int RestoreBackupTree(string backupDir, ResolvedRoots roots, Manifest manifest)
    {
        int restored = 0;

        var byRelpath = manifest.Files.ToLookup(f => f.RelPath.Replace('/', '\\'));
        foreach (var file in Directory.EnumerateFiles(backupDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(backupDir, file);
            var entry = byRelpath[rel].FirstOrDefault();
            if (entry == null) continue;
            var root = roots.Resolve(entry.Root);
            if (root == null) continue;
            var dest = Path.Combine(root, entry.RelPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            restored++;
        }
        return restored;
    }
}
