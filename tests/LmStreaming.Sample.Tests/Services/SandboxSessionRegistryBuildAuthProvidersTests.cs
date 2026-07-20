using System.Net;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the per-provider gating logic in <c>SandboxSessionRegistry.BuildAuthProviders</c>: each
/// provider (GitHub, ADO, M365) attaches its gateway auth-provider entry only when the
/// configuration it needs is present. M365 is the only provider that gates on both
/// <c>ClientId</c> AND <c>ClientSecret</c> — the secret is the load-bearing one, since the M365
/// confidential client stays disabled without it and emitting the webhook entry would point the
/// gateway at a webhook that always denies.
/// </summary>
public class SandboxSessionRegistryBuildAuthProvidersTests
{
    [Fact]
    public async Task M365_provider_emitted_when_both_client_id_and_secret_set()
    {
        var auth = new AuthOptions
        {
            M365 = new M365AuthOptions
            {
                ClientId = "1d999ae2-a1f6-44ca-b733-c3df6ce8dc0c",
                ClientSecret = "shh-its-a-secret",
            },
        };

        await using var registry = CreateRegistry(auth);

        registry.GetAuthProviderIdsForTest().Should().Contain("m365-auth");
    }

    [Fact]
    public async Task M365_provider_not_emitted_when_client_secret_missing()
    {
        // The provider stays disabled (its confidential client builder needs both id + secret), so
        // attaching the gateway entry would set up a webhook that always denies — confusing rather
        // than helpful.
        var auth = new AuthOptions
        {
            M365 = new M365AuthOptions
            {
                ClientId = "1d999ae2-a1f6-44ca-b733-c3df6ce8dc0c",
                ClientSecret = null,
            },
        };

        await using var registry = CreateRegistry(auth);

        registry.GetAuthProviderIdsForTest().Should().NotContain("m365-auth");
    }

    [Fact]
    public async Task M365_provider_not_emitted_when_client_id_missing()
    {
        var auth = new AuthOptions
        {
            M365 = new M365AuthOptions
            {
                ClientId = null,
                ClientSecret = "shh",
            },
        };

        await using var registry = CreateRegistry(auth);

        registry.GetAuthProviderIdsForTest().Should().NotContain("m365-auth");
    }

    [Fact]
    public async Task M365_provider_not_emitted_when_client_secret_is_whitespace()
    {
        // Whitespace must be treated the same as missing — appsettings can ship a placeholder.
        var auth = new AuthOptions
        {
            M365 = new M365AuthOptions
            {
                ClientId = "1d999ae2-a1f6-44ca-b733-c3df6ce8dc0c",
                ClientSecret = "   ",
            },
        };

        await using var registry = CreateRegistry(auth);

        registry.GetAuthProviderIdsForTest().Should().NotContain("m365-auth");
    }

    [Fact]
    public async Task No_providers_emitted_when_nothing_configured()
    {
        await using var registry = CreateRegistry(new AuthOptions());

        registry.GetAuthProviderIdsForTest().Should().BeEmpty();
    }

    private static SandboxSessionRegistry CreateRegistry(AuthOptions auth)
    {
        static HttpResponseMessage Unused(HttpRequestMessage _) => new(HttpStatusCode.OK);

        var gateway = new SandboxGatewayLifetime(
            new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(Unused)));

        return new SandboxSessionRegistry(
            gateway,
            new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler(Unused)),
            auth,
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
