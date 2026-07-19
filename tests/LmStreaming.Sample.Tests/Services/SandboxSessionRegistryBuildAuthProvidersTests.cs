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

    [Fact]
    public async Task Predefined_key_entry_emits_its_own_webhook_provider()
    {
        var dir = Directory.CreateTempSubdirectory("egr-bap");
        try
        {
            var keys = new PredefinedKeyRegistry(dir.FullName, new NoopTokenStore(), new HttpClient(), NullLoggerFactory.Instance);
            await keys.UpsertAsync(new PredefinedKeyEntry
            {
                Id = "e1",
                Host = "api.internal.example.com",
                Kind = PredefinedKeyKind.CustomHeaders,
                Headers = [new PredefinedHeader("X-Key", "v")],
            });

            await using var registry = CreateRegistry(new AuthOptions(), keys);

            // No OAuth providers configured, so the ONLY emitted provider is the predefined entry's.
            registry.GetAuthProviderIdsForTest().Should().ContainSingle().Which.Should().Be("predefined-e1");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task No_predefined_registry_emits_no_predefined_providers()
    {
        // Fail-closed: the headless daemon (and any caller) that passes no registry gets no keys.
        await using var registry = CreateRegistry(new AuthOptions(), predefinedKeys: null);

        registry.GetAuthProviderIdsForTest().Should().BeEmpty();
    }

    private sealed class NoopTokenStore : IOAuthTokenStore
    {
        public Task<OAuthTokenRecord?> GetAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult<OAuthTokenRecord?>(null);

        public Task SaveAsync(OAuthTokenRecord record, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string provider, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static SandboxSessionRegistry CreateRegistry(AuthOptions auth, PredefinedKeyRegistry? predefinedKeys = null)
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
            new AuthSharedSecret(auth),
            predefinedKeys);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
