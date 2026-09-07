namespace ZDUpdater;

public static class Interactive
{
    public static bool IsInteractiveSession { get; private set; }

    public static async Task<int> RunWizardAsync(HttpClient http)
    {
        IsInteractiveSession = true;
        Banner();

        var state = SafeLoadState();
        var plutoRoot = PathDetection.ResolvePlutoT6();

        Console.WriteLine("Step 1 of 3 - finding your game");
        Console.WriteLine();

        Console.WriteLine($"  Plutonium storage : {plutoRoot}");
        if (!Directory.Exists(plutoRoot))
        {
            Console.WriteLine("      ^ this folder does not exist yet. Launch Plutonium's T6 once");
            Console.WriteLine("        (it creates the folder), then run this installer again.");
            return Finish(1);
        }

        var bo2 = ResolveBo2Interactively(state?.Bo2InstallDir);
        if (bo2 == null) return Finish(1);
        Console.WriteLine($"  Black Ops II      : {bo2}");
        Console.WriteLine();

        var isUpdate = state != null;
        if (isUpdate)
        {
            Console.WriteLine($"  Installed: {state!.ReleaseTag}");
            Console.WriteLine();
            Console.WriteLine("  What would you like to do?");
            Console.WriteLine("    1) Update to the latest release (only changed files are downloaded)");
            Console.WriteLine("    2) Verify the installed files");
            Console.WriteLine("    3) Uninstall (removes only what this tool placed; restores anything it displaced)");
            Console.WriteLine("    4) Quit");
            var choice = AskChoice("  Choice [1]: ", "1234", '1');
            if (choice == '4') return Finish(0);
            var wopts = CliOptions.ForWizard(
                choice == '2' ? Command.Verify : choice == '3' ? Command.Uninstall : Command.Update, bo2);
            if (choice == '2')
            {
                Console.WriteLine();
                return Finish(await Program.RunVerifyAsync(wopts));
            }
            if (choice == '3')
            {
                Console.WriteLine();
                if (!Ask("  Remove Zombies Declassified from this machine? [y/N] ", defaultYes: false))
                    return Finish(0);
                return Finish(await Program.RunUninstallAsync(wopts));
            }
        }

        Console.WriteLine(isUpdate
            ? $"Step 2 of 3 - checking for updates (installed: {state!.ReleaseTag})"
            : "Step 2 of 3 - fetching the latest release");
        Console.WriteLine();

        var opts = CliOptions.ForWizard(isUpdate ? Command.Update : Command.Install, bo2);
        var rc = await Program.RunInstallOrUpdateAsync(opts, http, isUpdate);

        Console.WriteLine();
        Console.WriteLine(rc == 0
            ? "Step 3 of 3 - done. Launch Plutonium T6 Zombies and pick a map."
            : "Step 3 of 3 - finished WITH ERRORS (see the lines above). Nothing else was changed.");
        return Finish(rc);
    }

    private static void Banner()
    {
        Console.WriteLine();
        Console.WriteLine("  Zombies Declassified - installer / updater");
        Console.WriteLine("  ------------------------------------------");
        Console.WriteLine("  Adds the DLC5 zombies maps to Black Ops II (Plutonium).");
        Console.WriteLine("  Nothing that ships with the game is overwritten or deleted.");
        Console.WriteLine();
    }

    private static LocalState? SafeLoadState()
    {
        try { return LocalState.Load(); }
        catch (UpdaterException ex)
        {
            Console.WriteLine("  Note: " + ex.Message);
            Console.WriteLine();
            return null;
        }
    }

    private static string? ResolveBo2Interactively(string? remembered)
    {
        var candidate = remembered ?? PathDetection.DetectBo2InstallDir();

        if (candidate != null && LooksLikeBo2(candidate))
        {
            Console.WriteLine($"  Found Black Ops II at:");
            Console.WriteLine($"    {candidate}");
            if (Ask("  Use this folder? [Y/n] ", defaultYes: true))
                return candidate;
        }
        else
        {
            Console.WriteLine("  Could not find your Black Ops II folder automatically.");
        }

        Console.WriteLine();
        Console.WriteLine("  Type the full path to your Black Ops II folder.");
        Console.WriteLine("  It is the one containing BlackOps2.exe, with 'zone' and 'sound' inside it,");
        Console.WriteLine(@"  usually: C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Black Ops II");
        Console.WriteLine("  (You can drag the folder into this window, then press Enter.)");
        Console.WriteLine();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            Console.Write("  Path: ");
            var typed = Console.ReadLine();
            if (typed == null) return null;
            typed = CleanPath(typed);
            if (typed.Length == 0) continue;

            if (!Directory.Exists(typed))
            {
                Console.WriteLine("    That folder does not exist. Try again.");
                continue;
            }
            if (!LooksLikeBo2(typed))
            {
                Console.WriteLine("    That folder does not look like Black Ops II");
                Console.WriteLine("    (no 'zone' and 'sound' folders inside it). Try again.");
                continue;
            }
            return typed;
        }
        Console.WriteLine("  Giving up after 5 tries - nothing was changed.");
        return null;
    }

    private static bool LooksLikeBo2(string dir) =>
        Directory.Exists(Path.Combine(dir, "zone")) && Directory.Exists(Path.Combine(dir, "sound"));

    private static string CleanPath(string raw)
    {
        var s = raw.Trim();
        if (s.Length >= 2 && s.StartsWith('"') && s.EndsWith('"'))
            s = s[1..^1];
        return Path.TrimEndingDirectorySeparator(s.Trim());
    }

    private static char AskChoice(string prompt, string allowed, char fallback)
    {
        for (int i = 0; i < 5; i++)
        {
            Console.Write(prompt);
            var line = Console.ReadLine();
            if (line == null) return fallback;
            line = line.Trim();
            if (line.Length == 0) return fallback;
            if (allowed.Contains(line[0])) return line[0];
            Console.WriteLine("    Please answer with one of: " + string.Join(", ", allowed.ToCharArray()));
        }
        return fallback;
    }

    private static bool Ask(string prompt, bool defaultYes)
    {
        Console.Write(prompt);
        var line = Console.ReadLine();
        if (line == null) return defaultYes;
        line = line.Trim();
        if (line.Length == 0) return defaultYes;
        return line[0] is 'y' or 'Y';
    }

    private static int Finish(int rc)
    {
        Console.WriteLine();
        Console.Write("  Press Enter to close. ");
        try { Console.ReadLine(); } catch (IOException) {  }
        return rc;
    }
}
