using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZDUpdater;

public enum RootKind { PlutoStorage, SteamApp }

public sealed class RootSpec
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("appId")] public int AppId { get; set; }

    public RootKind ParsedKind => Kind switch
    {
        "pluto-storage" => RootKind.PlutoStorage,
        "steam-app" => RootKind.SteamApp,
        _ => throw new ManifestException($"unknown root kind '{Kind}'")
    };
}

public sealed class Channel
{

    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("assetName")] public string? AssetName { get; set; }

    [JsonPropertyName("tag")] public string? Tag { get; set; }
    [JsonPropertyName("fileId")] public string? FileId { get; set; }
    [JsonPropertyName("filename")] public string? Filename { get; set; }
    [JsonPropertyName("member")] public string? Member { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class FileEntry
{
    [JsonPropertyName("relpath")] public string RelPath { get; set; } = "";
    [JsonPropertyName("root")] public string Root { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("md5")] public string Md5 { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("compareGroup")] public string CompareGroup { get; set; } = "";

    [JsonPropertyName("elevation")] public string Elevation { get; set; } = "none";
    [JsonPropertyName("channels")] public List<Channel> Channels { get; set; } = new();

    [JsonPropertyName("priorSha256")] public List<string> PriorSha256 { get; set; } = new();

    public bool NeedsElevation => Elevation == "bo2-elevated";

    public string Key => Root + "|" + RelPath;
}

public sealed class RetireEntry
{
    [JsonPropertyName("relpath")] public string RelPath { get; set; } = "";
    [JsonPropertyName("root")] public string Root { get; set; } = "";
    [JsonPropertyName("note")] public string? Note { get; set; }

    public string Key => Root + "|" + RelPath;
}

public sealed class Manifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("releaseTag")] public string ReleaseTag { get; set; } = "";
    [JsonPropertyName("releaseVersion")] public string ReleaseVersion { get; set; } = "";
    [JsonPropertyName("generatedUtc")] public string GeneratedUtc { get; set; } = "";
    [JsonPropertyName("layoutId")] public string LayoutId { get; set; } = "";
    [JsonPropertyName("shortHash")] public string ShortHash { get; set; } = "";
    [JsonPropertyName("roots")] public Dictionary<string, RootSpec> Roots { get; set; } = new();
    [JsonPropertyName("files")] public List<FileEntry> Files { get; set; } = new();
    [JsonPropertyName("retire")] public List<RetireEntry> Retire { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Manifest Parse(string json)
    {
        var m = JsonSerializer.Deserialize<Manifest>(json, JsonOpts)
                 ?? throw new ManifestException("empty manifest.json");
        if (m.SchemaVersion != 2)
            throw new ManifestException(
                $"manifest schemaVersion {m.SchemaVersion} unsupported by this updater build " +
                "(this build understands schemaVersion 2 only — a v3 manifest means a newer " +
                "updater is required; self-update should have caught this first)");
        foreach (var f in m.Files)
        {
            if (!m.Roots.ContainsKey(f.Root))
                throw new ManifestException($"file '{f.RelPath}' references unknown root '{f.Root}'");
            if (f.Channels.Count == 0)
                throw new ManifestException($"file '{f.RelPath}' has no channels — cannot be fetched");
        }
        var shipped = m.Files.Select(f => f.Key.ToLowerInvariant()).ToHashSet();
        foreach (var r in m.Retire)
        {
            if (!m.Roots.ContainsKey(r.Root))
                throw new ManifestException($"retire row '{r.RelPath}' references unknown root '{r.Root}'");
            if (shipped.Contains(r.Key.ToLowerInvariant()))
                throw new ManifestException($"retire row '{r.RelPath}' is also a shipped file");
        }
        return m;
    }

    public IReadOnlyDictionary<string, FileEntry> ByKey() =>
        Files.ToDictionary(f => f.Key, f => f);

    public Manifest Without(ISet<string> keys) => new()
    {
        SchemaVersion = SchemaVersion,
        ReleaseTag = ReleaseTag,
        ReleaseVersion = ReleaseVersion,
        GeneratedUtc = GeneratedUtc,
        LayoutId = LayoutId,
        ShortHash = ShortHash,
        Roots = Roots,
        Files = Files.Where(f => !keys.Contains(f.Key)).ToList(),
        Retire = Retire,
    };
}

public sealed class ManifestException : Exception
{
    public ManifestException(string message) : base(message) { }
}

public enum RowAction { Unchanged, New, Changed, ForeignDrift, Removed }

public sealed class DiffRow
{
    public required FileEntry Entry { get; init; }
    public required RowAction Action { get; init; }
    public string? OnDiskSha256 { get; init; }

    public bool Legacy { get; init; }
}
