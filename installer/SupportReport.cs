using System.Text;
using System.Text.Json;

namespace ZDUpdater;

public static class SupportReport
{
    private static StreamWriter? _w;
    private static TextWriter? _out;
    private static TextWriter? _err;
    private static string? _path;
    private static bool _done;
    private static readonly object _lock = new();

    public static string? Path => _path;

    public static void Start(string[] args)
    {
        try
        {
            var dir = LocalState.HomeDir();
            _path = System.IO.Path.Combine(dir, "support-report.txt");
            var prev = System.IO.Path.Combine(dir, "support-report.previous.txt");
            if (File.Exists(_path)) File.Copy(_path, prev, true);
            _w = new StreamWriter(_path, false, new UTF8Encoding(false)) { AutoFlush = true };

            var info = typeof(SupportReport).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? "?";
            _w.WriteLine("Zombies Declassified installer support report");
            _w.WriteLine($"updater {info}   run {DateTime.Now:yyyy-MM-dd HH:mm:ss}   {Environment.OSVersion}   64-bit {Environment.Is64BitOperatingSystem}");
            _w.WriteLine("args: " + (args.Length == 0 ? "(wizard)" : string.Join(" ", args)));

            var pluto = PathDetection.ResolvePlutoT6();
            _w.WriteLine("plutonium storage: " + pluto + (Directory.Exists(pluto) ? "" : "  (missing)"));
            var bo2 = PathDetection.DetectBo2InstallDir();
            _w.WriteLine("black ops ii: " + (bo2 ?? "(not detected)"));

            var mods = System.IO.Path.Combine(pluto, "mods");
            if (Directory.Exists(mods))
            {
                foreach (var m in Directory.GetDirectories(mods))
                    _w.WriteLine("mod folder: " + System.IO.Path.GetFileName(m) + "   name: " + ModName(m));
            }
            else
            {
                _w.WriteLine("mods folder: none");
            }

            var state = LocalState.StatePath();
            _w.WriteLine("state: " + (File.Exists(state) ? StateSummary(state) : "(none)"));
            _w.WriteLine("---- run ----");

            _out = Console.Out;
            _err = Console.Error;
            Console.SetOut(new TeeWriter(_out, _w));
            Console.SetError(new TeeWriter(_err, _w));
            Console.CancelKeyPress += (_, _) => Finish(130, "interrupted (Ctrl+C)");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Finish(1, "crashed: " + e.ExceptionObject);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Finish(Environment.ExitCode, "process exit");
            Console.WriteLine("Support report: " + _path);
        }
        catch (Exception)
        {
            _w = null;
        }
    }

    public static void Finish(int code, string how)
    {
        lock (_lock)
        {
            if (_done || _w == null) return;
            _done = true;
            try
            {
                if (_out != null) Console.SetOut(_out);
                if (_err != null) Console.SetError(_err);
                _w.WriteLine($"---- end: {how}, exit code {code} ----");
                _w.Flush();
                _w.Dispose();
            }
            catch (Exception) { }
            try { Console.WriteLine("Support report: " + _path); } catch (Exception) { }
        }
    }

    private static string ModName(string dir)
    {
        try
        {
            var p = System.IO.Path.Combine(dir, "mod.json");
            if (!File.Exists(p)) return "(no mod.json)";
            using var d = JsonDocument.Parse(File.ReadAllText(p));
            return d.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "(no name)";
        }
        catch (Exception ex)
        {
            return "(unreadable: " + ex.GetType().Name + ")";
        }
    }

    private static string StateSummary(string path)
    {
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllText(path));
            var parts = new List<string>();
            foreach (var p in d.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String || p.Value.ValueKind == JsonValueKind.Number)
                    parts.Add(p.Name + "=" + p.Value);
                else if (p.Value.ValueKind == JsonValueKind.Array)
                    parts.Add(p.Name + "[" + p.Value.GetArrayLength() + "]");
                else if (p.Value.ValueKind == JsonValueKind.Object)
                    parts.Add(p.Name + "{...}");
            }
            return string.Join("  ", parts);
        }
        catch (Exception ex)
        {
            return "(unreadable: " + ex.GetType().Name + ")";
        }
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _a;
        private readonly TextWriter _b;
        public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
        public override Encoding Encoding => _a.Encoding;
        public override void Write(char value) { _a.Write(value); try { _b.Write(value); } catch (Exception) { } }
        public override void Write(string? value) { _a.Write(value); try { _b.Write(value); } catch (Exception) { } }
        public override void WriteLine(string? value) { _a.WriteLine(value); try { _b.WriteLine(value); } catch (Exception) { } }
        public override void Flush() { _a.Flush(); try { _b.Flush(); } catch (Exception) { } }
    }
}
