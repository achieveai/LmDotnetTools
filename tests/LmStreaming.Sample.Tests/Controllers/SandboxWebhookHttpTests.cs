using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Models;
using LmStreaming.Sample.Configuration;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Services.Discovery;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Over-HTTP proof of the unified sandbox webhook (gateway ADR 0043): a real <c>POST</c> of the
/// <c>sbx.webhook/1</c> envelope through routing, snake_case binding and the <c>Authorization</c> check into
/// the real <see cref="SandboxWebhookController"/>, discovery services and
/// <see cref="SandboxTaskCompletionNotifier"/>, asserting what the pooled agents received. Same in-process
/// host as <see cref="ContextDiscoveryWebhookHttpTests"/>, for the same reason.
/// </summary>
public sealed class SandboxWebhookHttpTests
{
    private const string Secret = "unified-shared-secret-3f9d";
    private const string SessionId = "sess-unified";
    private const string ThreadId = "thread-unified";
    private const string OtherSessionSecret = "other-session-secret-7a1c";
    private static readonly TimeSpan OwnerRecordWait = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task TaskCompleted_OneConversationOnTheSession_NotifiesItWithTheOutputTail()
    {
        await using var app = await TestApp.BuildAsync();

        var response = await app.PostAsync(TaskCompleted("gw-1-7", taskId: "7"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var message = app
            .Primary.SentMessages.Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<NotifyMessage>()
            .Subject;
        message.NotifyKind.Should().Be(SandboxTaskCompletionNotifier.NotifyKind);
        message.Label.Should().Be("Task 7 completed");
        message.Text.Should().Contain("Background task 7 has ended: it exited.");
        message.Text.Should().Contain("does not report the real exit code");
        message.Text.Should().Contain("It ran for 3.1 s.");
        message.Text.Should().Contain("stdout (last part):\nhello-bg\ndone-bg");
        message.Text.Should().NotContain("stderr");
    }

    [Fact]
    public async Task TaskCompleted_RedeliveredWithTheSameEventId_IsAcknowledgedButNotifiedOnce()
    {
        await using var app = await TestApp.BuildAsync();

        var first = await app.PostAsync(TaskCompleted("gw-1-7"));
        var second = await app.PostAsync(TaskCompleted("gw-1-7"));
        var next = await app.PostAsync(TaskCompleted("gw-1-8"));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK, "a redelivery is acknowledged, not refused");
        next.StatusCode.Should().Be(HttpStatusCode.OK);
        app.Primary.SentMessages.Should().HaveCount(2, "the duplicate is dropped; a new event id is not");
    }

    [Fact]
    public async Task WrongSecret_Returns401_AndDoesNotBurnTheEventId()
    {
        await using var app = await TestApp.BuildAsync();

        var forged = await app.PostAsync(TaskCompleted("gw-1-7"), authorization: "wrong-secret");
        var real = await app.PostAsync(TaskCompleted("gw-1-7"));

        forged.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        real.StatusCode.Should().Be(HttpStatusCode.OK);
        app.Primary.SentMessages.Should().ContainSingle("the forged call must not mark the real event as seen");
    }

    [Theory]
    [InlineData("")]
    [InlineData(OtherSessionSecret)]
    public async Task MissingOrAnotherSessionsSecret_Returns401_AndNothingIsDelivered(string authorization)
    {
        // The identity guard exempts this route, so the session secret is its only gate: a secret valid for a
        // different session must not open this one.
        await using var app = await TestApp.BuildAsync();
        await app.SecretStore.SaveAsync("sess-other", OtherSessionSecret);

        var response = await app.PostAsync(TaskCompleted("gw-1-7"), authorization);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        app.Primary.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownEvent_IsAcknowledgedUnread()
    {
        await using var app = await TestApp.BuildAsync();

        var response = await app.PostAsync(
            Envelope("sandbox_created", "gw-1-1", taskId: null, agentId: null, data: """{"image":"debian"}""")
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        app.Primary.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task TaskCompleted_MalformedData_Returns400_AndTheRetryIsStillHandled()
    {
        await using var app = await TestApp.BuildAsync();

        var malformed = await app.PostAsync(
            Envelope("task_completed", "gw-1-7", taskId: "7", agentId: "", data: """{"exit_code":0}""")
        );
        var retry = await app.PostAsync(TaskCompleted("gw-1-7"));

        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        app.Primary.SentMessages.Should().ContainSingle("a rejected event must not be remembered as seen");
    }

    [Fact]
    public async Task ContextDiscovery_ItemOwnedByTheConversation_IsRecordedAsContextNotSent()
    {
        await using var app = await TestApp.BuildAsync(routeToOpeningSubAgent: true);
        app.Primary.CurrentRunId = "active-run";

        var response = await app.PostAsync(
            Envelope(
                "context_discovery",
                "gw-1-2",
                taskId: null,
                agentId: "",
                data: $$"""{"discoveries":[{"kind":"context_file","path":"CLAUDE.md","content":"Be terse.","truncated":false,"agent_id":"{{ThreadId}}"}]}"""
            )
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        app.Primary.SentMessages.Should().BeEmpty("context is recorded on the active run, never queued as input");
        var message = app.Primary.RecordedContext.Should().ContainSingle().Subject;
        message.NotifyKind.Should().Be(NotifyKinds.ContextDiscovery);
        message.Text.Should().Contain("Be terse.");
    }

    [Fact]
    public async Task ContextDiscovery_DefaultAgentEnvelope_EmptyAgentId_IsDropped()
    {
        // The envelope's "" names no owner: dropped, never broadcast to the session's conversations.
        await using var app = await TestApp.BuildAsync(routeToOpeningSubAgent: true);
        app.Primary.CurrentRunId = "active-run";

        var response = await app.PostAsync(
            Envelope(
                "context_discovery",
                "gw-1-2",
                taskId: null,
                agentId: "",
                data: """{"discoveries":[{"kind":"context_file","path":"CLAUDE.md","content":"Be terse.","truncated":false}]}"""
            )
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        app.Primary.SentMessages.Should().BeEmpty();
        app.Primary.RecordedContext.Should().BeEmpty();
    }

    [Fact]
    public async Task ContextDiscovery_SubAgentEnvelopeId_IsRoutedToThatSubAgent()
    {
        // The unified route rebuilds the discovery batch from the envelope; its agent_id must survive, or a
        // sub-agent's nested CLAUDE.md lands in the primary conversation instead.
        var sink = new RecordingSinkAgent("thread-sub", SubAgentContextDeliveryResult.Delivered);
        await using var app = await TestApp.BuildAsync(
            new Dictionary<string, IMultiTurnAgent> { ["thread-sub"] = sink },
            routeToOpeningSubAgent: true
        );

        var response = await app.PostAsync(
            Envelope(
                "context_discovery",
                "gw-1-3",
                taskId: null,
                agentId: SandboxSubAgentIdentity.ForSubAgent("thread-sub", "worker"),
                data: """{"discoveries":[{"kind":"context_file","path":"sub/CLAUDE.md","content":"Sub rules.","truncated":false}]}"""
            )
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        sink.LastDeliveredAgentId.Should()
            .Be(SubAgentThreadIds.For("thread-sub", "worker"), "the gateway id maps back to the scoped thread");
        sink.DeliveredMessages.Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<NotifyMessage>()
            .Which.Text.Should()
            .Contain("Sub rules.");
        app.Primary.SentMessages.Should().BeEmpty();
        sink.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task TaskCompleted_DefaultAgent_TwoConversationsOnTheSession_NeitherIsNotified()
    {
        // The primary agent sends no agent id, so every conversation shares the default bucket: the task
        // cannot be attributed and the notice is dropped rather than sent to a conversation that never ran it.
        var other = new RecordingMultiTurnAgent("thread-other");
        await using var app = await TestApp.BuildAsync(
            new Dictionary<string, IMultiTurnAgent> { ["thread-other"] = other }
        );

        var response = await app.PostAsync(TaskCompleted("gw-1-7"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        app.Primary.SentMessages.Should().BeEmpty();
        other.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task TaskCompleted_DefaultAgent_TwoConversations_GoesToTheOneWhoseBashStartedIt()
    {
        // SandboxBackgroundTaskTracker records the owner when the Bash call returns its task id.
        var other = new RecordingMultiTurnAgent("thread-other");
        await using var app = await TestApp.BuildAsync(
            new Dictionary<string, IMultiTurnAgent> { ["thread-other"] = other }
        );
        app.Registry.RecordBackgroundTaskOwner(SessionId, "7", "thread-other");

        var response = await app.PostAsync(TaskCompleted("gw-1-7", taskId: "7"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        other.SentMessages.Should().ContainSingle().Which.Should().BeOfType<NotifyMessage>();
        app.Primary.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task TaskCompleted_DefaultAgent_ArrivesBeforeTheBashResult_StillGoesToTheOneWhoseBashStartedIt()
    {
        // The gateway sends task_completed the moment the task ends, so a fast task can beat its own
        // "Task started with ID" result to the tracker.
        var other = new RecordingMultiTurnAgent("thread-other");
        await using var app = await TestApp.BuildAsync(
            new Dictionary<string, IMultiTurnAgent> { ["thread-other"] = other }
        );
        (await app.PostAsync(Envelope("sandbox_created", "gw-1-1", null, "", "{}"))).EnsureSuccessStatusCode();

        var post = app.PostAsync(TaskCompleted("gw-1-7", taskId: "7"));
        await Task.Delay(OwnerRecordWait / 3);
        app.Registry.RecordBackgroundTaskOwner(SessionId, "7", "thread-other");
        var response = await post;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        other.SentMessages.Should().ContainSingle().Which.Should().BeOfType<NotifyMessage>();
        app.Primary.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task TaskCompleted_RootNamedByItsThreadId_TwoConversations_GoesToThatConversation()
    {
        // A mode with skill tiers names its root agent with its thread id (SandboxSubAgentIdentity.ForRoot),
        // so the gateway reports that id instead of "". It is the conversation's root, not a sub-agent.
        var other = new RecordingMultiTurnAgent("thread-other");
        await using var app = await TestApp.BuildAsync(
            new Dictionary<string, IMultiTurnAgent> { ["thread-other"] = other }
        );
        app.Registry.RecordBackgroundTaskOwner(SessionId, "7", "thread-other");

        var response = await app.PostAsync(TaskCompleted("gw-1-7", taskId: "7", agentId: "thread-other"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        other.SentMessages.Should().ContainSingle().Which.Should().BeOfType<NotifyMessage>();
        app.Primary.SentMessages.Should().BeEmpty();
        app.Registry.TryTakeBackgroundTaskOwner(SessionId, "7", out _)
            .Should()
            .BeFalse("the tracker's record of the same task is consumed, not left behind");
    }

    [Fact]
    public async Task TaskCompleted_SubAgentId_IsDeliveredToThatConversationsSubAgentOnly()
    {
        var sink = new RecordingSinkAgent("thread-sub", SubAgentContextDeliveryResult.Delivered);
        await using var app = await TestApp.BuildAsync(
            new Dictionary<string, IMultiTurnAgent> { ["thread-sub"] = sink }
        );

        var response = await app.PostAsync(
            TaskCompleted("gw-1-7", agentId: SandboxSubAgentIdentity.ForSubAgent("thread-sub", "worker"))
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        sink.DeliverCallCount.Should().Be(1);
        sink.LastDeliveredAgentId.Should()
            .Be(SubAgentThreadIds.For("thread-sub", "worker"), "SubAgentManager owns only the scoped thread id");
        sink.DeliveredMessages.Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<NotifyMessage>()
            .Which.NotifyKind.Should()
            .Be(SandboxTaskCompletionNotifier.NotifyKind);
        sink.SentMessages.Should().BeEmpty();
        app.Primary.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task TaskCompleted_SubAgentHasFinished_IsAcknowledged_AndNobodyIsNotified()
    {
        // ISubAgentContextSink: a sub-agent that can no longer take context must not fall back to the primary.
        var sink = new RecordingSinkAgent("thread-sub", SubAgentContextDeliveryResult.TargetNotDeliverable);
        await using var app = await TestApp.BuildAsync(
            new Dictionary<string, IMultiTurnAgent> { ["thread-sub"] = sink }
        );

        var response = await app.PostAsync(
            TaskCompleted("gw-1-7", agentId: SandboxSubAgentIdentity.ForSubAgent("thread-sub", "worker"))
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        sink.DeliverCallCount.Should().Be(1);
        sink.SentMessages.Should().BeEmpty();
        app.Primary.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task TaskCompleted_OwnerConversationNoLongerLoaded_IsDropped_NotSentToTheOtherConversation()
    {
        // The recorded owner wins over the one-conversation fallback, so a closed owner drops the notice.
        await using var app = await TestApp.BuildAsync();
        app.Registry.RecordBackgroundTaskOwner(SessionId, "7", "thread-closed");

        var response = await app.PostAsync(TaskCompleted("gw-1-7", taskId: "7"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        app.Primary.SentMessages.Should().BeEmpty();
    }

    private static string TaskCompleted(string eventId, string taskId = "7", string agentId = "") =>
        Envelope(
            "task_completed",
            eventId,
            taskId,
            agentId,
            """
            {"status":"completed","exit_code":0,"started_at":"2026-10-19T10:00:00Z","ended_at":"2026-10-19T10:00:03Z",
             "duration_ms":3100,"stdout_tail":"hello-bg\ndone-bg\n","stderr_tail":"","output_truncated":false}
            """
        );

    private static string Envelope(string @event, string eventId, string? taskId, string? agentId, string data) =>
        JsonSerializer.Serialize(
            new
            {
                schema = "sbx.webhook/1",
                @event,
                event_id = eventId,
                gateway_id = "gw-1",
                seq = 7,
                occurred_at = "2026-10-19T10:00:03Z",
                app_id = "lmstreaming",
                session_id = SessionId,
                workspace = "default",
                agent_id = agentId,
                task_id = taskId,
                data = JsonDocument.Parse(data).RootElement,
            }
        );

    private sealed class TestApp : IAsyncDisposable
    {
        private readonly IHost _host;

        private TestApp(
            IHost host,
            HttpClient client,
            RecordingMultiTurnAgent primary,
            SandboxSessionRegistry registry,
            SessionSecretStore secretStore
        )
        {
            _host = host;
            Client = client;
            Primary = primary;
            Registry = registry;
            SecretStore = secretStore;
        }

        public HttpClient Client { get; }
        public SandboxSessionRegistry Registry { get; }
        public SessionSecretStore SecretStore { get; }
        public RecordingMultiTurnAgent Primary { get; }

        public async Task<HttpResponseMessage> PostAsync(string json, string authorization = Secret)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sandbox/webhook")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            return await Client.SendAsync(request);
        }

        public static async Task<TestApp> BuildAsync(
            IReadOnlyDictionary<string, IMultiTurnAgent>? otherThreads = null,
            bool routeToOpeningSubAgent = false
        )
        {
            var sessionSecretStore = new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            );
            await sessionSecretStore.SaveAsync(SessionId, Secret);

            var authOptions = new AuthOptions();
            authOptions.Webhook.UseSandboxWebhook = true;
            var registry = CreateRegistry(sessionSecretStore, authOptions);

            var primary = new RecordingMultiTurnAgent(ThreadId);
            var pool = new MultiTurnAgentPool(
                (threadId, _, _) =>
                    new MultiTurnAgentPool.AgentCreationResult(
                        otherThreads is not null && otherThreads.TryGetValue(threadId, out var agent) ? agent : primary
                    ),
                NullLogger<MultiTurnAgentPool>.Instance
            );
            pool.ThreadRemoved += threadId => registry.UnregisterThreadFromAllSessions(threadId);

            var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
            foreach (var threadId in new[] { ThreadId }.Concat(otherThreads?.Keys ?? []))
            {
                _ = pool.GetOrCreateAgent(threadId, mode);
                registry.RegisterThread(SessionId, threadId);
            }

            var host = await new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddControllers().AddApplicationPart(typeof(SandboxWebhookController).Assembly);

                        services.AddSingleton(registry);
                        services.AddSingleton(pool);
                        services.AddSingleton(sessionSecretStore);
                        services.AddSingleton<ContextDiscoveryFormatter>();
                        services.AddSingleton(
                            new ContextDiscoveryOptions { RouteToOpeningSubAgent = routeToOpeningSubAgent }
                        );
                        services.AddSingleton<ContextDiscoveryInjector>();
                        services.AddSingleton(new AgentOutputTokenPolicy(new AgentOutputTokenOptions()));
                        services.AddSingleton<ContextDiscoveryDiagnostics>();
                        services.AddSingleton<WorkspaceSubAgentLoader>();
                        services.AddSingleton<ContextDiscoveryBatchHandler>();
                        services.AddSingleton(
                            new SandboxTaskCompletionNotifier(
                                registry,
                                pool,
                                NullLogger<SandboxTaskCompletionNotifier>.Instance
                            )
                            {
                                OwnerRecordWait = OwnerRecordWait,
                            }
                        );
                    });
                    webBuilder.Configure(appBuilder =>
                    {
                        appBuilder.UseRouting();
                        appBuilder.UseEndpoints(endpoints => endpoints.MapControllers());
                    });
                })
                .StartAsync();

            return new TestApp(host, host.GetTestClient(), primary, registry, sessionSecretStore);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }

        private static SandboxSessionRegistry CreateRegistry(
            SessionSecretStore sessionSecretStore,
            AuthOptions authOptions
        )
        {
            static HttpResponseMessage Unused(HttpRequestMessage _) => new(HttpStatusCode.OK);

            var gateway = new SandboxGatewayLifetime(
                new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
                NullLogger<SandboxGatewayLifetime>.Instance,
                new HttpClient(new StubHandler(Unused))
            );

            return new SandboxSessionRegistry(
                gateway,
                new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
                NullLogger<SandboxSessionRegistry>.Instance,
                new HttpClient(new StubHandler(Unused)),
                authOptions,
                sessionSecretStore
            );
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(respond(request));
        }
    }
}
