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
        => Capture(state).Selections;

    // Capture both kinds of input under the same champion scope. Saved preferences
    // for other champions remain in storage, but never reach a native build.
    internal (IReadOnlyDictionary<int, Selection> Selections, CustomSkin[] CustomSkins) Capture(UserState state)
    {
        lock(sync)
        {
            var selections = new Dictionary<int, Selection>();
            if(localChampion is { } local)
                lock(state.Selections)
                    if(state.Selections.TryGetValue(local, out var selection) && selection.ChampionId == local) selections[local] = selection;
            var custom = state.CustomSkins.Where(c => c.Enabled && c.ChampionId == localChampion).ToArray();
            Problem = null;
            if(peer?.ChampionId is not { } champion) return (selections, custom);
            if(localChampion is null)
            { Problem = "Waiting for your locked-in champion before preparing your partner's appearance.";return (selections, custom); }
            selections.TryGetValue(champion, out var saved);
            if(champion == localChampion)
            {
                if(saved != peer.Desired || state.CustomSkins.Any(c => c.Enabled && c.ChampionId == champion))
                    Problem = "Same-champion conflict: overrides apply per champion. Your own selection takes priority; different appearances cannot be shown for two players using that champion.";
                return (selections, custom);
            }
            // A default or unavailable peer appearance must not silently select an unrelated saved preference.
            selections.Remove(champion);
            if(peer.Preparation == "unavailable" && peer.Desired is null)
            { Problem = "Partner selection is unsupported (imported packages cannot be synchronized)."; return (selections, custom); }
            if(peer.Desired is not { } desired) return (selections, custom);
            if(desired.ChampionId != champion)
            { Problem = "Partner selection does not match their current champion."; return (selections, custom); }
            if(library.Manifest is not { } manifest || manifest.Commit != peer.LibraryCommit || manifest.MetadataPatch != peer.MetadataPatch)
            { Problem = "Partner library version differs. Install the same catalog library on both computers."; return (selections, custom); }
            if(library.Find(desired).Count != 1)
            { Problem = "Partner's exact skin/chroma/form package is missing or ambiguous. Install the matching library; no replacement was selected."; return (selections, custom); }
            selections[champion] = desired;
            return (selections, custom);
        }
    }
}
