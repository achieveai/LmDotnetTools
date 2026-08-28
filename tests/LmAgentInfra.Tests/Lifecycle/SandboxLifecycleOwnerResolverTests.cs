using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmLifecycle;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Lifecycle;

/// <summary>
/// ADR 0005 — who owns a conversation. Every claim here is one the type could get backwards while
/// still passing any test that only exercises S2S traffic, which is why they are pinned separately
/// from the delivery pipeline that consumes them.
/// </summary>
/// <remarks>
/// The registry is built for real rather than faked. The binding map is the host's own record and
/// the resolver's entire job is to read the right field out of it; a fake would let the test agree
/// with a resolver that read the wrong one.
/// </remarks>
public sealed class SandboxLifecycleOwnerResolverTests
{
    private static readonly SandboxCredential Caller = new("app-a", "caller-key");

    [Fact]
    public async Task Ownership_comes_from_the_caller_credential()
    {
        await using var registry = NewRegistry();
        Bind(registry, "thread-1", Caller);

        var owner = await new SandboxLifecycleOwnerResolver(registry).ResolveThreadOwnerAsync("thread-1");

        owner.Should().Be(LifecycleOwnerKey.ForCredential(Caller));
    }

    [Fact]
    public async Task An_interactive_conversation_has_no_owner()
    {
        await using var registry = NewRegistry();

        // What an interactive conversation looks like: an effective credential (always present, and
        // here the process default) with no caller behind it. Reading the effective credential would
        // resolve this to the default app id — and then any S2S subscriber authenticated as that app,
        // an ordinary configuration, would start receiving interactive users' events.
        Bind(registry, "thread-1", caller: null);

        var owner = await new SandboxLifecycleOwnerResolver(registry).ResolveThreadOwnerAsync("thread-1");

        owner.Should().BeNull();
    }

    [Fact]
    public async Task A_thread_with_no_binding_has_no_owner()
    {
        await using var registry = NewRegistry();

        var resolver = new SandboxLifecycleOwnerResolver(registry);

        (await resolver.ResolveThreadOwnerAsync("thread-unknown")).Should().BeNull();
        (await resolver.ResolveThreadOwnerAsync(null)).Should().BeNull();
    }

    [Fact]
    public async Task An_event_from_a_sub_agent_thread_falls_back_to_the_spawning_thread()
    {
        await using var registry = NewRegistry();
        Bind(registry, "parent", Caller);

        // The sub-agent's own thread has no binding, which is the normal case for several sub-agent
        // modes. Without the fallback every sub-agent event would be dropped, and a silent drop reads
        // as a delivery bug rather than as a policy decision.
        var owner = await new SandboxLifecycleOwnerResolver(registry).ResolveEventOwnerAsync(
            Event(threadId: "child", parentThreadId: "parent")
        );

        owner.Should().Be(LifecycleOwnerKey.ForCredential(Caller));
    }

    [Fact]
    public async Task An_approval_on_that_same_thread_does_not_fall_back()
    {
        await using var registry = NewRegistry();
        Bind(registry, "parent", Caller);

        var resolver = new SandboxLifecycleOwnerResolver(registry);

        // Deliberately asymmetric with the test above, on identical state. Observation can afford to
        // widen to the spawning thread; authorization cannot — inheriting upward would let a parent
        // conversation's subscriber approve a tool call it was never shown.
        (await resolver.ResolveEventOwnerAsync(Event("child", "parent")))
            .Should()
            .NotBeNull();
        (await resolver.ResolveThreadOwnerAsync("child")).Should().BeNull();
    }

    [Fact]
    public async Task An_event_that_correlates_to_nothing_has_no_owner()
    {
        await using var registry = NewRegistry();
        Bind(registry, "thread-1", Caller);

        var resolver = new SandboxLifecycleOwnerResolver(registry);

        var uncorrelated = new LifecycleEventEnvelope { Correlation = null };
        (await resolver.ResolveEventOwnerAsync(uncorrelated)).Should().BeNull();
        (await resolver.ResolveEventOwnerAsync(Event(threadId: null, parentThreadId: null))).Should().BeNull();
    }

    [Fact]
    public async Task An_authenticated_caller_resolves_to_its_own_app_id()
    {
        await using var registry = NewRegistry();

        var resolver = new SandboxLifecycleOwnerResolver(registry);

        // No allow-list is consulted, and an unknown app is not special-cased: it gets a well-formed
        // key that matches no conversation, which is operationally the same as getting nothing.
        (await resolver.ResolveCallerAsync("app-never-seen"))
            .Should()
            .Be(LifecycleOwnerKey.ForAppId("app-never-seen"));

        (await resolver.ResolveCallerAsync(" ")).Should().BeNull();
    }

    private static void Bind(SandboxSessionRegistry registry, string threadId, SandboxCredential? caller) =>
        registry.PublishEstablishedBinding(
            threadId,
            new SandboxEstablishedBinding(new WorkspaceRef("default"), registry.DefaultCredential, caller)
        );

    private static LifecycleEventEnvelope Event(string? threadId, string? parentThreadId) =>
        new()
        {
            Correlation = new LifecycleCorrelation { ThreadId = threadId, ParentThreadId = parentThreadId },
        };

    /// <summary>
    /// A registry whose transport throws, because nothing here should reach the gateway: ownership is
    /// answered entirely from the in-process binding map.
    /// </summary>
    private static SandboxSessionRegistry NewRegistry()
    {
        var options = new SandboxGatewayOptions
        {
            BaseUrl = "http://localhost:65535",
            // Named explicitly, and distinct from every caller app id here, so that the two
            // credentials on a binding can never be confused for one another by coincidence: a
            // resolver reading the effective credential answers "host-default-app" where these tests
            // expect either "app-a" or nothing.
            AppId = "host-default-app",
        };
        return new SandboxSessionRegistry(
            new SandboxGatewayLifetime(
                options,
                NullLogger<SandboxGatewayLifetime>.Instance,
                new HttpClient(new UnreachableHandler())
            ),
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new UnreachableHandler()),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmagentinfra-owner-tests", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            )
        );
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Owner resolution must not call the gateway.");
    }
}
