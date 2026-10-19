using System.Text.Json.Serialization;

namespace MadokaChanger.Core;

public sealed record Champion(int Id, string Name, string Alias, string Description, string Portrait, List<Skin> Skins);
public sealed record Skin(int Id, string Name, bool IsBase, string Splash, string Tile, string Description, List<Chroma> Chromas);
public sealed record Chroma(int Id, string Name, string Preview, List<string> Colors);
public sealed record Catalog(string Patch, string Language, DateTimeOffset Refreshed, List<Champion> Champions);
public sealed record Selection(int ChampionId, int SkinId, int? ChromaId = null, string? Form = null)
{
    [JsonIgnore] public string Key => $"{ChampionId}:{SkinId}:{ChromaId}:{Form}";
}
public sealed record RepositoryFile(string Path, string Sha, long Size, string? LfsOid = null, long? LfsSize = null);
public sealed record RepositoryRevision(string Branch, string Commit, List<RepositoryFile> Files);
public sealed record PackageRecord(string Path, string Sha, long Size, string Name, string Author, string Version,
    int? ChampionId, int? SkinId, int? ChromaId, string? Form, string? Preview, List<string> Overrides, string? MappingError)
{
    [JsonIgnore] public Selection? Selection => ChampionId is { } c && SkinId is { } s && MappingError is null ? new(c,s,ChromaId,Form) : null;
}
public sealed record LibraryManifest(string Commit, string Branch, DateTimeOffset Installed, string MetadataPatch,
    List<PackageRecord> Packages, List<RepositoryFile> Files);
public sealed record LibraryPointer(string Directory, string Commit);
public sealed record CustomSkin(string Id, int ChampionId, string Name, string Author, string Version, string Description,
    string Sha256, string FileName, string ProjectPath, List<string> Overrides, bool Enabled = false);
public enum ConflictChoice { Reject, PreferRepository, PreferCustom }
public enum PatcherState { Unavailable, Stopped, Building, Starting, Running, Error }
public sealed record PatcherStatus(PatcherState State, string Message);
public sealed record TransferProgress(string Phase, int Completed, int Total, long Bytes, long TotalBytes, int Retries = 0)
{
    [JsonIgnore] public double Percent => Total == 0 ? 0 : 100.0 * Completed / Total;
    [JsonIgnore] public string Display => $"{Phase} · {Completed:N0}/{Total:N0} · {Bytes / 1048576d:N1}/{TotalBytes / 1048576d:N1} MB · {Retries} retries";
}
public sealed class AppSettings
{
    public string GameFolder { get; set; } = "";
    public string LibraryFolder { get; set; } = "";
    public string Language { get; set; } = "default";
    public bool ReducedMotion { get; set; }
    /// <summary>Block skins flagged by League's anti-skinhack scan. Off by default: every library skin is a repacked official skin and is always flagged.</summary>
    public bool EnforceSkinhackScan { get; set; }
    /// <summary>Always run the patcher host elevated. Off by default; it still elevates when League itself is set to run as administrator.</summary>
    public bool ElevatePatcher { get; set; }
}
public sealed class UserState
{
    public AppSettings Settings { get; set; } = new();
    public HashSet<int> Favorites { get; set; } = [];
    public Dictionary<int, Selection> Selections { get; set; } = [];
    public List<CustomSkin> CustomSkins { get; set; } = [];
    public ConflictChoice ConflictChoice { get; set; } = ConflictChoice.Reject;
}
public sealed record PackageInspection(string Name, string Author, string Version, string Description,
    List<string> Champions, List<string> Overrides, long ExpandedBytes);
