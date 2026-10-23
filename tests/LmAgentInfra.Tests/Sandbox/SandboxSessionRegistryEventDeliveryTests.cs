using System.Net;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

/// <summary>
/// Where a new sandbox is told to deliver its events: the unified <c>webhook</c> block (gateway ADR 0043) when
/// <see cref="WebhookOptions.UseSandboxWebhook"/> is on, the legacy <c>discovery</c> block otherwise, never both;
/// and the per-session event ledger the unified receiver deduplicates on.
/// </summary>
public class SandboxSessionRegistryEventDeliveryTests
{
    [Fact]
    public async Task GetOrCreateSessionAsync_SandboxWebhookOn_RegistersTheUnifiedWebhookAndNoDiscoveryBlock()
    {
        string? createBody = null;
        await using var registry = CreateRegistry(useSandboxWebhook: true, body => createBody = body);

        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));

        using var sent = JsonDocument.Parse(createBody!);
        sent.RootElement.TryGetProperty("discovery", out var discovery)
            .Should()
            .BeFalse("both blocks would make the gateway send every discovery twice: {0}", discovery);
        var webhook = sent.RootElement.GetProperty("webhook");
        webhook.GetProperty("url").GetString().Should().Be("http://127.0.0.1:5077/api/sandbox/webhook");
        webhook.GetProperty("auth_header").GetString().Should().NotBeNullOrWhiteSpace();
        webhook
            .GetProperty("events")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .Equal(SandboxWebhookEvents.ContextDiscovery, SandboxWebhookEvents.TaskCompleted);
        registry.DiscoveryWebhookUrl.Should().Be(registry.SandboxWebhookUrl);
    }

    [Fact]
    public async Task GetOrCreateSessionAsync_SandboxWebhookOff_RegistersOnlyTheLegacyDiscoveryWebhook()
    {
        string? createBody = null;
        await using var registry = CreateRegistry(useSandboxWebhook: false, body => createBody = body);

        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));

        using var sent = JsonDocument.Parse(createBody!);
        sent.RootElement.TryGetProperty("webhook", out _).Should().BeFalse();
        sent.RootElement.GetProperty("discovery")
            .GetProperty("webhook")
            .GetProperty("url")
            .GetString()
            .Should()
            .Be("http://127.0.0.1:5077/api/discovery/context_discovery");
        registry.DiscoveryWebhookUrl.Should().Be("http://127.0.0.1:5077/api/discovery/context_discovery");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetOrCreateSessionAsync_EventWebhookOff_RegistersNeitherBlockAndDoesNotWarn(
        bool useSandboxWebhook
    )
    {
        // A host the gateway cannot reach (an in-memory test server) turns registration off; otherwise the gateway
        // delivers every event to whatever else listens on the PublicBaseUrl port.
        string? createBody = null;
        var logger = new ListLogger();
        await using var registry = CreateRegistry(
            useSandboxWebhook,
            body => createBody = body,
            """{"session_id":"sess-1"}""",
            logger,
            registerEventWebhook: false
        );

        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));

        using var sent = JsonDocument.Parse(createBody!);
        sent.RootElement.TryGetProperty("webhook", out _).Should().BeFalse();
        sent.RootElement.TryGetProperty("discovery", out _).Should().BeFalse();
        registry.DiscoveryEnabled.Should().BeFalse();
        logger.Warnings.Should().BeEmpty("nothing was registered, so there is nothing to read back");
    }

    [Fact]
    public async Task TryMarkWebhookEventSeen_IsPerSessionAndUnmarkLetsTheRetryThrough()
    {
        await using var registry = CreateRegistry(useSandboxWebhook: true, _ => { });

        registry.TryMarkWebhookEventSeen("sess-a", "gw-1").Should().BeTrue();
        registry.TryMarkWebhookEventSeen("sess-a", "gw-1").Should().BeFalse("a redelivery is a duplicate");
        registry.TryMarkWebhookEventSeen("sess-b", "gw-1").Should().BeTrue("event ids are unique per session only");

        registry.UnmarkWebhookEventSeen("sess-a", "gw-1");

        registry.TryMarkWebhookEventSeen("sess-a", "gw-1").Should().BeTrue();
        registry.TryMarkWebhookEventSeen("sess-a", " ").Should().BeFalse("a blank id is never recorded");
    }

    [Fact]
    public async Task DestroyingTheSession_ForgetsItsSeenEvents()
    {
        await using var registry = CreateRegistry(useSandboxWebhook: true, _ => { });
        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        _ = registry.TryMarkWebhookEventSeen(session.SessionId, "gw-1");

        await registry.DestroyWorkspaceSessionAsync("ws-1");

        registry.TryMarkWebhookEventSeen(session.SessionId, "gw-1").Should().BeTrue();
    }

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 0)]
    public async Task GetOrCreateSessionAsync_WarnsOnlyWhenTheSandboxWebhookIsOnAndTheGatewayDidNotRegisterIt(
        bool useSandboxWebhook,
        bool registered,
        int expectedWarnings
    )
    {
        // A v0.1.16 gateway ignores the webhook block: the create succeeds and no event ever arrives. With the
        // switch off (the infra default) there is nothing to check.
        var logger = new ListLogger();
        var getBody = registered
            ? """{"session_id":"sess-1","webhook":{"url":"http://127.0.0.1:5077/api/sandbox/webhook","events":null,"auth_header_set":true}}"""
            : """{"session_id":"sess-1"}""";
        await using var registry = CreateRegistry(useSandboxWebhook, _ => { }, getBody, logger);

        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));

        logger
            .Warnings.Where(w => w.Contains("UseSandboxWebhook=false", StringComparison.Ordinal))
            .Should()
            .HaveCount(expectedWarnings);
    }

    private static SandboxSessionRegistry CreateRegistry(
        bool useSandboxWebhook,
        Action<string?> onCreate,
        string? getBody = null,
        ILogger<SandboxSessionRegistry>? logger = null,
        bool registerEventWebhook = true
    )
    {
        var options = new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" };
        var lifetime = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new Gateway((_, _) => new HttpResponseMessage(HttpStatusCode.OK)))
        );
        var authOptions = new AuthOptions();
        authOptions.Webhook.PublicBaseUrl = "http://127.0.0.1:5077/";
        authOptions.Webhook.UseSandboxWebhook = useSandboxWebhook;
        authOptions.Webhook.RegisterEventWebhook = registerEventWebhook;
        return new SandboxSessionRegistry(
            lifetime,
            options,
            logger ?? NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(
                new Gateway(
                    (request, body) =>
                    {
                        if (request.Method != HttpMethod.Post)
                        {
                            return getBody is null
                                ? new HttpResponseMessage(HttpStatusCode.OK)
                                : new HttpResponseMessage(HttpStatusCode.OK)
                                {
                                    Content = new StringContent(getBody, Encoding.UTF8, "application/json"),
                                };
                        }

                        onCreate(body);
                        return new HttpResponseMessage(HttpStatusCode.Created)
                        {
                            Content = new StringContent(
                                """{"session_id":"sess-1","container_id":"c-1"}""",
                                Encoding.UTF8,
                                "application/json"
                            ),
                        };
                    }
                )
            ),
            authOptions,
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmagentinfra-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            )
        );
    }

    private sealed class ListLogger : ILogger<SandboxSessionRegistry>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private sealed class Gateway(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request, body);
        }
    }
}
