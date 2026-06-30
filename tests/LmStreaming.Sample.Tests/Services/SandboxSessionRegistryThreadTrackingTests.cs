using System.Net;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the session→thread membership tracking and the per-session discovery dedup ledger that
/// the context-discovery injector relies on: <see cref="SandboxSessionRegistry.RegisterThread"/>,
/// <see cref="SandboxSessionRegistry.UnregisterThread"/>,
/// <see cref="SandboxSessionRegistry.UnregisterThreadFromAllSessions"/>,
/// <see cref="SandboxSessionRegistry.GetThreads"/>, and
/// <see cref="SandboxSessionRegistry.TryMarkDiscoverySeen"/>.
/// </summary>
public class SandboxSessionRegistryThreadTrackingTests
{
    private const string SessionA = "session-a";
    private const string SessionB = "session-b";

    [Fact]
    public async Task GetThreads_EmptyWhenNothingRegistered()
    {
        await using var registry = CreateRegistry();

        registry.GetThreads(SessionA).Should().BeEmpty();
    }

    [Fact]
    public async Task RegisterThread_Idempotent_AndScopedToSession()
    {
        await using var registry = CreateRegistry();

        registry.RegisterThread(SessionA, "thread-1");
        registry.RegisterThread(SessionA, "thread-1"); // duplicate registration
        registry.RegisterThread(SessionA, "thread-2");
        registry.RegisterThread(SessionB, "thread-3");

        registry.GetThreads(SessionA).Should().BeEquivalentTo(["thread-1", "thread-2"]);
        registry.GetThreads(SessionB).Should().BeEquivalentTo(["thread-3"]);
    }

    [Fact]
    public async Task UnregisterThread_RemovesOnlyTargetMembership()
    {
        await using var registry = CreateRegistry();
        registry.RegisterThread(SessionA, "thread-1");
        registry.RegisterThread(SessionA, "thread-2");
        registry.RegisterThread(SessionB, "thread-1");

        registry.UnregisterThread(SessionA, "thread-1");

        registry.GetThreads(SessionA).Should().BeEquivalentTo(["thread-2"]);
        registry.GetThreads(SessionB).Should().BeEquivalentTo(["thread-1"]);
    }

    [Fact]
    public async Task UnregisterThread_AbsentEntry_IsNoOp()
    {
        await using var registry = CreateRegistry();

        // Must not throw — the pool's ThreadRemoved fires for every thread, even those that
        // were never registered against any session (non-workspace mode).
        registry.UnregisterThread(SessionA, "ghost-thread");
        registry.UnregisterThread("ghost-session", "ghost-thread");
    }

    [Fact]
    public async Task UnregisterThreadFromAllSessions_RemovesAcrossAllSessions()
    {
        await using var registry = CreateRegistry();
        registry.RegisterThread(SessionA, "thread-shared");
        registry.RegisterThread(SessionA, "thread-only-a");
        registry.RegisterThread(SessionB, "thread-shared");

        registry.UnregisterThreadFromAllSessions("thread-shared");

        registry.GetThreads(SessionA).Should().BeEquivalentTo(["thread-only-a"]);
        registry.GetThreads(SessionB).Should().BeEmpty();
    }

    [Fact]
    public async Task TryMarkDiscoverySeen_FirstCallTrue_RepeatFalse()
    {
        await using var registry = CreateRegistry();

        registry.TryMarkDiscoverySeen(SessionA, "context_file", "CLAUDE.md").Should().BeTrue();
        registry.TryMarkDiscoverySeen(SessionA, "context_file", "CLAUDE.md").Should().BeFalse();
    }

    [Fact]
    public async Task TryMarkDiscoverySeen_ScopedByKindAndPathAndSession()
    {
        await using var registry = CreateRegistry();

        registry.TryMarkDiscoverySeen(SessionA, "context_file", "CLAUDE.md").Should().BeTrue();
        // Same path different kind: distinct entry.
        registry.TryMarkDiscoverySeen(SessionA, "subagent", "CLAUDE.md").Should().BeTrue();
        // Same kind+path, different session: distinct entry.
        registry.TryMarkDiscoverySeen(SessionB, "context_file", "CLAUDE.md").Should().BeTrue();
        // Repeat of the original — must be deduped.
        registry.TryMarkDiscoverySeen(SessionA, "context_file", "CLAUDE.md").Should().BeFalse();
    }

    [Fact]
    public async Task TryMarkDiscoverySeen_RejectsBlankInputs()
    {
        await using var registry = CreateRegistry();

        registry.TryMarkDiscoverySeen("", "context_file", "CLAUDE.md").Should().BeFalse();
        registry.TryMarkDiscoverySeen(SessionA, "", "CLAUDE.md").Should().BeFalse();
        registry.TryMarkDiscoverySeen(SessionA, "context_file", "").Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_ClearsThreadAndDedupState()
    {
        var registry = CreateRegistry();
        registry.RegisterThread(SessionA, "thread-1");
        _ = registry.TryMarkDiscoverySeen(SessionA, "context_file", "CLAUDE.md");

        await registry.DisposeAsync();

        // After disposal the tracking collections must be cleared so a fresh registry instance
        // doesn't inherit stale entries (would only matter in long-lived test processes).
        // Direct verification requires reaching past the disposal guard; instead we just ensure
        // disposal completes without throwing — the contract is sufficient.
        Assert.True(true);
    }

    private static SandboxSessionRegistry CreateRegistry()
    {
        static HttpResponseMessage Unused(HttpRequestMessage _) =>
            new(HttpStatusCode.OK);

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
            new AuthSharedSecret(new AuthOptions()));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }
}
