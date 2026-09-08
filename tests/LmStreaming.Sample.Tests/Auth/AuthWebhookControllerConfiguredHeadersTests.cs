using AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Auth;

/// <summary>
/// The <c>cfg-</c> (appsettings-configured) branch of <see cref="AuthWebhookController"/>: a
/// <c>headers</c>-type egress provider injects its configured request headers when — and only when —
/// the session secret authenticates, the provider exists and is enabled, the destination is inside
/// the provider's host scope on 443, and the first matching CONFIG rule is an allow rule that both
/// names this provider and carries the rule id the gateway reported. Every other shape denies.
/// </summary>
public sealed class AuthWebhookControllerConfiguredHeadersTests
{
    private const string Secret = "test-shared-secret";
    private const string SessionId = "session-1";
    private const string HeaderValue = "sk-live-CANARY-NEVER-LEAKS";

    /// <summary>
    /// Baseline configuration: a headers provider scoped to api.example.com plus an allow rule that
    /// references it on GET/POST under /v1/*. Individual tests overlay keys onto this.
    /// </summary>
    private static Dictionary<string, string?> BaseConfig() =>
        new()
        {
            ["AuthProviders:partner-headers:Type"] = "headers",
            ["AuthProviders:partner-headers:Hosts"] = "api.example.com",
            ["AuthProviders:partner-headers:CacheTtlSeconds"] = "30",
            ["AuthProviders:partner-headers:Headers:X-Api-Key:Value"] = HeaderValue,
            ["AuthProviders:partner-headers:Headers:X-Client-Name:Value"] = "LmStreaming.Sample",
            ["AuthProviders:partner-headers:Headers:X-Disabled:Value"] = "nope",
            ["AuthProviders:partner-headers:Headers:X-Disabled:Enabled"] = "false",
            ["Network:Rules:partner-api:Hosts"] = "api.example.com",
            ["Network:Rules:partner-api:Ports"] = "443",
            ["Network:Rules:partner-api:Methods"] = "GET,POST",
            ["Network:Rules:partner-api:Paths"] = "/v1/*",
            ["Network:Rules:partner-api:AuthProvider"] = "partner-headers",
            ["Network:Rules:partner-api:Priority"] = "200",
        };

    private static AuthWebhookController CreateController(
        Dictionary<string, string?> config,
        string authorization = Secret
    )
    {
        var options = new SandboxGatewayOptions();
        new ConfigurationBuilder().AddInMemoryCollection(config).Build().Bind(options);

        var sessionSecretStore = new SessionSecretStore(
            Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
            NullLogger<SessionSecretStore>.Instance
        );
        sessionSecretStore.SaveAsync(SessionId, Secret).GetAwaiter().GetResult();

        var controller = new AuthWebhookController(
            [],
            sessionSecretStore,
            new DenyingPolicy(),
            new NoopForwarder(),
            new AuthOptions(),
            NullLogger<AuthWebhookController>.Instance,
            predefinedKeys: null,
            gatewayOptions: options
        );

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = authorization;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static AuthWebhookRequest NewRequest(
        string providerId = "cfg-partner-headers",
        string ruleId = "partner-api",
        string host = "api.example.com",
        int port = 443,
        string method = "GET",
        string path = "/v1/items"
    ) =>
        new()
        {
            SessionId = SessionId,
            AppId = "lmstreaming-sample",
            ProviderId = providerId,
            RuleId = ruleId,
            DestinationHost = host,
            DestinationPort = port,
            Method = method,
            Path = path,
            RequiredScopes = [],
        };

    private static AuthWebhookResponse Decision(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<AuthWebhookResponse>().Subject;

    [Fact]
    public async Task Allows_and_injects_every_enabled_header_verbatim()
    {
        var controller = CreateController(BaseConfig());

        var decision = Decision(await controller.Evaluate("cfg-partner-headers", NewRequest(), CancellationToken.None));

        decision.Decision.Should().Be("allow");
        string[][] expected =
        [
            ["X-Api-Key", HeaderValue],
            ["X-Client-Name", "LmStreaming.Sample"],
        ];
        decision.Headers.Should().BeEquivalentTo(expected);
        decision.Headers.Should().NotContain(h => h[0] == "X-Disabled");
        // Static values carry no lifetime of their own; the expiry mirrors the provider cache TTL so a
        // rotated value takes effect promptly.
        decision.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(30), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Allows_when_the_rule_references_the_provider_in_a_different_casing()
    {
        // Configuration keys are case-insensitive, so an operator may spell the reference differently
        // from the provider key. The compiled rule must still carry the provider's own id, or the
        // ordinal comparison here (matching the gateway's exact lookup) would deny every request.
        var config = BaseConfig();
        config["Network:Rules:partner-api:AuthProvider"] = "PARTNER-headers";
        var controller = CreateController(config);

        var decision = Decision(await controller.Evaluate("cfg-partner-headers", NewRequest(), CancellationToken.None));

        decision.Decision.Should().Be("allow");
        decision.Headers.Should().HaveCount(2);
    }

    [Fact]
    public async Task Denies_when_the_session_secret_does_not_match()
    {
        var controller = CreateController(BaseConfig(), authorization: "wrong-secret");

        var result = await controller.Evaluate("cfg-partner-headers", NewRequest(), CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    public static TheoryData<string, string, AuthWebhookRequest> DeniedRequests() =>
        new()
        {
            { "unknown cfg provider", "cfg-nope", NewRequest(providerId: "cfg-nope") },
            { "provider_id does not match the route", "cfg-partner-headers", NewRequest(providerId: "cfg-other") },
            {
                "destination host outside the provider scope",
                "cfg-partner-headers",
                NewRequest(host: "evil.example.org")
            },
            { "cleartext / non-443 port", "cfg-partner-headers", NewRequest(port: 8443) },
            { "method the rule does not allow", "cfg-partner-headers", NewRequest(method: "DELETE") },
            { "path the rule does not cover", "cfg-partner-headers", NewRequest(path: "/v2/items") },
            {
                "rule_id the gateway reported is not the matched rule",
                "cfg-partner-headers",
                NewRequest(ruleId: "other-rule")
            },
        };

    [Theory]
    [MemberData(nameof(DeniedRequests))]
    public async Task Denies_the_request(string because, string route, AuthWebhookRequest body)
    {
        var controller = CreateController(BaseConfig());

        var decision = Decision(await controller.Evaluate(route, body, CancellationToken.None));

        decision.Decision.Should().Be("deny", because);
        decision.Headers.Should().BeNull();
    }

    [Fact]
    public async Task Denies_a_disabled_provider()
    {
        var config = BaseConfig();
        config["AuthProviders:partner-headers:Enabled"] = "false";
        config.Remove("Network:Rules:partner-api:AuthProvider"); // keep the rule itself valid
        var controller = CreateController(config);

        var decision = Decision(await controller.Evaluate("cfg-partner-headers", NewRequest(), CancellationToken.None));

        decision.Decision.Should().Be("deny");
    }

    [Fact]
    public async Task Denies_a_webhook_type_provider_because_the_gateway_calls_its_endpoint_not_us()
    {
        var config = new Dictionary<string, string?>
        {
            ["AuthProviders:partner-webhook:Type"] = "webhook",
            ["AuthProviders:partner-webhook:Hosts"] = "api.example.com",
            ["AuthProviders:partner-webhook:Endpoint"] = "https://auth.example.com/egress",
            ["AuthProviders:partner-webhook:GatewayAuth"] = "external-secret",
            ["Network:Rules:partner-api:Hosts"] = "api.example.com",
            ["Network:Rules:partner-api:Ports"] = "443",
            ["Network:Rules:partner-api:Methods"] = "GET",
            ["Network:Rules:partner-api:AuthProvider"] = "partner-webhook",
        };
        var controller = CreateController(config);

        var decision = Decision(
            await controller.Evaluate(
                "cfg-partner-webhook",
                NewRequest(providerId: "cfg-partner-webhook"),
                CancellationToken.None
            )
        );

        decision.Decision.Should().Be("deny");
    }

    [Fact]
    public async Task Denies_when_a_higher_priority_config_deny_rule_covers_the_destination()
    {
        // First-match-wins by ASCENDING priority: the deny at 100 beats the authenticated allow at 200,
        // so the webhook must refuse to inject even though a matching allow rule exists.
        var config = BaseConfig();
        config["Network:Rules:block-items:Hosts"] = "api.example.com";
        config["Network:Rules:block-items:Ports"] = "443";
        config["Network:Rules:block-items:Methods"] = "*";
        config["Network:Rules:block-items:Paths"] = "/v1/items";
        config["Network:Rules:block-items:Action"] = "deny";
        config["Network:Rules:block-items:Priority"] = "100";
        var controller = CreateController(config);

        var decision = Decision(await controller.Evaluate("cfg-partner-headers", NewRequest(), CancellationToken.None));

        decision.Decision.Should().Be("deny");
    }

    [Fact]
    public async Task Denies_when_the_host_has_no_gateway_options_at_all()
    {
        // Other hosts (e.g. the daemon) construct the controller without SandboxGatewayOptions; the
        // cfg- branch must fail closed rather than throw.
        var sessionSecretStore = new SessionSecretStore(
            Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
            NullLogger<SessionSecretStore>.Instance
        );
        await sessionSecretStore.SaveAsync(SessionId, Secret);
        var controller = new AuthWebhookController(
            [],
            sessionSecretStore,
            new DenyingPolicy(),
            new NoopForwarder(),
            new AuthOptions(),
            NullLogger<AuthWebhookController>.Instance
        );
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = Secret;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var decision = Decision(await controller.Evaluate("cfg-partner-headers", NewRequest(), CancellationToken.None));

        decision.Decision.Should().Be("deny");
    }

    [Fact]
    public async Task Denies_rather_than_throwing_when_the_policy_was_never_validated()
    {
        // A programmatic consumer can build SandboxGatewayOptions in code and skip the validator, so the
        // cfg- branch must survive malformed policy (here an unparseable port) with a 200 deny — an
        // unhandled 500 would surface gateway-side as an opaque webhook failure, not a clean decision.
        var options = new SandboxGatewayOptions
        {
            AuthProviders =
            {
                ["partner-headers"] = new SandboxEgressAuthProviderOptions
                {
                    Type = "headers",
                    Hosts = "api.example.com",
                    Headers = { ["X-Api-Key"] = new SandboxEgressHeaderOptions { Value = "v" } },
                },
            },
            Network =
            {
                Rules =
                {
                    ["partner-api"] = new SandboxNetworkRuleOptions
                    {
                        Hosts = "api.example.com",
                        Ports = "abc",
                        Methods = "GET",
                        AuthProvider = "partner-headers",
                    },
                },
            },
        };

        var sessionSecretStore = new SessionSecretStore(
            Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
            NullLogger<SessionSecretStore>.Instance
        );
        await sessionSecretStore.SaveAsync(SessionId, Secret);
        var controller = new AuthWebhookController(
            [],
            sessionSecretStore,
            new DenyingPolicy(),
            new NoopForwarder(),
            new AuthOptions(),
            NullLogger<AuthWebhookController>.Instance,
            predefinedKeys: null,
            gatewayOptions: options
        );
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = Secret;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var decision = Decision(await controller.Evaluate("cfg-partner-headers", NewRequest(), CancellationToken.None));

        decision.Decision.Should().Be("deny");
        decision.Headers.Should().BeNull();
    }

    private sealed class DenyingPolicy : IAuthResolutionPolicy
    {
        public Task<OAuthAccessToken?> ResolveAsync(
            IOAuthTokenProvider provider,
            IReadOnlyList<string>? scopes,
            CancellationToken cancellationToken
        ) => Task.FromResult<OAuthAccessToken?>(null);
    }

    private sealed class NoopForwarder : IAuthWebhookForwarder
    {
        public Task<AuthWebhookTarget?> NotifyAuthRequiredAsync(
            string sessionId,
            string providerId,
            string signinUrl,
            string reason,
            CancellationToken ct
        ) => Task.FromResult<AuthWebhookTarget?>(null);

        public Task NotifyAuthCompletedAsync(
            AuthWebhookTarget? target,
            string sessionId,
            string providerId,
            CancellationToken ct
        ) => Task.CompletedTask;

        public Task NotifyAuthDeniedAsync(
            AuthWebhookTarget? target,
            string sessionId,
            string providerId,
            string reason,
            CancellationToken ct
        ) => Task.CompletedTask;
    }
}
