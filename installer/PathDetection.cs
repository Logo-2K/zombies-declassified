using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace ZDUpdater;

public sealed class DetectedPaths
{
    public required string PlutoT6 { get; init; }
    public string? Bo2InstallDir { get; init; }
}

public static class PathDetection
{

    private static readonly int[] Bo2AppIds = { 202970, 212910, 202990 };

    private const string WellKnownInstallDir = "Call of Duty Black Ops II";

    public static string ResolvePlutoT6()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Plutonium", "storage", "t6");
    }

    public static string? DetectBo2InstallDir()
    {
        foreach (var lib in EnumerateSteamLibraries())
        {
            var steamapps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(steamapps)) continue;

            foreach (var appId in Bo2AppIds)
            {
                var acf = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
                if (!File.Exists(acf)) continue;
                var installDir = ReadInstallDir(acf);
                if (installDir == null) continue;
                var candidate = Path.Combine(steamapps, "common", installDir);
                if (LooksLikeBo2(candidate)) return candidate;
            }

            var wellKnown = Path.Combine(steamapps, "common", WellKnownInstallDir);
            if (LooksLikeBo2(wellKnown)) return wellKnown;
        }
        return null;
    }

    public static bool LooksLikeBo2(string dir) =>
        Directory.Exists(dir) &&
        Directory.Exists(Path.Combine(dir, "zone")) &&
        Directory.Exists(Path.Combine(dir, "sound"));

    private static IEnumerable<string> EnumerateSteamLibraries()
    {
        var seen = new List<string>();
        var steamInstallPath = ReadSteamInstallPath();
        if (steamInstallPath != null)
        {
            seen.Add(steamInstallPath);
            yield return steamInstallPath;

            var vdfPath = Path.Combine(steamInstallPath, "steamapps", "libraryfolders.vdf");
            List<string> extra = new();
            if (File.Exists(vdfPath))
            {
                try { extra = VdfParser.FindLibraryPaths(File.ReadAllText(vdfPath)); }
                catch (IOException) {  }
            }
            foreach (var p in extra)
            {
                if (seen.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
                seen.Add(p);
                yield return p;
            }
        }
    }

    private static string? ReadInstallDir(string acfPath)
    {
        try
        {
            var text = File.ReadAllText(acfPath);
            var m = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!m.Success) return null;

            return m.Groups[1].Value.Replace("\\\\", "\\");
        }
        catch (IOException) { return null; }
    }

    private static string? ReadSteamInstallPath()
    {

        var candidates = new (RegistryHive hive, RegistryView view, string key, string value)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
            (RegistryHive.CurrentUser,  RegistryView.Default,    @"SOFTWARE\Valve\Steam", "SteamPath"),
        };

        foreach (var (hive, view, key, value) in candidates)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var sub = baseKey.OpenSubKey(key);
                var path = sub?.GetValue(value) as string;
                if (!string.IsNullOrWhiteSpace(path))

                    return Path.GetFullPath(path.Replace('/', '\\'));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException
                                        or UnauthorizedAccessException
                                        or IOException
                                        or ArgumentException)
            {

            }
        }
        return null;
    }
}
