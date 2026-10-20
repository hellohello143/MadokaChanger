using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MadokaChanger.Core;

/// <summary>HTTP implementation of the duo relay protocol. It never logs request or response bodies.</summary>
public sealed class DuoHttpTransport : IDuoTransport
{
    private const int MaxResponseBytes = 256 * 1024;
    private const int MaxErrorBytes = 8 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;
    private readonly bool ownsClient;

    public DuoHttpTransport(Uri endpoint, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || !(endpoint.Scheme == Uri.UriSchemeHttps || endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
            throw new ArgumentException("The duo relay URL must use HTTPS, or HTTP on loopback.", nameof(endpoint));

        var baseUri = endpoint.AbsoluteUri.EndsWith('/') ? endpoint : new Uri(endpoint.AbsoluteUri + "/");
        ownsClient = httpClient is null;
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (http.BaseAddress is null) http.BaseAddress = baseUri;
        else if (http.BaseAddress != baseUri) throw new ArgumentException("The HTTP client base address must match the duo relay URL.", nameof(httpClient));
    }

    public Task<DuoMembership> CreateAsync(CancellationToken ct) =>
        SendJsonAsync<DuoMembership>(HttpMethod.Post, "v1/rooms", new { version = DuoProtocol.Version }, null, ct);

    public Task<DuoMembership> JoinAsync(string code, CancellationToken ct) =>
        SendJsonAsync<DuoMembership>(HttpMethod.Post, "v1/rooms/join", new { version = DuoProtocol.Version, code }, null, ct);

    public Task<DuoSnapshot> SnapshotAsync(DuoMembership membership, CancellationToken ct) =>
        SendJsonAsync<DuoSnapshot>(HttpMethod.Get, RoomPath(membership) + "?generation=" + Uri.EscapeDataString(membership.Snapshot.Generation), null, membership.Token, ct);

    public Task<DuoSnapshot> PublishAsync(DuoMembership membership, DuoPublish update, CancellationToken ct) =>
        SendJsonAsync<DuoSnapshot>(HttpMethod.Put, RoomPath(membership) + "/state", update, membership.Token, ct);

    public async Task LeaveAsync(DuoMembership membership, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, RoomPath(membership) + "?generation=" + Uri.EscapeDataString(membership.Snapshot.Generation));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", membership.Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    private static string RoomPath(DuoMembership m) => "v1/rooms/" + Uri.EscapeDataString(m.Snapshot.RoomId);

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object? payload, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (payload is not null) request.Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response.Content, MaxResponseBytes, ct).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("The duo relay returned an empty response.");
        }
        catch (JsonException ex) { throw new InvalidDataException("The duo relay returned invalid JSON.", ex); }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var bytes = await ReadBoundedAsync(response.Content, MaxErrorBytes, ct).ConfigureAwait(false);
        string message = "The duo relay request failed (HTTP " + (int)response.StatusCode + ").";
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                var candidate = m.GetString();
                if (!string.IsNullOrWhiteSpace(candidate)) message = candidate.Length <= 256 ? candidate : candidate[..256];
            }
        }
        catch (JsonException) { }
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > 0 and var length && length > limit)
            throw new InvalidDataException("The duo relay response exceeded the size limit.");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > limit) throw new InvalidDataException("The duo relay response exceeded the size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public void Dispose() { if (ownsClient) http.Dispose(); }
}
