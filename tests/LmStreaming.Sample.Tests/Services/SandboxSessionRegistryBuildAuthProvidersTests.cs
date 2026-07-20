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
    public async Task Github_token_injection_scoped_to_api_hosts_only_redirect_chain_is_network_only()
    {
        // SECURITY (credential exposure): the user's GitHub token is injected (Authorization header)
        // ONLY on the github-auth rule, which must cover nothing but GitHub API/git hosts. The Actions
        // run-log redirect chain (results-receiver + the SAS-signed Azure blob) must be reachable via a
        // SEPARATE network-only rule with NO authProvider, so the token is never sent to those hosts.
        var auth = new AuthOptions
        {
            Github = new GitHubAuthOptions { ClientId = "Iv1.deadbeefdeadbeef", ClientSecret = "shh" },
        };

        await using var registry = CreateRegistry(auth);

        var (_, network) = registry.BuildAuthProvidersForTest();

        var authRule = network.Should().ContainSingle(r => r.Id == "github").Subject;
        authRule.AuthProvider.Should().Be("github-auth");
        authRule.Hosts.Should().BeEquivalentTo("github.com", "api.github.com", "codeload.github.com");
        // The token must NEVER be injectable toward the redirect-chain hosts.
        authRule.Hosts.Should().NotContain(h => h.Contains("blob.core.windows.net"));
        authRule.Hosts.Should().NotContain(h => h.Contains("results-receiver"));

        var egressRule = network.Should().ContainSingle(r => r.Id == "github-egress").Subject;
        egressRule.AuthProvider.Should().BeNull(); // network-only: no token injection
        egressRule.Hosts.Should().BeEquivalentTo(
            "results-receiver.actions.githubusercontent.com",
            "*.blob.core.windows.net");
        egressRule.Ports.Should().Equal(443);

        // Defense-in-depth: the webhook host gate must also refuse GitHub-token injection to the
        // redirect-chain hosts (they are no longer in the injectable github host list).
        OAuthProviderHosts.IsAllowed("github", "productionresultssa7.blob.core.windows.net").Should().BeFalse();
        OAuthProviderHosts.IsAllowed("github", "results-receiver.actions.githubusercontent.com").Should().BeFalse();
        OAuthProviderHosts.IsAllowed("github", "api.github.com").Should().BeTrue();
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

            var (providers, network) = registry.BuildAuthProvidersForTest();

            // Exactly one webhook auth-provider, pointing at this entry's predefined route.
            var provider = providers.Should().ContainSingle().Subject;
            provider.Id.Should().Be("predefined-e1");
            provider.Type.Should().Be("webhook");
            provider.Endpoint.Should().EndWith("/api/auth/webhook/predefined-e1");
            provider.CacheTtlSeconds.Should().Be(30); // custom-headers: short TTL for prompt rotation

            // Exactly one allow rule, host-scoped to the entry's host on 443, linked to the provider.
            var rule = network.Should().ContainSingle().Subject;
            rule.Id.Should().Be("predefined-e1");
            rule.Action.Should().Be("allow");
            rule.Hosts.Should().Equal("api.internal.example.com");
            rule.Ports.Should().Equal(443);
            rule.AuthProvider.Should().Be("predefined-e1");
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
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance),
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
