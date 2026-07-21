using System.Collections.Immutable;
using System.Net;
using LmStreaming.Sample.Auth;

namespace LmStreaming.Sample.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="SandboxAuthWebhookForwarder"/>'s session-aware target resolution:
/// per-session isolation, tie-break ordering when more than one thread is eligible, provider-id
/// filtering, raw-CLR vs <see cref="JsonElement"/> property coercion (in-memory vs JSON-round-tripped
/// stores), and — critically — that a terminal call given an already-captured target never
/// re-resolves eligibility against whatever the registry/store look like by the time it fires.
/// </summary>
public sealed class SandboxAuthWebhookForwarderTests
{
    private const string SessionA = "session-a";
    private const string SessionB = "session-b";

    private sealed record CapturedPost(string Url, JsonDocument Body);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<CapturedPost> Posts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Posts.Add(new CapturedPost(request.RequestUri!.ToString(), JsonDocument.Parse(body)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private static SandboxSessionRegistry CreateRegistry()
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
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance));
    }

    private static SandboxAuthWebhookForwarder CreateForwarder(
        SandboxSessionRegistry registry,
        IConversationStore store,
        HttpMessageHandler handler) =>
        new(registry, store, new HttpClient(handler), NullLogger<SandboxAuthWebhookForwarder>.Instance);

    private static async Task RegisterEligibleThreadAsync(
        IConversationStore store,
        SandboxSessionRegistry registry,
        string sessionId,
        string threadId,
        string webhookUrl,
        string providerId,
        long registeredAt,
        string? currentRunId = null)
    {
        registry.RegisterThread(sessionId, threadId);
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                CurrentRunId = currentRunId,
                LastUpdated = registeredAt,
                Properties = ImmutableDictionary<string, object>.Empty
                    .Add("sample.authWebhookUrl", webhookUrl)
                    .Add("sample.authWebhookProviderId", providerId)
                    .Add("sample.authWebhookRegisteredAt", registeredAt),
            });
    }

    [Fact]
    public async Task Forwarded_payload_uses_the_documented_lower_camel_case_wire_names()
    {
        await using var registry = CreateRegistry();
        var store = new InMemoryConversationStore();
        var handler = new CapturingHandler();
        var forwarder = CreateForwarder(registry, store, handler);

        await RegisterEligibleThreadAsync(store, registry, SessionA, "thread-a", "https://a.test/hook", "github", 100, "run-a");

        await forwarder.NotifyAuthRequiredAsync(SessionA, "github", "http://host/auth/github", "expired", CancellationToken.None);

        var root = handler.Posts.Should().ContainSingle().Which.Body.RootElement;
        root.GetProperty("type").GetString().Should().Be("auth_required");
        root.GetProperty("sessionId").GetString().Should().Be(SessionA);
        root.GetProperty("threadId").GetString().Should().Be("thread-a");
        root.GetProperty("runId").GetString().Should().Be("run-a");
        root.GetProperty("providerId").GetString().Should().Be("github");
        root.GetProperty("signinUrl").GetString().Should().Be("http://host/auth/github");
        root.GetProperty("reason").GetString().Should().Be("expired");

        foreach (var pascalName in new[] { "Type", "SessionId", "ThreadId", "RunId", "ProviderId", "SigninUrl", "Reason" })
        {
            root.TryGetProperty(pascalName, out _).Should().BeFalse(
                $"the wire contract is documented in camelCase; '{pascalName}' must not also appear");
        }
    }

    [Fact]
    public async Task Two_concurrent_sessions_resolve_and_forward_independently()
    {
        await using var registry = CreateRegistry();
        var store = new InMemoryConversationStore();
        var handler = new CapturingHandler();
        var forwarder = CreateForwarder(registry, store, handler);

        await RegisterEligibleThreadAsync(store, registry, SessionA, "thread-a", "https://a.test/hook", "github", 100, "run-a");
        await RegisterEligibleThreadAsync(store, registry, SessionB, "thread-b", "https://b.test/hook", "github", 100, "run-b");

        var targetA = await forwarder.NotifyAuthRequiredAsync(SessionA, "github", "https://signin/github", "expired", CancellationToken.None);
        var targetB = await forwarder.NotifyAuthRequiredAsync(SessionB, "github", "https://signin/github", "expired", CancellationToken.None);

        targetA.Should().Be(new AuthWebhookTarget("thread-a", "run-a", "https://a.test/hook"));
        targetB.Should().Be(new AuthWebhookTarget("thread-b", "run-b", "https://b.test/hook"));

        handler.Posts.Should().HaveCount(2, "each session's auth_required must forward exactly once, to its own thread's webhook");
        handler.Posts.Should().ContainSingle(p => p.Url == "https://a.test/hook")
            .Which.Body.RootElement.GetProperty("threadId").GetString().Should().Be("thread-a");
        handler.Posts.Should().ContainSingle(p => p.Url == "https://b.test/hook")
            .Which.Body.RootElement.GetProperty("threadId").GetString().Should().Be("thread-b");
    }

    [Fact]
    public async Task Target_drift_after_required_does_not_redirect_the_terminal_call()
    {
        await using var registry = CreateRegistry();
        var store = new InMemoryConversationStore();
        var handler = new CapturingHandler();
        var forwarder = CreateForwarder(registry, store, handler);

        await RegisterEligibleThreadAsync(store, registry, SessionA, "thread-old", "https://old.test/hook", "github", 100, "run-old");

        var target = await forwarder.NotifyAuthRequiredAsync(SessionA, "github", "https://signin/github", "expired", CancellationToken.None);
        target.Should().Be(new AuthWebhookTarget("thread-old", "run-old", "https://old.test/hook"));

        // Drift: the originally-eligible thread is deleted, and a newer eligible thread registers,
        // before the terminal call arrives.
        registry.UnregisterThread(SessionA, "thread-old");
        await RegisterEligibleThreadAsync(store, registry, SessionA, "thread-new", "https://new.test/hook", "github", 200, "run-new");

        handler.Posts.Clear();
        await forwarder.NotifyAuthCompletedAsync(target, SessionA, "github", CancellationToken.None);

        handler.Posts.Should().ContainSingle().Which.Url.Should().Be(
            "https://old.test/hook",
            "the terminal call must reuse the captured target, not re-resolve eligibility against current registry/store state");
    }

    [Fact]
    public async Task Newly_registered_thread_mid_hold_is_not_picked_up_by_the_terminal_call()
    {
        await using var registry = CreateRegistry();
        var store = new InMemoryConversationStore();
        var handler = new CapturingHandler();
        var forwarder = CreateForwarder(registry, store, handler);

        await RegisterEligibleThreadAsync(store, registry, SessionA, "thread-original", "https://original.test/hook", "github", 100, "run-1");

        var target = await forwarder.NotifyAuthRequiredAsync(SessionA, "github", "https://signin/github", "expired", CancellationToken.None);

        // A second thread registers for the same session+provider while the original request is
        // still pending (e.g. the user opened a second tab).
        await RegisterEligibleThreadAsync(store, registry, SessionA, "thread-second", "https://second.test/hook", "github", 50, "run-2");

        handler.Posts.Clear();
        await forwarder.NotifyAuthDeniedAsync(target, SessionA, "github", "denied_by_user", CancellationToken.None);

        handler.Posts.Should().ContainSingle().Which.Url.Should().Be("https://original.test/hook");
    }

    [Fact]
    public async Task Multiple_eligible_threads_tie_break_on_ascending_registered_at()
    {
        await using var registry = CreateRegistry();
        var store = new InMemoryConversationStore();
        var handler = new CapturingHandler();
        var forwarder = CreateForwarder(registry, store, handler);

        await RegisterEligibleThreadAsync(store, registry, SessionA, "thread-z", "https://z.test/hook", "github", 100, "run-z");
        await RegisterEligibleThreadAsync(store, registry, SessionA, "thread-a", "https://a.test/hook", "github", 200, "run-a");

        var target = await forwarder.NotifyAuthRequiredAsync(SessionA, "github", "https://signin/github", "expired", CancellationToken.None);

        target.Should().Be(new AuthWebhookTarget("thread-z", "run-z", "https://z.test/hook"));
    }

    [Fact]
    public async Task Equal_registered_at_tie_breaks_on_ascending_thread_id()
    {
        await using var registry = CreateRegistry();
        var store = new InMemoryConversationStore();
        var handler = new CapturingHandler();
        var forwarder = CreateForwarder(registry, store, handler);

        await RegisterEligibleThreadAsync(store, registry, SessionA, "thread-b", "https://b.test/hook", "github", 100, "run-b");
        await RegisterEligibleThreadAsync(store, registry, SessionA, "thread-a", "https://a.test/hook", "github", 100, "run-a");

        var target = await forwarder.NotifyAuthRequiredAsync(SessionA, "github", "https://signin/github", "expired", CancellationToken.None);

        target.Should().Be(new AuthWebhookTarget("thread-a", "run-a", "https://a.test/hook"));
    }

    [Fact]
    public async Task Thread_registered_for_a_different_provider_is_not_eligible()
    {
        await using var registry = CreateRegistry();
        var store = new InMemoryConversationStore();
        var handler = new CapturingHandler();
        var forwarder = CreateForwarder(registry, store, handler);

        await RegisterEligibleThreadAsync(store, registry, SessionA, "thread-ado", "https://ado.test/hook", "ado", 100, "run-ado");

        var target = await forwarder.NotifyAuthRequiredAsync(SessionA, "github", "https://signin/github", "expired", CancellationToken.None);

        target.Should().BeNull();
        handler.Posts.Should().BeEmpty();
    }

    [Fact]
    public async Task No_registered_threads_for_session_resolves_null_and_forwards_nothing()
    {
        await using var registry = CreateRegistry();
        var store = new InMemoryConversationStore();
        var handler = new CapturingHandler();
        var forwarder = CreateForwarder(registry, store, handler);

        var target = await forwarder.NotifyAuthRequiredAsync("unknown-session", "github", "https://signin/github", "expired", CancellationToken.None);

        target.Should().BeNull();
        handler.Posts.Should().BeEmpty();
    }

    [Fact]
    public async Task Terminal_calls_given_a_null_target_are_no_ops()
    {
        await using var registry = CreateRegistry();
        var store = new InMemoryConversationStore();
        var handler = new CapturingHandler();
        var forwarder = CreateForwarder(registry, store, handler);

        await forwarder.NotifyAuthCompletedAsync(null, SessionA, "github", CancellationToken.None);
        await forwarder.NotifyAuthDeniedAsync(null, SessionA, "github", "expired", CancellationToken.None);

        handler.Posts.Should().BeEmpty();
    }

    [Fact]
    public async Task JsonElement_valued_properties_from_a_round_tripped_store_are_still_recognized()
    {
        await using var registry = CreateRegistry();
        var store = new InMemoryConversationStore();
        var handler = new CapturingHandler();
        var forwarder = CreateForwarder(registry, store, handler);

        registry.RegisterThread(SessionA, "thread-a");
        await store.SaveMetadataAsync(
            "thread-a",
            new ThreadMetadata
            {
                ThreadId = "thread-a",
                CurrentRunId = "run-a",
                LastUpdated = 100,
                Properties = ImmutableDictionary<string, object>.Empty
                    .Add("sample.authWebhookUrl", JsonSerializer.SerializeToElement("https://a.test/hook"))
                    .Add("sample.authWebhookProviderId", JsonSerializer.SerializeToElement("github"))
                    .Add("sample.authWebhookRegisteredAt", JsonSerializer.SerializeToElement(100L)),
            });

        var target = await forwarder.NotifyAuthRequiredAsync(SessionA, "github", "https://signin/github", "expired", CancellationToken.None);

        target.Should().Be(new AuthWebhookTarget("thread-a", "run-a", "https://a.test/hook"));
    }
}
