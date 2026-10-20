namespace MadokaChanger.Core;

/// <summary>Ephemeral input only; saved preferences never receive peer writes.</summary>
public sealed class DuoOverlay(LibraryStore library)
{
    private readonly object sync = new();
    private int? localChampion;
    private DuoSelectionState? peer;
    public string? Problem { get; private set; }
    public void Update(int? localChampionId, DuoSelectionState? peerState)
    {
        lock(sync) { localChampion = localChampionId; peer = peerState; Problem = null; }
    }
    public IReadOnlyDictionary<int, Selection> Merge(UserState state)
    {
        lock(sync)
        {
            Dictionary<int, Selection> selections;
            lock(state.Selections) selections = new(state.Selections);
            Problem = null;
            if(peer?.ChampionId is not { } champion) return selections;
            if(localChampion is null)
            { Problem = "Waiting for your local champion to be detected before merging the partner's appearance.";return selections; }
            selections.TryGetValue(champion, out var saved);
            if(champion == localChampion)
            {
                if(saved != peer.Desired || state.CustomSkins.Any(c => c.Enabled && c.ChampionId == champion))
                    Problem = "Same-champion conflict: overrides apply per champion. Your own selection takes priority; different appearances cannot be shown for two players using that champion.";
                return selections;
            }
            // A default or unavailable peer appearance must not silently select an unrelated saved preference.
            selections.Remove(champion);
            if(peer.Preparation == "unavailable" && peer.Desired is null)
            { Problem = "Partner selection is unsupported (imported packages cannot be synchronized)."; return selections; }
            if(state.CustomSkins.Any(c => c.Enabled && c.ChampionId == champion))
            { Problem = "Disable the enabled custom package for your partner's champion to synchronize its catalog appearance."; return selections; }
            if(peer.Desired is not { } desired) return selections;
            if(library.Manifest is not { } manifest || manifest.Commit != peer.LibraryCommit || manifest.MetadataPatch != peer.MetadataPatch)
            { Problem = "Partner library version differs. Install the same catalog library on both computers."; return selections; }
            if(library.Find(desired).Count != 1)
            { Problem = "Partner's exact skin/chroma/form package is missing or ambiguous. Install the matching library; no replacement was selected."; return selections; }
            selections[champion] = desired;
            return selections;
        }
    }
}
