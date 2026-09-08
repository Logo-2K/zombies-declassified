using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZDUpdater;

public sealed class LocalState
{
    [JsonPropertyName("releaseTag")] public string ReleaseTag { get; set; } = "";
    [JsonPropertyName("installedUtc")] public string InstalledUtc { get; set; } = "";
    [JsonPropertyName("bo2InstallDir")] public string? Bo2InstallDir { get; set; }

    [JsonPropertyName("manifest")] public Manifest? Manifest { get; set; }

    [JsonPropertyName("notPlaced")] public List<string>? NotPlaced { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public static string HomeDir()
    {
        var custom = Environment.GetEnvironmentVariable("ZDUPDATER_HOME");
        var dir = string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZombiesDeclassifiedUpdater")
            : custom;
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string StatePath() => Path.Combine(HomeDir(), "state.json");

    public static string BackupsDir() => Path.Combine(HomeDir(), "backups");

    public static LocalState? Load()
    {
        var path = StatePath();
        if (!File.Exists(path)) return null;

        var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
        try
        {
            return JsonSerializer.Deserialize<LocalState>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new UpdaterException(
                $"the local install record at {path} is corrupt ({ex.Message}). " +
                "Move or delete that file and re-run 'install' to rebuild it — " +
                "your installed game files are untouched by this error.");
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, JsonOpts);
        var path = StatePath();
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, System.Text.Encoding.UTF8);
        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null);
        else
            File.Move(tmp, path);
    }
}
