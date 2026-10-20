namespace MadokaChanger.Core;

public static class DuoConfiguration
{
    public const string DefaultRelayUrl = "https://madokachanger-duo-relay.intogummybear.workers.dev";
    public const string RelayUrlEnvironmentVariable = "MADOKACHANGER_DUO_URL";

    /// <summary>Resolves an explicit URL first, then the process override, then the deployed relay.</summary>
    public static string ResolveRelayUrl(string? url = null)
    {
        return ResolveRelayUrl(url, Environment.GetEnvironmentVariable(RelayUrlEnvironmentVariable));
    }

    /// <summary>Resolves a URL using an explicitly supplied process override, primarily for deterministic callers.</summary>
    public static string ResolveRelayUrl(string? url, string? environmentOverride) =>
        !string.IsNullOrWhiteSpace(url) ? url.Trim()
        : !string.IsNullOrWhiteSpace(environmentOverride) ? environmentOverride.Trim()
        : DefaultRelayUrl;

    public static bool IsValidRelayUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    public static IDuoTransport CreateTransport(string? url = null)
    {
        var resolved = ResolveRelayUrl(url);
        if(IsValidRelayUrl(resolved) && Uri.TryCreate(resolved,UriKind.Absolute,out var uri))
        {
            try { return new DuoHttpTransport(uri); }
            catch(ArgumentException) { }
        }
        return new UnconfiguredTransport();
    }
    private sealed class UnconfiguredTransport : IDuoTransport
    {
        private static InvalidOperationException Error()=>new("Duo relay URL is invalid. Use HTTPS, or HTTP only for a loopback address.");
        public Task<DuoMembership> CreateAsync(CancellationToken ct)=>Task.FromException<DuoMembership>(Error());
        public Task<DuoMembership> JoinAsync(string code,CancellationToken ct)=>Task.FromException<DuoMembership>(Error());
        public Task<DuoSnapshot> SnapshotAsync(DuoMembership membership,CancellationToken ct)=>Task.FromException<DuoSnapshot>(Error());
        public Task<DuoSnapshot> PublishAsync(DuoMembership membership,DuoPublish update,CancellationToken ct)=>Task.FromException<DuoSnapshot>(Error());
        public Task LeaveAsync(DuoMembership membership,CancellationToken ct)=>Task.CompletedTask;
        public void Dispose() { }
    }
}
