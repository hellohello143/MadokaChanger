using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadokaChanger.Core;

namespace MadokaChanger.App.ViewModels;

/// <summary>UI projection for a temporary, two-person skin session.</summary>
public sealed partial class DuoViewModel : ObservableObject, IDisposable
{
    private readonly DuoCoordinator coordinator;
    private readonly Dispatcher dispatcher;
    private Catalog? catalog;
    [ObservableProperty] private string joinCode = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string actionError = "";

    public DuoViewModel(DuoCoordinator coordinator)
    {
        this.coordinator = coordinator;
        dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        coordinator.Changed += CoordinatorChanged;
    }

    public string RelayUrl => coordinator.RelayUrl;
    public bool HasRelay => DuoConfiguration.IsValidRelayUrl(RelayUrl);
    public string RelayHelp => HasRelay
        ? RelayUrl.Equals(DuoConfiguration.DefaultRelayUrl, StringComparison.OrdinalIgnoreCase)
            ? "Using the MadokaChanger Duo relay."
            : $"Using custom relay: {RelayUrl}"
        : "Duo relay URL is invalid. Use HTTPS, or HTTP only for a loopback address, then restart MadokaChanger.";
    public string PartyStatus => coordinator.Party.Status;
    public bool HasParty => coordinator.Party.Membership is not null;
    public string PartyCode => coordinator.Party.Membership?.Snapshot.Code ?? "----";
    public bool CanJoin => !IsBusy && HasRelay && DuoProtocol.IsCode(JoinCode) && !HasParty;
    public bool CanCreate => !IsBusy && HasRelay && !HasParty;
    public bool CanLeave => !IsBusy && HasParty;
    public string ConnectionStatus => coordinator.Party.IsConnected ? "Connected" : HasParty ? "Reconnecting" : "Not connected";
    public bool HasLockedChampion => coordinator.LocalState.ChampionId is not null;
    public string LocalChampion => !coordinator.LocalState.Available ? "League client unavailable" :
        coordinator.LocalState.ChampionId is { } championId ? ChampionLabel(championId) : "Waiting for champion lock-in";
    public string LocalPhase => coordinator.LocalState.Phase;
    public string LocalDesired => SelectionLabel(coordinator.LocalSelection.Desired);
    public string LocalPrepared => SelectionLabel(coordinator.LocalSelection.Prepared);
    public string LocalPreparation => PreparationLabel(coordinator.Preparation);
    public string PreparationState => coordinator.Preparation;
    public string LocalStatus => coordinator.Status;
    public string PeerPresence => coordinator.Peer is null ? "Waiting for your partner" :
        DateTimeOffset.UtcNow - coordinator.Peer.LastSeen > TimeSpan.FromSeconds(35) ? "Partner presence is stale" : "Partner is here";
    public string PeerChampion => ChampionLabel(coordinator.Peer?.State.ChampionId);
    public string PeerPhase => coordinator.Peer?.State.Phase ?? "Waiting";
    public string PeerDesired => SelectionLabel(coordinator.Peer?.State.Desired);
    public string PeerPrepared => SelectionLabel(coordinator.Peer?.State.Prepared);
    public string PeerPreparation => PreparationLabel(coordinator.Peer?.State.Preparation);
    public string PeerStatus => coordinator.Peer is not { } peer
        ? "Only champion and skin selection metadata is shared."
        : peer.State.Preparation == "unavailable" && peer.State.Desired is null
            ? "Their enabled imported package cannot be shared. Imported packages stay private; choose a supported catalog skin."
            : peer.State.Preparation == "unavailable"
                ? "Their exact selection is unavailable on their device. Install the matching supported catalog package."
                : peer.State.Preparation == "error"
                    ? "Their local skin preparation failed."
                    : "Only champion and skin selection metadata is shared.";
    public string Outcome => ActionError.Length > 0 ? ActionError : LocalStatus;
    public bool HasOutcome => Outcome.Length > 0;

    public void SetCatalog(Catalog? value)
    {
        catalog = value;
        NotifySnapshot();
    }

    public void StartMonitoring()
    {
        if (!App.IsHeadlessVerification) coordinator.StartMonitoring();
    }

    public Task RefreshLocalStateAsync(CancellationToken ct) => coordinator.RefreshLocalStateAsync(ct);

    public void NotifyCommittedSelectionChanged() => coordinator.NotifyCommittedSelectionChanged();

    public async Task ShutdownAsync()
    {
        Dispose();
        await coordinator.DisposeAsync();
    }

    partial void OnJoinCodeChanged(string value)
    {
        var normalized = new string(value.Where(char.IsAsciiDigit).Take(4).ToArray());
        if (normalized != value) JoinCode = normalized;
        OnPropertyChanged(nameof(CanJoin));
    }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanCreate));OnPropertyChanged(nameof(CanJoin));OnPropertyChanged(nameof(CanLeave)); }
    partial void OnActionErrorChanged(string value) { OnPropertyChanged(nameof(Outcome));OnPropertyChanged(nameof(HasOutcome)); }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync() => await RunAsync(ct => coordinator.CreateAsync(ct));

    [RelayCommand(CanExecute = nameof(CanJoin))]
    private async Task JoinAsync() => await RunAsync(ct => coordinator.JoinAsync(JoinCode, ct));

    [RelayCommand(CanExecute = nameof(CanLeave))]
    private async Task LeaveAsync() => await RunAsync(ct => coordinator.LeaveAsync(ct));

    [RelayCommand]
    private void CopyCode()
    {
        if (!HasParty) return;
        try { Clipboard.SetText(PartyCode);ActionError = "Party code copied."; }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or InvalidOperationException) { ActionError = "Could not copy the party code. Select and copy it manually."; }
    }

    private async Task RunAsync(Func<CancellationToken,Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; ActionError = "";
        try { await action(CancellationToken.None); }
        catch (Exception e) when (e is not OperationCanceledException) { ActionError = e.Message; }
        finally { IsBusy = false; NotifySnapshot(); }
    }

    private void CoordinatorChanged()
    {
        if (dispatcher.CheckAccess()) NotifySnapshot();
        else _ = dispatcher.InvokeAsync(NotifySnapshot, DispatcherPriority.DataBind);
    }

    private void NotifySnapshot()
    {
        foreach (var name in new[] { nameof(RelayUrl), nameof(HasRelay), nameof(RelayHelp), nameof(PartyStatus), nameof(HasParty), nameof(PartyCode), nameof(CanJoin), nameof(CanCreate), nameof(CanLeave), nameof(ConnectionStatus), nameof(HasLockedChampion), nameof(LocalChampion), nameof(LocalPhase), nameof(LocalDesired), nameof(LocalPrepared), nameof(LocalPreparation), nameof(PreparationState), nameof(LocalStatus), nameof(PeerPresence), nameof(PeerChampion), nameof(PeerPhase), nameof(PeerDesired), nameof(PeerPrepared), nameof(PeerPreparation), nameof(PeerStatus), nameof(Outcome), nameof(HasOutcome) }) OnPropertyChanged(name);
        CreateCommand.NotifyCanExecuteChanged();JoinCommand.NotifyCanExecuteChanged();LeaveCommand.NotifyCanExecuteChanged();
    }

    private string ChampionLabel(int? championId)
    {
        if (championId is null) return "Not detected";
        var champion = catalog?.Champions.FirstOrDefault(c => c.Id == championId.Value);
        return champion is null ? $"Champion #{championId}" : $"{champion.Name} · #{championId}";
    }

    private string SelectionLabel(Selection? selection)
    {
        if (selection is null) return "No Madoka override";
        var champion = catalog?.Champions.FirstOrDefault(c => c.Id == selection.ChampionId);
        var skin = champion?.Skins.FirstOrDefault(s => s.Id == selection.SkinId);
        var result = skin is null ? $"Champion #{selection.ChampionId} · skin #{selection.SkinId}" : $"{champion!.Name} · {skin.Name}";
        if (selection.ChromaId is { } chromaId)
        {
            var chroma = skin?.Chromas.FirstOrDefault(c => c.Id == chromaId);
            result += chroma is null ? $" · chroma #{chromaId}" : $" · {chroma.Name}";
        }
        if (selection.Form is { Length: > 0 } form) result += $" · form {form}";
        return result;
    }

    private static string PreparationLabel(string? value) => value switch
    {
        "prepared" => "Prepared in the active patcher build",
        "preparing" => "Preparing for the next active build",
        "pending" => "Pending a safe patcher rebuild",
        "unavailable" => "Unavailable on this device",
        "error" => "Preparation failed",
        "waiting" => "Waiting for a locked-in champion with a saved skin",
        "stopped" => "Patcher stopped · selection will be prepared on Start",
        _ => string.IsNullOrWhiteSpace(value) ? "Unknown" : value
    };

    public void Dispose() => coordinator.Changed -= CoordinatorChanged;
}
