using MadokaChanger.Core;
using Xunit;

namespace MadokaChanger.Tests;

public sealed class DuoConfigurationTests
{
    [Fact]
    public void MissingOrBlankConfigurationUsesDeployedRelay()
    {
        Assert.Equal(DuoConfiguration.DefaultRelayUrl, DuoConfiguration.ResolveRelayUrl(null, null));
        Assert.Equal(DuoConfiguration.DefaultRelayUrl, DuoConfiguration.ResolveRelayUrl("  ", ""));
        Assert.Equal(DuoConfiguration.DefaultRelayUrl, DuoConfiguration.ResolveRelayUrl("", null));
    }

    [Fact]
    public void ExplicitUrlAndProcessOverrideArePreserved()
    {
        Assert.Equal("https://custom.example/", DuoConfiguration.ResolveRelayUrl(" https://custom.example/ ", "https://env.example"));
        Assert.Equal("https://env.example", DuoConfiguration.ResolveRelayUrl(null, " https://env.example "));
    }

    [Fact]
    public async Task NoArgumentAndNullTransportUseDefaultWhileUnsafeUrlIsDisabled()
    {
        using var defaultTransport = (IDisposable)DuoConfiguration.CreateTransport(DuoConfiguration.DefaultRelayUrl);
        using var customTransport = (IDisposable)DuoConfiguration.CreateTransport("https://custom.example");
        using var noArgumentTransport = (IDisposable)DuoConfiguration.CreateTransport();
        using var nullTransport = (IDisposable)DuoConfiguration.CreateTransport(null);
        Assert.IsType<DuoHttpTransport>(defaultTransport);
        Assert.IsType<DuoHttpTransport>(customTransport);
        Assert.IsType<DuoHttpTransport>(noArgumentTransport);
        Assert.IsType<DuoHttpTransport>(nullTransport);

        using var invalid = (IDisposable)DuoConfiguration.CreateTransport("http://custom.example");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ((IDuoTransport)invalid).CreateAsync(CancellationToken.None));
        Assert.False(DuoConfiguration.IsValidRelayUrl("https://user:secret@custom.example"));
        Assert.False(DuoConfiguration.IsValidRelayUrl("https://custom.example/?token=secret"));
    }
}
