namespace MadokaChanger.Core;

// Wire names use JsonFile.Options (camelCase). Credentials are never part of selection state.
public sealed record DuoSelectionState(int? ChampionId, Selection? Desired, Selection? Prepared,
    string Phase, string Preparation, string? LibraryCommit, string? MetadataPatch);
public sealed record DuoMember(string MemberId, long Revision, DuoSelectionState State, DateTimeOffset LastSeen);
public sealed record DuoSnapshot(int Version, string RoomId, string Generation, string Code, long Revision,
    DateTimeOffset ExpiresAt, List<DuoMember> Members);
public sealed record DuoMembership(DuoSnapshot Snapshot, string MemberId, string Token);
public sealed record DuoPublish(int Version, string RoomId, string Generation, long Revision, DuoSelectionState State);

public interface IDuoTransport : IDisposable
{
    Task<DuoMembership> CreateAsync(CancellationToken ct);
    Task<DuoMembership> JoinAsync(string code, CancellationToken ct);
    Task<DuoSnapshot> SnapshotAsync(DuoMembership membership, CancellationToken ct);
    Task<DuoSnapshot> PublishAsync(DuoMembership membership, DuoPublish update, CancellationToken ct);
    Task LeaveAsync(DuoMembership membership, CancellationToken ct);
}

public static class DuoProtocol
{
    public const int Version = 1;
    public static bool IsCode(string? code) => code is { Length: 4 } && code.All(char.IsAsciiDigit);
    public static bool ValidSelection(Selection? s) => s is null || s.ChampionId is > 0 and <= 10000
        && s.SkinId > 0 && s.SkinId / 1000 == s.ChampionId && (s.ChromaId is null or > 0)
        && (s.Form is null || s.Form.Length is > 0 and <= 64 && s.Form.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or ' '));
    public static bool ValidState(DuoSelectionState? s) => s is not null
        && (s.ChampionId is null or > 0 and <= 10000) && ValidSelection(s.Desired) && ValidSelection(s.Prepared)
        && (s.Desired is null || s.Desired.ChampionId == s.ChampionId)
        && (s.Prepared is null || s.Prepared.ChampionId == s.ChampionId)
        && s.Phase is { Length: > 0 and <= 40 } && s.Preparation is "stopped" or "preparing" or "prepared" or "pending" or "unavailable" or "error"
        && (s.LibraryCommit is null || s.LibraryCommit.Length <= 128) && (s.MetadataPatch is null || s.MetadataPatch.Length <= 64);
}
