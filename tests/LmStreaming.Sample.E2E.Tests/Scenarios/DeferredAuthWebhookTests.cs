using System.Diagnostics;
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
/// End-to-end coverage of DEFERRED AUTH on the sandbox gateway's auth webhook, with the test
/// playing the gateway (no real gateway, no real OAuth): a not-signed-in
/// <c>POST /api/auth/webhook/{provider}</c> is HELD while connected chat clients receive an
/// <c>auth_required</c> WebSocket frame; once a token lands in the store (here: seeded directly,
/// standing in for the user completing the interactive sign-in) the held call resolves
/// <c>allow</c> and clients receive <c>auth_completed</c>.
/// </summary>
public sealed class DeferredAuthWebhookTests : LoggingTestBase
{
    // Per-session secrets have no single "correct secret" for a session that was never created
    // through SandboxSessionRegistry.CreateSessionAsync — NewFactory seeds this fixed session id's
    // secret directly into the DI-resolved SessionSecretStore so these fixture webhook bodies
    // (which all carry "session_id": "s-test") can present it and hit the real MatchesAsync path.
    private const string SessionId = "s-test";
    private const string SharedSecret = "e2e-deferred-webhook-shared-secret";

    private const string GitHubWebhookBody = """
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

    public DeferredAuthWebhookTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private E2EWebAppFactory NewFactory(int holdTimeoutSeconds = 15)
    {
        var responder = ScriptedSseResponder.New()
            .ForRole("noop", _ => true)
                .Turn(t => t.Text("ok"))
            .Build();
        var factory = new E2EWebAppFactory(
            "test",
            new ScriptedBuilder(responder.AsAnthropicHandler()),
            new Dictionary<string, string?>
            {
                ["Auth:Webhook:HoldTimeoutSeconds"] = holdTimeoutSeconds.ToString(),
                ["Auth:Webhook:PollIntervalSeconds"] = "0.2",
            });
        factory.Services.GetRequiredService<SessionSecretStore>()
            .SaveAsync(SessionId, SharedSecret)
            .GetAwaiter()
            .GetResult();
        return factory;
    }

    private static Task<HttpResponseMessage> PostWebhookAsync(
        E2EWebAppFactory factory,
        HttpClient client,
        string provider = "github",
        string? body = null,
        CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/auth/webhook/{provider}")
        {
            Content = new StringContent(body ?? GitHubWebhookBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret);
        return client.SendAsync(request, ct);
    }

    private static Task SeedGitHubTokenAsync(E2EWebAppFactory factory, string token = "deferred-token-abc") =>
        factory.Services.GetRequiredService<IOAuthTokenStore>().SaveAsync(new OAuthTokenRecord(
            Provider: "github",
            Account: "octocat",
            RefreshToken: string.Empty,
            AccessToken: token,
            AccessTokenExpiresAtUtc: DateTimeOffset.UtcNow.AddYears(1),
            Scopes: ["repo", "read:org"]));

    private static Task ClearGitHubTokenAsync(E2EWebAppFactory factory) =>
        factory.Services.GetRequiredService<IOAuthTokenStore>().RemoveAsync("github");

    private static bool IsFrame(JsonDocument doc, string type, string? providerId = null)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("$type", out var typeProp)
            || !string.Equals(typeProp.GetString(), type, StringComparison.Ordinal))
        {
            return false;
        }

        return providerId is null
            || (doc.RootElement.TryGetProperty("providerId", out var p)
                && string.Equals(p.GetString(), providerId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Held_webhook_pushes_auth_required_then_allows_after_token_lands()
    {
        LogTestStart();
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        await ClearGitHubTokenAsync(factory);

        await using var ws = new WebSocketTestClient(
            await factory.ConnectWebSocketAsync("deferred-auth-happy"));
        Logger.LogInformation("WebSocket connected; POSTing webhook with no token (expect it to be HELD)");

        var webhookTask = PostWebhookAsync(factory, client);

        using (var required = await ws.WaitForFrameAsync(
            f => IsFrame(f, "auth_required", "github"),
            TimeSpan.FromSeconds(10)))
        {
            Logger.LogInformation("Received auth_required frame: {Frame}", required.RootElement.ToString());
            required.RootElement.GetProperty("signinUrl").GetString().Should().Be("/auth/github");
            required.RootElement.GetProperty("reason").GetString().Should().Contain("github");
        }

        webhookTask.IsCompleted.Should().BeFalse("the webhook must stay held until a token lands");

        Logger.LogInformation("Seeding github token (stands in for the user completing sign-in)");
        await SeedGitHubTokenAsync(factory);

        using var response = await webhookTask;
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(json))
        {
            doc.RootElement.GetProperty("decision").GetString().Should().Be("allow");
            var pair = doc.RootElement.GetProperty("headers")[0];
            pair[0].GetString().Should().Be("Authorization");
            pair[1].GetString().Should().Be("Bearer deferred-token-abc");
        }

        using (var completed = await ws.WaitForFrameAsync(
            f => IsFrame(f, "auth_completed", "github"),
            TimeSpan.FromSeconds(10)))
        {
            Logger.LogInformation("Received auth_completed frame");
        }

        await ClearGitHubTokenAsync(factory);
        LogTestEnd();
    }

    [Fact]
    public async Task Held_webhook_denies_after_hold_timeout()
    {
        LogTestStart();
        using var factory = NewFactory(holdTimeoutSeconds: 2);
        using var client = factory.CreateClient();
        await ClearGitHubTokenAsync(factory);

        var stopwatch = Stopwatch.StartNew();
        using var response = await PostWebhookAsync(factory, client);
        stopwatch.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("decision").GetString().Should().Be("deny");
        doc.RootElement.GetProperty("reason").GetString().Should().Contain("sign in required");

        // The call must actually have been held for (about) the configured timeout, not denied
        // immediately. Allow slack below 2s for timer coarseness.
        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5));
        Logger.LogInformation("Webhook denied after {Elapsed} (hold timeout 2s)", stopwatch.Elapsed);
        LogTestEnd();
    }

    [Fact]
    public async Task Concurrent_held_webhooks_emit_single_auth_required_and_both_allow()
    {
        LogTestStart();
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        await ClearGitHubTokenAsync(factory);

        await using var ws = new WebSocketTestClient(
            await factory.ConnectWebSocketAsync("deferred-auth-concurrent"));

        var webhook1 = PostWebhookAsync(factory, client);
        var webhook2 = PostWebhookAsync(factory, client);

        // Count auth_required frames while waiting for auth_completed: exactly one prompt is
        // expected no matter how many webhook calls are held for the same provider.
        var authRequiredCount = 0;
        var seeded = false;
        using var completed = await ws.WaitForFrameAsync(
            f =>
            {
                if (IsFrame(f, "auth_required", "github"))
                {
                    authRequiredCount++;
                    if (!seeded)
                    {
                        seeded = true;
                        // Seed only after the prompt is observed so both calls are provably held.
                        SeedGitHubTokenAsync(factory).GetAwaiter().GetResult();
                    }
                }

                return IsFrame(f, "auth_completed", "github");
            },
            TimeSpan.FromSeconds(15));

        authRequiredCount.Should().Be(1, "concurrent holds for the same provider must prompt once");

        using var response1 = await webhook1;
        using var response2 = await webhook2;
        foreach (var response in new[] { response1, response2 })
        {
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("decision").GetString().Should().Be("allow");
        }

        await ClearGitHubTokenAsync(factory);
        LogTestEnd();
    }

    [Fact]
    public async Task Connection_opened_after_hold_receives_replayed_auth_required()
    {
        LogTestStart();
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        await ClearGitHubTokenAsync(factory);

        // Hold the webhook FIRST — no chat client is connected yet, so the broadcast goes nowhere.
        var webhookTask = PostWebhookAsync(factory, client);

        // Wait until the hold is registered before connecting (the coordinator snapshot is the
        // same signal the replay path reads).
        var coordinator = factory.Services.GetRequiredService<PendingAuthCoordinator>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (coordinator.Snapshot().Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        coordinator.Snapshot().Should().ContainSingle("the webhook must be held before the client connects");

        // A client connecting mid-hold must still be prompted (replay-on-connect).
        await using var ws = new WebSocketTestClient(
            await factory.ConnectWebSocketAsync("deferred-auth-late-join"));
        using (var required = await ws.WaitForFrameAsync(
            f => IsFrame(f, "auth_required", "github"),
            TimeSpan.FromSeconds(10)))
        {
            required.RootElement.GetProperty("signinUrl").GetString().Should().Be("/auth/github");
        }

        await SeedGitHubTokenAsync(factory);
        using var response = await webhookTask;
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("decision").GetString().Should().Be("allow");

        await ClearGitHubTokenAsync(factory);
        LogTestEnd();
    }

    [Fact]
    public async Task Disallowed_host_denies_immediately_without_auth_required_frame()
    {
        LogTestStart();
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        await using var ws = new WebSocketTestClient(
            await factory.ConnectWebSocketAsync("deferred-auth-hostlist"));

        // m365 token toward api.github.com: blocked by the host allowlist BEFORE token acquisition,
        // so the deny must be immediate (no hold) and no sign-in prompt must be broadcast.
        const string body = """
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

        var stopwatch = Stopwatch.StartNew();
        using var response = await PostWebhookAsync(factory, client, provider: "m365", body: body);
        stopwatch.Stop();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("decision").GetString().Should().Be("deny");
        doc.RootElement.GetProperty("reason").GetString().Should().Contain("not allowed");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "host-allowlist denies must not be held");

        var waitForPrompt = async () => await ws.WaitForFrameAsync(
            f => IsFrame(f, "auth_required"),
            TimeSpan.FromSeconds(1.5));
        _ = await waitForPrompt.Should().ThrowAsync<TimeoutException>(
            "no sign-in prompt may be broadcast for a host-allowlist deny");
        LogTestEnd();
    }

    [Fact]
    public async Task Gateway_abort_releases_hold_and_later_webhook_still_works()
    {
        LogTestStart();
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        await ClearGitHubTokenAsync(factory);

        var coordinator = factory.Services.GetRequiredService<PendingAuthCoordinator>();

        using var abortCts = new CancellationTokenSource();
        var webhookTask = PostWebhookAsync(factory, client, ct: abortCts.Token);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (coordinator.Snapshot().Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        coordinator.Snapshot().Should().ContainSingle();
        Logger.LogInformation("Hold established; aborting the gateway call");
        await abortCts.CancelAsync();

        var act = async () => await webhookTask;
        _ = await act.Should().ThrowAsync<OperationCanceledException>();

        // The pending entry must be cleaned up shortly after the abort propagates.
        deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (coordinator.Snapshot().Count > 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        coordinator.Snapshot().Should().BeEmpty("an aborted hold must not leak its pending entry");

        // And the machinery still works end-to-end afterwards.
        await SeedGitHubTokenAsync(factory);
        using var response = await PostWebhookAsync(factory, client);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("decision").GetString().Should().Be("allow");

        await ClearGitHubTokenAsync(factory);
        LogTestEnd();
    }
}
