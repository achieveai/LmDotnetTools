using System.Net;
using Microsoft.Extensions.Configuration;

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
            M365 = new M365AuthOptions { ClientId = "1d999ae2-a1f6-44ca-b733-c3df6ce8dc0c", ClientSecret = null },
        };

        await using var registry = CreateRegistry(auth);

        registry.GetAuthProviderIdsForTest().Should().NotContain("m365-auth");
    }

    [Fact]
    public async Task M365_provider_not_emitted_when_client_id_missing()
    {
        var auth = new AuthOptions
        {
            M365 = new M365AuthOptions { ClientId = null, ClientSecret = "shh" },
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
            M365 = new M365AuthOptions { ClientId = "1d999ae2-a1f6-44ca-b733-c3df6ce8dc0c", ClientSecret = "   " },
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
        egressRule
            .Hosts.Should()
            .BeEquivalentTo("results-receiver.actions.githubusercontent.com", "*.blob.core.windows.net");
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
            var keys = new PredefinedKeyRegistry(
                dir.FullName,
                new NoopTokenStore(),
                new HttpClient(),
                NullLoggerFactory.Instance
            );
            await keys.UpsertAsync(
                new PredefinedKeyEntry
                {
                    Id = "e1",
                    Host = "api.internal.example.com",
                    Kind = PredefinedKeyKind.CustomHeaders,
                    Headers = [new PredefinedHeader("X-Key", "v")],
                }
            );

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Configured_public_docs_rule_does_not_require_or_inject_oauth(bool configureGithub)
    {
        var options = BindGatewayOptions(
            new Dictionary<string, string?>
            {
                ["Network:Rules:public-docs:Hosts"] = "docs.stripe.com,*.apple.com",
                ["Network:Rules:public-docs:Ports"] = "443",
                ["Network:Rules:public-docs:Methods"] = "GET",
                ["Network:Rules:public-docs:Paths"] = "*",
            }
        );
        var auth = new AuthOptions();
        if (configureGithub)
        {
            auth.Github.ClientId = "test-client";
        }

        await using var registry = CreateRegistry(auth, options: options);
        var (providers, network) = registry.BuildAuthProvidersForTest();
        var rule = network.Should().ContainSingle(r => r.Id == "public-docs").Subject;
        rule.Action.Should().Be("allow");
        rule.Hosts.Should().BeEquivalentTo("docs.stripe.com", "*.apple.com");
        rule.Methods.Should().Equal("GET");
        rule.Ports.Should().Equal(443);
        rule.AuthProvider.Should().BeNull();
        rule.RequiredScopes.Should().BeEmpty();
        if (configureGithub)
        {
            providers.Should().ContainSingle().Which.Id.Should().Be("github-auth");
            network.Should().ContainSingle(r => r.Id == "github").Which.AuthProvider.Should().Be("github-auth");
        }
        else
        {
            providers.Should().BeNull();
            network.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task Unconfigured_shared_options_emit_no_network_rules()
    {
        await using var registry = CreateRegistry(new AuthOptions());
        var (providers, network) = registry.BuildAuthProvidersForTest();
        providers.Should().BeNull();
        network.Should().BeNull();
    }

    [Fact]
    public async Task Config_rule_named_after_a_managed_rule_replaces_it()
    {
        // github-egress is a managed network-only rule; naming it in configuration REPLACES it whole,
        // so an operator can retarget the Actions redirect chain without a code change.
        var options = BindGatewayOptions(
            new Dictionary<string, string?>
            {
                ["Network:Rules:github-egress:Hosts"] = "mirror.example.com",
                ["Network:Rules:github-egress:Ports"] = "443",
                ["Network:Rules:github-egress:Methods"] = "GET",
                ["Network:Rules:github-egress:Priority"] = "120",
            }
        );
        var auth = new AuthOptions { Github = new GitHubAuthOptions { ClientId = "test-client" } };

        await using var registry = CreateRegistry(auth, options: options);
        var (_, network) = registry.BuildAuthProvidersForTest();

        var rule = network.Should().ContainSingle(r => r.Id == "github-egress").Subject;
        rule.Hosts.Should().Equal("mirror.example.com");
        rule.Priority.Should().Be(120);
        // The managed github (token-injecting) rule is untouched.
        network.Should().ContainSingle(r => r.Id == "github").Which.AuthProvider.Should().Be("github-auth");
    }

    [Fact]
    public async Task Disabled_config_rule_named_after_a_managed_rule_removes_it()
    {
        var options = BindGatewayOptions(
            new Dictionary<string, string?> { ["Network:Rules:github-egress:Enabled"] = "false" }
        );
        var auth = new AuthOptions { Github = new GitHubAuthOptions { ClientId = "test-client" } };

        await using var registry = CreateRegistry(auth, options: options);
        var (_, network) = registry.BuildAuthProvidersForTest();

        network.Should().NotContain(r => r.Id == "github-egress");
        network.Should().ContainSingle(r => r.Id == "github");
    }

    [Fact]
    public async Task Config_providers_never_replace_managed_providers()
    {
        // A configured provider always lands under the cfg- wire prefix, so it can never collide with
        // (or shadow) a managed provider identity.
        var options = BindGatewayOptions(
            new Dictionary<string, string?>
            {
                ["AuthProviders:partner-headers:Type"] = "headers",
                ["AuthProviders:partner-headers:Hosts"] = "api.example.com",
                ["AuthProviders:partner-headers:Headers:X-Api-Key:Value"] = "v",
            }
        );
        var auth = new AuthOptions { Github = new GitHubAuthOptions { ClientId = "test-client" } };

        await using var registry = CreateRegistry(auth, options: options);
        var (providers, _) = registry.BuildAuthProvidersForTest();

        providers!.Select(p => p.Id).Should().BeEquivalentTo("github-auth", "cfg-partner-headers");
    }

    [Fact]
    public async Task Header_values_never_reach_the_sandbox_create_request()
    {
        const string Canary = "sk-live-CANARY-NEVER-LEAKS";
        var options = BindGatewayOptions(
            new Dictionary<string, string?>
            {
                ["AuthProviders:partner-headers:Type"] = "headers",
                ["AuthProviders:partner-headers:Hosts"] = "api.example.com",
                ["AuthProviders:partner-headers:Headers:X-Api-Key:Value"] = Canary,
                ["Network:Rules:partner-api:Hosts"] = "api.example.com",
                ["Network:Rules:partner-api:Ports"] = "443",
                ["Network:Rules:partner-api:Methods"] = "GET",
                ["Network:Rules:partner-api:AuthProvider"] = "partner-headers",
                ["Network:Rules:partner-api:Priority"] = "100",
            }
        );

        await using var registry = CreateRegistry(new AuthOptions(), options: options);
        var (providers, network) = registry.BuildAuthProvidersForTest();

        var serialized = JsonSerializer.Serialize(new { providers, network });
        serialized.Should().NotContain(Canary);
        network
            .Should()
            .ContainSingle(r => r.Id == "partner-api")
            .Which.AuthProvider.Should()
            .Be("cfg-partner-headers");
    }

    [Fact]
    public async Task Broad_allow_shadowing_a_managed_oauth_gate_fails_closed()
    {
        // The managed github gate sits at priority 100. A *.github.com allow at 50 is evaluated FIRST,
        // so GitHub egress would proceed with no credential gate at all. The gateway hard-rejects this
        // at sandbox-create; catching it here turns a 400 into an actionable failure.
        var options = BindGatewayOptions(
            new Dictionary<string, string?>
            {
                ["Network:Rules:broad:Hosts"] = "*.github.com",
                ["Network:Rules:broad:Ports"] = "443",
                ["Network:Rules:broad:Methods"] = "GET",
                ["Network:Rules:broad:Priority"] = "50",
            }
        );
        var auth = new AuthOptions { Github = new GitHubAuthOptions { ClientId = "test-client" } };

        await using var registry = CreateRegistry(auth, options: options);

        var build = () => registry.BuildAuthProvidersForTest();
        build.Should().Throw<ArgumentException>().WithMessage("*broad*").WithMessage("*github*");
    }

    [Fact]
    public async Task Broad_allow_behind_a_managed_oauth_gate_is_allowed()
    {
        // Same rule at 500: first-match-wins by ascending priority means the managed gate still wins.
        var options = BindGatewayOptions(
            new Dictionary<string, string?>
            {
                ["Network:Rules:broad:Hosts"] = "*.github.com",
                ["Network:Rules:broad:Ports"] = "443",
                ["Network:Rules:broad:Methods"] = "GET",
                ["Network:Rules:broad:Priority"] = "500",
            }
        );
        var auth = new AuthOptions { Github = new GitHubAuthOptions { ClientId = "test-client" } };

        await using var registry = CreateRegistry(auth, options: options);
        var (_, network) = registry.BuildAuthProvidersForTest();

        network.Should().ContainSingle(r => r.Id == "broad");
        network.Should().ContainSingle(r => r.Id == "github").Which.AuthProvider.Should().Be("github-auth");
    }

    [Fact]
    public async Task Broad_allow_shadowing_a_predefined_key_gate_fails_closed()
    {
        // Predefined egress keys are runtime state, invisible to the config-only startup check — they
        // only appear in the MERGED rule set, which is why precedence is re-validated after the merge.
        var dir = Directory.CreateTempSubdirectory("egr-prec");
        try
        {
            var keys = new PredefinedKeyRegistry(
                dir.FullName,
                new NoopTokenStore(),
                new HttpClient(),
                NullLoggerFactory.Instance
            );
            await keys.UpsertAsync(
                new PredefinedKeyEntry
                {
                    Id = "e1",
                    Host = "api.example.com",
                    Kind = PredefinedKeyKind.CustomHeaders,
                    Headers = [new PredefinedHeader("X-Key", "v")],
                }
            );

            var options = BindGatewayOptions(
                new Dictionary<string, string?>
                {
                    ["Network:Rules:broad:Hosts"] = "*.example.com",
                    ["Network:Rules:broad:Ports"] = "443",
                    ["Network:Rules:broad:Methods"] = "GET",
                    ["Network:Rules:broad:Priority"] = "50",
                }
            );

            await using var registry = CreateRegistry(new AuthOptions(), keys, options);

            var build = () => registry.BuildAuthProvidersForTest();
            build.Should().Throw<ArgumentException>().WithMessage("*broad*").WithMessage("*predefined-e1*");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Authenticated_broad_allow_shadowing_another_config_gate_fails_closed()
    {
        // A wildcard allow that CARRIES an auth provider is still a broad allow to the gateway — it just
        // cannot shadow itself. Here it is evaluated in front of a different gate.
        var options = BindGatewayOptions(
            new Dictionary<string, string?>
            {
                ["AuthProviders:partner-headers:Type"] = "headers",
                ["AuthProviders:partner-headers:Hosts"] = "*.x.com",
                ["AuthProviders:partner-headers:Headers:X-Api-Key:Value"] = "v",
                ["Network:Rules:wide-auth:Hosts"] = "*.x.com",
                ["Network:Rules:wide-auth:Ports"] = "443",
                ["Network:Rules:wide-auth:Methods"] = "GET",
                ["Network:Rules:wide-auth:AuthProvider"] = "partner-headers",
                ["Network:Rules:wide-auth:Priority"] = "50",
                ["Network:Rules:blocked:Hosts"] = "*.x.com",
                ["Network:Rules:blocked:Ports"] = "443",
                ["Network:Rules:blocked:Methods"] = "*",
                ["Network:Rules:blocked:Action"] = "deny",
                ["Network:Rules:blocked:Priority"] = "100",
            }
        );

        await using var registry = CreateRegistry(new AuthOptions(), options: options);

        var build = () => registry.BuildAuthProvidersForTest();
        build.Should().Throw<ArgumentException>().WithMessage("*wide-auth*").WithMessage("*blocked*");
    }

    [Fact]
    public async Task Config_rule_replacing_a_managed_rule_is_not_judged_against_the_rule_it_replaced()
    {
        // A rule keyed `github` REPLACES the managed github gate, so after the merge there is no gate
        // left for it to shadow — the wildcard is the whole policy for those hosts, by the operator's
        // explicit choice. Judging it against the replaced rule would reject a legal configuration.
        var options = BindGatewayOptions(
            new Dictionary<string, string?>
            {
                ["Network:Rules:github:Hosts"] = "*.github.com",
                ["Network:Rules:github:Ports"] = "443",
                ["Network:Rules:github:Methods"] = "GET",
                ["Network:Rules:github:Priority"] = "100",
            }
        );
        var auth = new AuthOptions { Github = new GitHubAuthOptions { ClientId = "test-client" } };

        await using var registry = CreateRegistry(auth, options: options);
        var (providers, network) = registry.BuildAuthProvidersForTest();

        var rule = network.Should().ContainSingle(r => r.Id == "github").Subject;
        rule.Hosts.Should().Equal("*.github.com");
        rule.AuthProvider.Should().BeNull(); // the config rule, not the managed one
        // The managed provider entry survives; only the RULE was replaced.
        providers.Should().ContainSingle(p => p.Id == "github-auth");
    }

    [Fact]
    public async Task Invalid_egress_configuration_fails_closed()
    {
        var options = BindGatewayOptions(
            new Dictionary<string, string?>
            {
                ["Network:Rules:bad:Hosts"] = "localhost",
                ["Network:Rules:bad:Ports"] = "443",
                ["Network:Rules:bad:Methods"] = "GET",
            }
        );
        await using var registry = CreateRegistry(new AuthOptions(), options: options);

        var build = () => registry.BuildAuthProvidersForTest();
        build.Should().Throw<ArgumentException>().WithMessage("*bad*");
    }

    [Fact]
    public async Task Invalid_egress_configuration_error_never_echoes_the_configured_value()
    {
        // F-002. The ArgumentException message is what reaches startup logs, so it must name the rule
        // and field but never the offending value.
        var options = BindGatewayOptions(
            new Dictionary<string, string?>
            {
                ["Network:Rules:leaky:Hosts"] = "api.example.com",
                ["Network:Rules:leaky:Ports"] = "9x-CANARY",
                ["Network:Rules:leaky:Methods"] = "GET",
            }
        );
        await using var registry = CreateRegistry(new AuthOptions(), options: options);

        var build = () => registry.BuildAuthProvidersForTest();
        build
            .Should()
            .Throw<ArgumentException>()
            .Which.Message.Should()
            .Contain("leaky")
            .And.Contain("Ports")
            .And.NotContain("9x-CANARY");
    }

    [Fact]
    public async Task Sample_config_emits_only_approved_documentation_hosts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LmDotnetTools.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the checked-in sample configuration must be tested");
        var options = new SandboxGatewayOptions();
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(directory!.FullName, "samples", "LmStreaming.Sample", "appsettings.json"))
            .Build()
            .GetSection("SandboxGateway")
            .Bind(options);
        await using var registry = CreateRegistry(new AuthOptions(), options: options);

        var (providers, network) = registry.BuildAuthProvidersForTest();
        providers.Should().BeNull();
        var rule = network.Should().ContainSingle().Subject;
        rule.Hosts.Should()
            .BeEquivalentTo(
                "developer.apple.com",
                "*.apple.com",
                "developers.google.com",
                "developer.android.com",
                "firebase.google.com",
                "ai.google.dev",
                "docs.cloud.google.com",
                "docs.stripe.com",
                "learn.microsoft.com",
                "docs.microsoft.com"
            );
        rule.Id.Should().Be("public-docs");
        rule.Action.Should().Be("allow");
        rule.Methods.Should().Equal("GET");
        rule.Ports.Should().Equal(443);
        rule.AuthProvider.Should().BeNull();
    }

    /// <summary>Binds an in-memory <c>SandboxGateway</c>-shaped configuration onto fresh options.</summary>
    private static SandboxGatewayOptions BindGatewayOptions(Dictionary<string, string?> values)
    {
        var options = new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" };
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().Bind(options);
        return options;
    }

    private sealed class NoopTokenStore : IOAuthTokenStore
    {
        public Task<OAuthTokenRecord?> GetAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult<OAuthTokenRecord?>(null);

        public Task SaveAsync(OAuthTokenRecord record, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string provider, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static SandboxSessionRegistry CreateRegistry(
        AuthOptions auth,
        PredefinedKeyRegistry? predefinedKeys = null,
        SandboxGatewayOptions? options = null
    )
    {
        static HttpResponseMessage Unused(HttpRequestMessage _) => new(HttpStatusCode.OK);
        options ??= new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" };

        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(Unused))
        );

        return new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler(Unused)),
            auth,
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            ),
            predefinedKeys
        );
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }
}
