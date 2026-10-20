using System.Net;
using System.Text;
using MadokaChanger.Core;
using Xunit;

namespace MadokaChanger.Tests;

public sealed class DuoTransportTests
{
    [Fact]
    public async Task JoinUsesV1JsonAndAuthenticatedPollingKeepsRoomGeneration()
    {
        var calls = new List<HttpRequestMessage>();
        var bodies = new List<string?>();
        var handler = new StubHandler(request =>
        {
            calls.Add(request);
            bodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult());
            const string snapshot = """{"version":1,"roomId":"room-1","generation":"gen-1","code":"0007","revision":0,"expiresAt":"2030-01-01T00:00:00Z","members":[{"memberId":"member-a","revision":0,"state":{"championId":null,"desired":null,"prepared":null,"phase":"Lobby","preparation":"stopped","libraryCommit":null,"metadataPatch":null},"lastSeen":"2026-01-01T00:00:00Z"}]}""";
            const string membership = """{"snapshot":{"version":1,"roomId":"room-1","generation":"gen-1","code":"0007","revision":0,"expiresAt":"2030-01-01T00:00:00Z","members":[{"memberId":"member-a","revision":0,"state":{"championId":null,"desired":null,"prepared":null,"phase":"Lobby","preparation":"stopped","libraryCommit":null,"metadataPatch":null},"lastSeen":"2026-01-01T00:00:00Z"}]},"memberId":"member-a","token":"test-token"}""";
            var body = request.RequestUri!.AbsolutePath.EndsWith("join", StringComparison.Ordinal) ? membership : snapshot;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        using var client = new HttpClient(handler);
        using var transport = new DuoHttpTransport(new Uri("https://relay.example/"), client);

        var joined = await transport.JoinAsync("0007", CancellationToken.None);
        var read = await transport.SnapshotAsync(joined, CancellationToken.None);

        Assert.Equal("room-1", read.RoomId);
        Assert.Equal("gen-1", read.Generation);
        Assert.Equal("Bearer", calls[1].Headers.Authorization!.Scheme);
        Assert.Equal("test-token", calls[1].Headers.Authorization!.Parameter);
        Assert.EndsWith("?generation=gen-1", calls[1].RequestUri!.PathAndQuery);
        Assert.Contains("\"version\":1", bodies[0]);
        Assert.Contains("\"code\":\"0007\"", bodies[0]);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("ftp://127.0.0.1")]
    public void RejectsUnencryptedNonLoopbackOrUnknownSchemes(string url) =>
        Assert.Throws<ArgumentException>(() => new DuoHttpTransport(new Uri(url)));

    [Fact]
    public async Task RejectsOversizedResponses()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"large\":\"" + new string('x', 300_000) + "\"}")
        });
        using var client = new HttpClient(handler);
        using var transport = new DuoHttpTransport(new Uri("https://relay.example"), client);
        var membership = new DuoMembership(Snapshot(), "member-a", "token");
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.SnapshotAsync(membership, CancellationToken.None));
    }

    private static DuoSnapshot Snapshot() => new(1, "room-1", "gen-1", "0007", 0,
        DateTimeOffset.UtcNow.AddMinutes(1), [new DuoMember("member-a", 0, State(), DateTimeOffset.UtcNow)]);
    private static DuoSelectionState State() => new(null, null, null, "Lobby", "stopped", null, null);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
