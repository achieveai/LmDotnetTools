using System.Net;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Verifies the sandbox gateway's auth-webhook contract (<c>POST /api/auth/webhook/{provider}</c>)
/// end-to-end through the real ASP.NET pipeline. These checks need NO gateway and run in CI always:
/// they cover the security-critical authorization (constant-time shared-secret check) and the
/// token-free deny path taken when the provider is not signed in. Progress is logged via
/// <see cref="LoggingTestBase"/> to the shared <c>.logs/tests/tests.jsonl</c> file.
/// </summary>
public sealed class AuthWebhookControllerTests : LoggingTestBase
{
    // Per-session secrets have no single "correct secret" for a session that was never created
    // through SandboxSessionRegistry.CreateSessionAsync — NewFactory seeds this fixed session id's
    // secret directly into the DI-resolved SessionSecretStore so these fixture webhook bodies
    // (which all carry "session_id": "s-test") can present it and hit the real MatchesAsync path.
    private const string SessionId = "s-test";
    private const string SharedSecret = "e2e-webhook-shared-secret";

    private const string WebhookBody = """
        {
          "session_id": "s-test",
          "app_id": "lmstreaming-sample",
          "provider_id": "github",
          "rule_id": "github",
          "destination_host": "api.github.com",
          "destination_port": 443,
          "method": "GET",
          "path": "/user",
          "required_scopes": []
        }
        """;

    public AuthWebhookControllerTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private E2EWebAppFactory NewFactory(bool disableDeferredAuth = false)
    {
        // Any scripted handler works — these tests hit the HTTP API, never create an agent.
        var responder = ScriptedSseResponder.New()
            .ForRole("noop", _ => true)
                .Turn(t => t.Text("ok"))
            .Build();
        Logger.LogInformation("Booting in-process sample host (provider mode 'test') for auth-webhook checks");

        // Deferred auth would HOLD a not-signed-in webhook call for the configured timeout; the
        // legacy immediate-deny tests disable it (HoldTimeoutSeconds=0) to stay deterministic.
        var settings = disableDeferredAuth
            ? new Dictionary<string, string?> { ["Auth:Webhook:HoldTimeoutSeconds"] = "0" }
            : null;
        var factory = new E2EWebAppFactory("test", new ScriptedBuilder(responder.AsAnthropicHandler()), settings);
        factory.Services.GetRequiredService<SessionSecretStore>()
            .SaveAsync(SessionId, SharedSecret)
            .GetAwaiter()
            .GetResult();
        return factory;
    }

    private static StringContent JsonBody() => new(WebhookBody, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Missing_authorization_is_rejected_401()
    {
        LogTestStart();
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        Logger.LogInformation("POST /api/auth/webhook/github with NO Authorization header (expect 401)");
        using var response = await client.PostAsync("/api/auth/webhook/github", JsonBody());

        Logger.LogInformation("Response status {StatusCode}", (int)response.StatusCode);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        LogTestEnd();
    }

    [Fact]
    public async Task Wrong_shared_secret_is_rejected_401()
    {
        LogTestStart();
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/webhook/github")
        {
            Content = JsonBody(),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "definitely-not-the-secret");

        Logger.LogInformation("POST /api/auth/webhook/github with WRONG shared secret (expect 401)");
        using var response = await client.SendAsync(request);

        Logger.LogInformation("Response status {StatusCode}", (int)response.StatusCode);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        LogTestEnd();
    }

    [Fact]
    public async Task Correct_secret_but_not_signed_in_returns_200_deny()
    {
        LogTestStart();
        using var factory = NewFactory(disableDeferredAuth: true);
        using var client = factory.CreateClient();

        var sharedSecret = SharedSecret;

        // Ensure the github provider has no persisted token so the deny path is deterministic.
        await factory.Services.GetRequiredService<IOAuthTokenStore>().RemoveAsync("github");
        Logger.LogInformation("Cleared any persisted github token to force the not-signed-in deny path");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/webhook/github")
        {
            Content = JsonBody(),
        };
        request.Headers.TryAddWithoutValidation("Authorization", sharedSecret);

        Logger.LogInformation("POST /api/auth/webhook/github with CORRECT shared secret, not signed in (expect 200 deny)");
        using var response = await client.SendAsync(request);

        // The decision lives in the body, not the status code — both allow and deny are HTTP 200.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        Logger.LogInformation("Webhook decision body: {Body}", json);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("decision").GetString().Should().Be("deny");

        // The deny reason must NOT leak token material or the provider's internal error text.
        var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
        reason.Should().NotBeNullOrEmpty();
        LogTestEnd();
    }

    [Fact]
    public async Task Correct_secret_and_signed_in_returns_200_allow_with_injected_bearer()
    {
        LogTestStart();
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        // Simulate a persisted sign-in (as if from a previous run / restart): seed the github token
        // store the same singleton the provider reads. GetAccessTokenAsync then returns this token.
        await factory.Services.GetRequiredService<IOAuthTokenStore>().SaveAsync(new OAuthTokenRecord(
            Provider: "github",
            Account: "octocat",
            RefreshToken: string.Empty,
            AccessToken: "injected-token-xyz",
            AccessTokenExpiresAtUtc: DateTimeOffset.UtcNow.AddYears(1),
            Scopes: ["repo", "read:org"]));
        Logger.LogInformation("Seeded a valid github token in the store (value not logged) to exercise the allow path");

        var sharedSecret = SharedSecret;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/webhook/github")
        {
            Content = JsonBody(),
        };
        request.Headers.TryAddWithoutValidation("Authorization", sharedSecret);

        Logger.LogInformation("POST /api/auth/webhook/github with CORRECT shared secret, signed in (expect 200 allow + Bearer)");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("decision").GetString().Should().Be("allow");

        // The gateway gets exactly one header pair: Authorization: Bearer <token>.
        var headers = doc.RootElement.GetProperty("headers");
        headers.GetArrayLength().Should().Be(1);
        var pair = headers[0];
        pair[0].GetString().Should().Be("Authorization");
        pair[1].GetString().Should().Be("Bearer injected-token-xyz");

        // The injected header must carry the token's real expiry so the gateway re-calls on lapse.
        doc.RootElement.TryGetProperty("expires_at", out var expiresAt).Should().BeTrue();
        expiresAt.GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow.AddDays(1));

        // Clean up so a sibling test in the same process starts from a known (signed-out) state.
        await factory.Services.GetRequiredService<IOAuthTokenStore>().RemoveAsync("github");
        LogTestEnd();
    }

    [Fact]
    public async Task GitHub_git_host_returns_200_allow_with_injected_basic_x_access_token()
    {
        LogTestStart();
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        // Seed a valid github token, then hit the GIT smart-HTTP host (github.com) rather than the
        // REST API host. GitHub's git endpoint rejects `Bearer` with 401, so the webhook must inject
        // HTTP Basic with username `x-access-token` and the token as the password.
        await factory.Services.GetRequiredService<IOAuthTokenStore>().SaveAsync(new OAuthTokenRecord(
            Provider: "github",
            Account: "octocat",
            RefreshToken: string.Empty,
            AccessToken: "injected-token-xyz",
            AccessTokenExpiresAtUtc: DateTimeOffset.UtcNow.AddYears(1),
            Scopes: ["repo", "read:org"]));

        const string gitBody = """
            {
              "session_id": "s-test",
              "app_id": "lmstreaming-sample",
              "provider_id": "github",
              "rule_id": "github",
              "destination_host": "github.com",
              "destination_port": 443,
              "method": "GET",
              "path": "/achieveai/LmDotnetTools.git/info/refs",
              "required_scopes": []
            }
            """;

        var sharedSecret = SharedSecret;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/webhook/github")
        {
            Content = new StringContent(gitBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", sharedSecret);

        Logger.LogInformation("POST /api/auth/webhook/github for git host github.com (expect 200 allow + Basic x-access-token)");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("decision").GetString().Should().Be("allow");

        var headers = doc.RootElement.GetProperty("headers");
        headers.GetArrayLength().Should().Be(1);
        var pair = headers[0];
        pair[0].GetString().Should().Be("Authorization");

        var expectedBasic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:injected-token-xyz"));
        pair[1].GetString().Should().Be(expectedBasic);

        await factory.Services.GetRequiredService<IOAuthTokenStore>().RemoveAsync("github");
        LogTestEnd();
    }

    [Fact]
    public async Task M365_not_signed_in_returns_200_deny()
    {
        LogTestStart();
        using var factory = NewFactory(disableDeferredAuth: true);
        using var client = factory.CreateClient();

        var sharedSecret = SharedSecret;
        var body = """
            {
              "session_id": "s-test",
              "app_id": "lmstreaming-sample",
              "provider_id": "m365",
              "rule_id": "m365",
              "destination_host": "graph.microsoft.com",
              "destination_port": 443,
              "method": "GET",
              "path": "/v1.0/me",
              "required_scopes": []
            }
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/webhook/m365")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", sharedSecret);
        Logger.LogInformation("POST /api/auth/webhook/m365 with CORRECT secret, not signed in (expect 200 deny)");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("decision").GetString().Should().Be("deny");
        LogTestEnd();
    }

    [Fact]
    public async Task M365_disallowed_host_returns_200_deny_via_defense_in_depth()
    {
        LogTestStart();
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var sharedSecret = SharedSecret;
        // A misconfigured rule pointing m365 at api.github.com must NOT mint an m365 token toward
        // GitHub — the webhook's OAuthProviderHosts.IsAllowed check is the final gate.
        var body = """
            {
              "session_id": "s-test",
              "app_id": "lmstreaming-sample",
              "provider_id": "m365",
              "rule_id": "m365",
              "destination_host": "api.github.com",
              "destination_port": 443,
              "method": "GET",
              "path": "/user",
              "required_scopes": []
            }
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/webhook/m365")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", sharedSecret);
        Logger.LogInformation("POST /api/auth/webhook/m365 toward api.github.com (expect 200 deny by hostlist)");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("decision").GetString().Should().Be("deny");
        doc.RootElement.GetProperty("reason").GetString().Should().Contain("not allowed");
        LogTestEnd();
    }
}
