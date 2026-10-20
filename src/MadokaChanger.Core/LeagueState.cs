using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MadokaChanger.Core;

public sealed record LeagueState(bool Available, string Phase, int? ChampionId, bool IsInGame);

public interface ILeagueStateSource
{
    Task<LeagueState> ReadAsync(string? gameFolder, CancellationToken ct);
}

/// <summary>Reads only the League client API on its loopback listener.</summary>
public sealed class LcuLeagueStateSource : ILeagueStateSource, IDisposable
{
    private const string GameflowPhaseEndpoint = "/lol-gameflow/v1/gameflow-phase";
    private const string ChampSelectEndpoint = "/lol-champ-select/v1/session";
    private const string GameflowSessionEndpoint = "/lol-gameflow/v1/session";
    private const string CurrentSummonerEndpoint = "/lol-summoner/v1/current-summoner";
    private const int MaxJsonBytes = 1024 * 1024;
    private readonly HttpClient? suppliedClient;
    private readonly HttpClient ownedClient;
    private readonly object sync = new();
    private string? previousPhase;
    private int? previousChampion;

    public LcuLeagueStateSource(HttpClient? httpClient = null)
    {
        suppliedClient = httpClient;
        if (httpClient is null)
        {
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                // Riot documents the client's self-signed TLS certificate. Only loopback URIs are accepted here.
                ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri is { IsLoopback: true }
            };
            ownedClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        }
        else ownedClient = httpClient;
    }

    public async Task<LeagueState> ReadAsync(string? gameFolder, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var requestToken = timeout.Token;
        LcuCredentials? credentials;
        try { credentials = LeagueClientApi.FindCredentials(gameFolder); }
        catch (InvalidDataException) { ClearRemembered(); return Unavailable(); }
        if (credentials is null) { ClearRemembered(); return Unavailable(); }
        if (credentials.Protocol != Uri.UriSchemeHttps) { ClearRemembered(); return Unavailable(); }

        try
        {
            using var phaseJson = await GetAsync(credentials, GameflowPhaseEndpoint, requestToken).ConfigureAwait(false);
            if (phaseJson.RootElement.ValueKind != JsonValueKind.String) { ClearRemembered(); return Unavailable(); }
            var phase = phaseJson.RootElement.GetString();
            if (string.IsNullOrWhiteSpace(phase) || phase.Length > 40) { ClearRemembered(); return Unavailable(); }

            LeagueState result;
            if (phase == "ChampSelect")
            {
                using var session = await GetAsync(credentials, ChampSelectEndpoint, requestToken).ConfigureAwait(false);
                result = new LeagueState(true, phase, ReadChampSelectChampion(session), false);
            }
            else if (IsInGamePhase(phase))
            {
                int? champion;
                try { champion = await ReadInGameChampionAsync(credentials, requestToken).ConfigureAwait(false); }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { champion = null; }
                if (champion is null)
                {
                    lock (sync)
                    {
                        if (IsSafeTransition(previousPhase, phase)) champion = previousChampion;
                    }
                }
                result = new LeagueState(true, phase, champion, true);
            }
            else if (phase == "ReadyCheck")
            {
                int? champion = null;
                lock (sync) if (previousPhase == "ChampSelect") champion = previousChampion;
                result = new LeagueState(true, phase, champion, false);
            }
            else result = new LeagueState(true, phase, null, false);

            lock (sync)
            {
                previousPhase = phase;
                previousChampion = result.ChampionId;
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { ClearRemembered(); throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or TaskCanceledException or InvalidOperationException)
        {
            ClearRemembered();
            return Unavailable();
        }
    }

    private async Task<int?> ReadInGameChampionAsync(LcuCredentials credentials, CancellationToken ct)
    {
        // Both fields are local LCU data. The internal name is used only for matching the current player in memory.
        using var summoner = await GetAsync(credentials, CurrentSummonerEndpoint, ct).ConfigureAwait(false);
        var internalName = ReadString(summoner.RootElement, "internalName");
        if (string.IsNullOrWhiteSpace(internalName)) return null;
        using var session = await GetAsync(credentials, GameflowSessionEndpoint, ct).ConfigureAwait(false);
        if (!TryProperty(session.RootElement, "gameData", out var gameData) || !TryProperty(gameData, "playerChampionSelections", out var selections)
            || selections.ValueKind != JsonValueKind.Array) return null;

        var matches = new List<JsonElement>();
        foreach (var selection in selections.EnumerateArray())
        {
            if (!string.Equals(ReadString(selection, "summonerInternalName"), internalName, StringComparison.OrdinalIgnoreCase)) continue;
            matches.Add(selection);
        }
        return matches.Count == 1 ? ReadPositiveInt(matches[0], "championId") : null;
    }

    private async Task<JsonDocument> GetAsync(LcuCredentials credentials, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(credentials.BaseAddress, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes("riot:" + credentials.Password)));
        using var response = await ownedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("The League client didn't return state.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength is > MaxJsonBytes) throw new InvalidDataException("The League client response exceeded the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > MaxJsonBytes) throw new InvalidDataException("The League client response exceeded the size limit.");
            buffer.Write(bytes, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    private static int? ReadChampSelectChampion(JsonDocument session)
    {
        var root = session.RootElement;
        if (!TryProperty(root, "localPlayerCellId", out var localCell) || !localCell.TryGetInt32(out var cellId)
            || !TryProperty(root, "myTeam", out var team) || team.ValueKind != JsonValueKind.Array) return null;
        var matches = team.EnumerateArray().Where(p => TryProperty(p, "cellId", out var cell) && cell.TryGetInt32(out var id) && id == cellId).ToArray();
        return matches.Length == 1 ? ReadPositiveInt(matches[0], "championId") : null;
    }

    private static bool IsInGamePhase(string phase) => phase is "GameStart" or "InProgress" or "Reconnect" or "PreEndOfGame" or "EndOfGame";
    private static bool IsSafeTransition(string? previous, string current) =>
        previous is "ChampSelect" or "ReadyCheck" or "GameStart" or "InProgress" or "Reconnect"
        && current is "GameStart" or "InProgress" or "Reconnect" or "PreEndOfGame" or "EndOfGame";
    private static LeagueState Unavailable() => new(false, "Unavailable", null, false);

    private void ClearRemembered() { lock (sync) { previousPhase = null; previousChampion = null; } }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadPositiveInt(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.TryGetInt32(out var number) && number is > 0 and <= 10000 ? number : null;

    public void Dispose() { if (suppliedClient is null) ownedClient.Dispose(); }
}
