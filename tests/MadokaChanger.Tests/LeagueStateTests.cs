using System.Net;
using System.Text;
using MadokaChanger.Core;
using Xunit;

namespace MadokaChanger.Tests;

public sealed class LeagueStateTests
{
    [Fact]
    public async Task ReadsLocalCellInChampSelectThenMatchesCurrentPlayerInGame()
    {
        var phase = "ChampSelect";
        var handler = new FakeLcuHandler(path => path switch
        {
            "/lol-gameflow/v1/gameflow-phase" => Quote(phase),
            "/lol-champ-select/v1/session" => """{"localPlayerCellId":4,"myTeam":[{"cellId":1,"championId":22},{"cellId":4,"championId":103}],"actions":[[{"actorCellId":1,"championId":22,"completed":true,"type":"pick"}],[{"actorCellId":4,"championId":103,"completed":true,"type":"pick"}]]}""",
            "/lol-summoner/v1/current-summoner" => """{"internalName":"local player","summonerId":123,"displayName":"Local Player"}""",
            "/lol-gameflow/v1/session" => phase == "GameStart"
                ? """{"gameData":{"playerChampionSelections":[{"summonerInternalName":"other","championId":22}]}}"""
                : """{"gameData":{"playerChampionSelections":[{"summonerInternalName":"other","championId":22},{"summonerInternalName":"local player","championId":103}]}}""",
            _ => "{}"
        });
        using var client = new HttpClient(handler);
        using var source = new LcuLeagueStateSource(client);
        var folder = MakeGameFolder();
        try
        {
            var select = await source.ReadAsync(folder, CancellationToken.None);
            Assert.Equal(new LeagueState(true, "ChampSelect", 103, false), select);

            phase = "GameStart";
            Assert.Equal(new LeagueState(true, "GameStart", 103, true), await source.ReadAsync(folder, CancellationToken.None));

            phase = "InProgress";
            var game = await source.ReadAsync(folder, CancellationToken.None);
            Assert.Equal(new LeagueState(true, "InProgress", 103, true), game);
            Assert.Contains(handler.Requests, request => request.EndsWith("/lol-champ-select/v1/session", StringComparison.Ordinal));
            Assert.Contains(handler.Requests, request => request.EndsWith("/lol-gameflow/v1/session", StringComparison.Ordinal));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task MissingOrAmbiguousLocalPlayerDoesNotBorrowAnotherChampion()
    {
        var handler = new FakeLcuHandler(path => path switch
        {
            "/lol-gameflow/v1/gameflow-phase" => Quote("InProgress"),
            "/lol-summoner/v1/current-summoner" => """{"internalName":"missing"}""",
            "/lol-gameflow/v1/session" => """{"gameData":{"playerChampionSelections":[{"summonerInternalName":"teammate","championId":103}]}}""",
            _ => "{}"
        });
        using var source = new LcuLeagueStateSource(new HttpClient(handler));
        var folder = MakeGameFolder();
        try { Assert.Equal(new LeagueState(true, "InProgress", null, true), await source.ReadAsync(folder, CancellationToken.None)); }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task ClientDisconnectClearsPreviouslyDetectedChampion()
    {
        var online = true;
        var handler = new FakeLcuHandler(path =>
        {
            if (!online) throw new HttpRequestException("offline");
            return path switch
            {
                "/lol-gameflow/v1/gameflow-phase" => Quote("ChampSelect"),
            "/lol-champ-select/v1/session" => """{"localPlayerCellId":7,"myTeam":[{"cellId":7,"championId":22}],"actions":[[{"actorCellId":7,"championId":22,"completed":true,"type":"pick"}]]}""",
                _ => "{}"
            };
        });
        using var source = new LcuLeagueStateSource(new HttpClient(handler));
        var folder = MakeGameFolder();
        try
        {
            Assert.Equal(22, (await source.ReadAsync(folder, CancellationToken.None)).ChampionId);
            online = false;
            Assert.Equal(new LeagueState(false, "Unavailable", null, false), await source.ReadAsync(folder, CancellationToken.None));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task MissingCurrentSummonerDuringImmediateGameStartRetainsVerifiedChampSelectChampion()
    {
        var phase = "ChampSelect";
        var handler = new HttpResponseHandler(path => path switch
        {
            "/lol-gameflow/v1/gameflow-phase" => Json(HttpStatusCode.OK, Quote(phase)),
            "/lol-champ-select/v1/session" => Json(HttpStatusCode.OK, """{"localPlayerCellId":4,"myTeam":[{"cellId":4,"championId":103}],"actions":[[{"actorCellId":4,"championId":103,"completed":true,"type":"pick"}]]}"""),
            "/lol-summoner/v1/current-summoner" when phase == "GameStart" => Json(HttpStatusCode.NotFound, "{}"),
            _ => Json(HttpStatusCode.OK, "{}")
        });
        using var client = new HttpClient(handler);
        using var source = new LcuLeagueStateSource(client);
        var folder = MakeGameFolder();
        try
        {
            Assert.Equal(103, (await source.ReadAsync(folder, CancellationToken.None)).ChampionId);
            phase = "GameStart";
            Assert.Equal(new LeagueState(true, "GameStart", 103, true), await source.ReadAsync(folder, CancellationToken.None));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task InvalidPhasePayloadClearsRememberedChampion()
    {
        var phaseReads = 0;
        var handler = new HttpResponseHandler(path => path switch
        {
            "/lol-gameflow/v1/gameflow-phase" => Json(HttpStatusCode.OK, ++phaseReads == 2 ? "{}" : Quote(phaseReads <= 1 ? "ChampSelect" : "GameStart")),
            "/lol-champ-select/v1/session" => Json(HttpStatusCode.OK, """{"localPlayerCellId":4,"myTeam":[{"cellId":4,"championId":103}],"actions":[[{"actorCellId":4,"championId":103,"completed":true,"type":"pick"}]]}"""),
            "/lol-summoner/v1/current-summoner" => Json(HttpStatusCode.OK, """{"internalName":"local"}"""),
            "/lol-gameflow/v1/session" => Json(HttpStatusCode.OK, """{"gameData":{"playerChampionSelections":[]}}"""),
            _ => Json(HttpStatusCode.OK, "{}")
        });
        using var client = new HttpClient(handler);
        using var source = new LcuLeagueStateSource(client);
        var folder = MakeGameFolder();
        try
        {
            Assert.Equal(103, (await source.ReadAsync(folder, CancellationToken.None)).ChampionId);
            Assert.Equal(new LeagueState(false, "Unavailable", null, false), await source.ReadAsync(folder, CancellationToken.None));
            Assert.Equal(new LeagueState(true, "GameStart", null, true), await source.ReadAsync(folder, CancellationToken.None));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task ChampSelectHoverAndBanAreIgnoredUntilLocalPickCompletesAndTradesFollowMyTeam()
    {
        var session = Session(4, 103, """{"actorCellId":4,"championId":103,"completed":false,"type":"pick"}""",
            """{"actorCellId":4,"championId":22,"completed":true,"type":"ban"}""");
        var handler = new HttpResponseHandler(path => path switch
        {
            "/lol-gameflow/v1/gameflow-phase" => Json(HttpStatusCode.OK, Quote("ChampSelect")),
            "/lol-champ-select/v1/session" => Json(HttpStatusCode.OK, session),
            _ => Json(HttpStatusCode.OK, "{}")
        });
        using var source = new LcuLeagueStateSource(new HttpClient(handler));
        var folder = MakeGameFolder();
        try
        {
            Assert.Null((await source.ReadAsync(folder, CancellationToken.None)).ChampionId);

            session = Session(4, 103, """{"actorCellId":4,"championId":103,"completed":true,"type":"pick"}""");
            Assert.Equal(103, (await source.ReadAsync(folder, CancellationToken.None)).ChampionId);

            // A completed action records the original lock. The local team row is authoritative after a trade.
            session = Session(4, 22, """{"actorCellId":4,"championId":103,"completed":true,"type":"pick"}""");
            Assert.Equal(22, (await source.ReadAsync(folder, CancellationToken.None)).ChampionId);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task ReadyCheckAndDodgeClearTheLastLockedChampionAndCannotSeedNextGame()
    {
        var phase = "ChampSelect";
        var champSelect = Session(4, 103, """{"actorCellId":4,"championId":103,"completed":true,"type":"pick"}""");
        var handler = new HttpResponseHandler(path => path switch
        {
            "/lol-gameflow/v1/gameflow-phase" => Json(HttpStatusCode.OK, Quote(phase)),
            "/lol-champ-select/v1/session" => Json(HttpStatusCode.OK, champSelect),
            "/lol-summoner/v1/current-summoner" => Json(HttpStatusCode.NotFound, "{}"),
            "/lol-gameflow/v1/session" => Json(HttpStatusCode.NotFound, "{}"),
            _ => Json(HttpStatusCode.OK, "{}")
        });
        using var source = new LcuLeagueStateSource(new HttpClient(handler));
        var folder = MakeGameFolder();
        try
        {
            Assert.Equal(103, (await source.ReadAsync(folder, CancellationToken.None)).ChampionId);
            phase = "ReadyCheck";
            Assert.Equal(new LeagueState(true, "ReadyCheck", null, false), await source.ReadAsync(folder, CancellationToken.None));
            phase = "GameStart";
            Assert.Equal(new LeagueState(true, "GameStart", null, true), await source.ReadAsync(folder, CancellationToken.None));

            phase = "None"; // Dodge/end of queue.
            Assert.Equal(new LeagueState(true, "None", null, false), await source.ReadAsync(folder, CancellationToken.None));
            phase = "ChampSelect"; // New lobby starts with a hover and an unfinished action.
            champSelect = Session(4, 22, """{"actorCellId":4,"championId":22,"completed":false,"type":"pick"}""");
            Assert.Null((await source.ReadAsync(folder, CancellationToken.None)).ChampionId);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task AramAutoassignmentAndBenchSwapUseOnlyFinalizedLocalSelection()
    {
        var session = Session(4, 103, pickAction: null, timerPhase: "BAN_PICK", benchEnabled: true, benchChampionIds: [103, 22]);
        var handler = new HttpResponseHandler(path => path switch
        {
            "/lol-gameflow/v1/gameflow-phase" => Json(HttpStatusCode.OK, Quote("ChampSelect")),
            "/lol-champ-select/v1/session" => Json(HttpStatusCode.OK, session),
            _ => Json(HttpStatusCode.OK, "{}")
        });
        using var source = new LcuLeagueStateSource(new HttpClient(handler));
        var folder = MakeGameFolder();
        try
        {
            Assert.Null((await source.ReadAsync(folder, CancellationToken.None)).ChampionId);
            session = Session(4, 103, pickAction: null, timerPhase: "FINALIZATION", benchEnabled: true, benchChampionIds: [103]);
            Assert.Null((await source.ReadAsync(folder, CancellationToken.None)).ChampionId);
            session = Session(4, 103, pickAction: null, timerPhase: "FINALIZATION", benchEnabled: true, benchChampionIds: [22]);
            Assert.Equal(103, (await source.ReadAsync(folder, CancellationToken.None)).ChampionId);
            // A bench swap changes the local slot; read that current row, not the earlier finalized value.
            session = Session(4, 22, pickAction: null, timerPhase: "FINALIZATION", benchEnabled: true, benchChampionIds: [103]);
            Assert.Equal(22, (await source.ReadAsync(folder, CancellationToken.None)).ChampionId);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static string Session(int localCellId, int localChampionId, string? pickAction, string? otherAction = null,
        string timerPhase = "BAN_PICK", bool benchEnabled = false, int[]? benchChampionIds = null)
    {
        var actions = new List<string>();
        if (otherAction is not null) actions.Add("[" + otherAction + "]");
        if (pickAction is not null) actions.Add("[" + pickAction + "]");
        return $$"""{"localPlayerCellId":{{localCellId}},"myTeam":[{"cellId":{{localCellId}},"championId":{{localChampionId}}}],"actions":[{{string.Join(",", actions)}}],"timer":{"phase":"{{timerPhase}}"},"benchEnabled":{{benchEnabled.ToString().ToLowerInvariant()}},"benchChampionIds":{{System.Text.Json.JsonSerializer.Serialize(benchChampionIds ?? [])}}}""";
    }

    private static string MakeGameFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "MadokaDuoLcu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "lockfile"), "LeagueClient:12345:29999:fixture-password:https");
        return root;
    }
    private static string Quote(string text) => System.Text.Json.JsonSerializer.Serialize(text);
    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeLcuHandler(Func<string, string> bodyForPath) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            var body = bodyForPath(request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class HttpResponseHandler(Func<string, HttpResponseMessage> responseForPath) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseForPath(request.RequestUri!.AbsolutePath));
    }
}
