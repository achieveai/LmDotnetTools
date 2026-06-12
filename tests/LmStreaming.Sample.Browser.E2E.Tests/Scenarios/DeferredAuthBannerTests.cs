using System.Net;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Services.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Full-UX browser test of DEFERRED AUTH, with the test playing the sandbox gateway (no real
/// gateway, no real OAuth):
/// <list type="number">
/// <item>A chat conversation establishes the persistent WebSocket.</item>
/// <item>The test POSTs the auth webhook with no token — the call is HELD by the backend.</item>
/// <item>The chat UI shows the <c>auth-required-banner</c>; its sign-in button opens a popup
/// to the same-origin <c>/auth/github</c> landing page (rendered "unavailable" here since no
/// ClientId is configured — the popup wiring is what's under test). Closing the popup leaves
/// the banner up (user-closes-popup path).</item>
/// <item>The test seeds the token store (stands in for the user completing sign-in) — the held
/// webhook resolves <c>allow</c> with the Bearer header and the banner dismisses via the
/// <c>auth_completed</c> frame (provider status never changed, so status polling could not
/// have dismissed it).</item>
/// </list>
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class DeferredAuthBannerTests
{
    private const string SharedSecret = "e2e-deferred-auth-shared-secret";

    private const string WebhookBody = """
        {
          "session_id": "s-browser-e2e",
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

    private readonly PlaywrightFixture _fixture;

    public DeferredAuthBannerTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Banner_appears_on_held_webhook_and_clears_after_token_seeded()
    {
        var tokenStoreDir = Path.Combine(
            Path.GetTempPath(),
            "lm-streaming-deferred-auth-e2e",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tokenStoreDir);

        // Config-by-env: the Kestrel-hosted factory does not expose DI Services, so the test
        // pins the shared secret and points the token store at a private temp dir it can seed
        // directly. The scope restores prior values on dispose; tests run serialized.
        using var env = new EnvironmentVariableScope(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Auth__Webhook__GatewaySharedSecret"] = SharedSecret,
            ["Auth__TokenStoreDir"] = tokenStoreDir,
            ["Auth__Webhook__HoldTimeoutSeconds"] = "60",
            ["Auth__Webhook__PollIntervalSeconds"] = "0.25",
        });

        try
        {
            var responder = ScriptedSseResponder
                .New()
                .ForRole("parent", _ => true)
                .Turn(t => t.Text("Hello from the scripted assistant."))
                .Build();

            await using var session = await _fixture.OpenAsync("test", responder.HandlerFor("test"));
            var page = session.Page;

            // One chat turn establishes the persistent WebSocket the auth frames ride on.
            await page.SendMessageAsync("hello");
            await page.WaitForStreamIdleAsync();

            // The test plays the sandbox gateway: webhook call with no token → HELD, not denied.
            using var gatewayClient = new HttpClient();
            using var webhookRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"{session.Factory.ServerAddress.TrimEnd('/')}/api/auth/webhook/github")
            {
                Content = new StringContent(WebhookBody, Encoding.UTF8, "application/json"),
            };
            webhookRequest.Headers.TryAddWithoutValidation("Authorization", SharedSecret);
            var webhookTask = gatewayClient.SendAsync(webhookRequest);

            // The auth_required frame must surface as a banner in the chat UI.
            await page.AuthRequiredBanner().WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 15_000,
            });
            (await page.AuthRequiredBanner().GetAttributeAsync("data-provider-id")).Should().Be("github");
            (await page.AuthRequiredBanner().InnerTextAsync()).Should().Contain("github");
            webhookTask.IsCompleted.Should().BeFalse("the webhook must stay held while the user is prompted");

            // Sign-in button opens the same-origin landing page in a popup.
            var popup = await session.Context.RunAndWaitForPageAsync(
                () => page.AuthSigninButton().ClickAsync());
            await popup.WaitForLoadStateAsync();
            popup.Url.Should().Contain("/auth/github");

            // User closes the popup without signing in — the prompt must persist.
            await popup.CloseAsync();
            await page.AuthRequiredBanner().WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 5_000,
            });

            // "User signs in": seed the token store the backend's hold is polling.
            var store = new FileOAuthTokenStore(tokenStoreDir, NullLogger<FileOAuthTokenStore>.Instance);
            await store.SaveAsync(new OAuthTokenRecord(
                Provider: "github",
                Account: "octocat",
                RefreshToken: string.Empty,
                AccessToken: "browser-e2e-token",
                AccessTokenExpiresAtUtc: DateTimeOffset.UtcNow.AddYears(1),
                Scopes: ["repo", "read:org"]));

            // The held webhook resolves allow with the injected Bearer header.
            using var webhookResponse = await webhookTask.WaitAsync(TimeSpan.FromSeconds(30));
            webhookResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = await webhookResponse.Content.ReadAsStringAsync();
            using (var doc = JsonDocument.Parse(json))
            {
                doc.RootElement.GetProperty("decision").GetString().Should().Be("allow");
                var pair = doc.RootElement.GetProperty("headers")[0];
                pair[0].GetString().Should().Be("Authorization");
                pair[1].GetString().Should().Be("Bearer browser-e2e-token");
            }

            // Banner dismisses via the auth_completed frame (provider Status never flipped to
            // SignedIn here, so the fallback status polling could not have dismissed it).
            await page.AuthRequiredBanner().WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Hidden,
                Timeout = 15_000,
            });

            await session.SaveSuccessScreenshotAsync("DeferredAuth.Banner_clears_after_token_seeded");
        }
        finally
        {
            try
            {
                Directory.Delete(tokenStoreDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
