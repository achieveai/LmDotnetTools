using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Agents;

/// <summary>
/// Covers issue #153 M2's cross-actor caller-credential guard on <see cref="MultiTurnAgentPool"/>:
/// <see cref="MultiTurnAgentPool.AgentCreationContext.CallerCredential"/> reaching the factory, the
/// credential staying frozen across a mode/provider recreate, and the in-lock conflict guard in
/// <see cref="MultiTurnAgentPool.GetOrCreateAgent(string, AgentProfile, string?, string?, string?, SandboxCredential?)"/>
/// matching the Cross-Actor Resume Matrix. Kept in its own file — separate from
/// <see cref="MultiTurnAgentPoolTests"/> — per the issue's test-strategy split.
/// </summary>
[Collection("EnvironmentVariables")]
public class MultiTurnAgentPoolCallerCredentialTests
{
    [Fact]
    public async Task GetOrCreateAgent_PassesCallerCredential_ToFactory()
    {
        SandboxCredential? seen = null;
        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                seen = context.CallerCredential;
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId));
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var credential = new SandboxCredential("caller-a", "key-a");

        _ = pool.GetOrCreateAgent(
            "thread-cred-context",
            mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: null,
            callerCredential: credential
        );

        seen.Should().Be(credential);
    }

    [Fact]
    public async Task GetOrCreateAgent_PassesNullCallerCredential_ToFactory_ForPlainUiCaller()
    {
        var seen = new List<SandboxCredential?>();
        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                seen.Add(context.CallerCredential);
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId));
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

        // No callerCredential argument: defaults to null, exactly like every existing WS/UI call site.
        _ = pool.GetOrCreateAgent("thread-cred-null", mode);

        seen.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task RecreateAgentWithModeAsync_PreservesFrozenCallerCredential()
    {
        var seenCredentials = new List<SandboxCredential?>();
        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                seenCredentials.Add(context.CallerCredential);
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId));
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var credential = new SandboxCredential("caller-mode-switch", "key-mode-switch");
        _ = pool.GetOrCreateAgent(
            "thread-mode-cred",
            mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: null,
            callerCredential: credential
        );

        var newMode = SystemChatModes.All[0];
        _ = await pool.RecreateAgentWithModeAsync("thread-mode-cred", newMode);

        // Two creations: the original + the recreate. Both must have seen the SAME frozen credential —
        // a mode switch is neither a create nor a cross-actor request, so it must not drop/change it.
        seenCredentials.Should().HaveCount(2);
        seenCredentials[0].Should().Be(credential);
        seenCredentials[1].Should().Be(credential, "a mode switch must preserve the frozen caller credential");
    }

    [Fact]
    public async Task RecreateAgentWithProviderAsync_PreservesFrozenCallerCredential()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "openai"]);
        var store = new InMemoryConversationStore();
        var seenCredentials = new List<SandboxCredential?>();

        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                seenCredentials.Add(context.CallerCredential);
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId));
            },
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var credential = new SandboxCredential("caller-provider-switch", "key-provider-switch");
        _ = pool.GetOrCreateAgent(
            "thread-prov-cred",
            mode,
            requestedProviderId: "test",
            requestResponseDumpFileName: null,
            requestedWorkspaceId: null,
            callerCredential: credential
        );

        _ = await pool.RecreateAgentWithProviderAsync("thread-prov-cred", "openai", mode);

        seenCredentials.Should().HaveCount(2);
        seenCredentials[0].Should().Be(credential);
        seenCredentials[1].Should().Be(credential, "a provider switch must preserve the frozen caller credential");
    }

    [Fact]
    public async Task GetOrCreateAgent_Throws_WhenSecondCallHasDifferentAppId()
    {
        await using var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)),
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var credentialA = new SandboxCredential("caller-a", "key-a");
        var credentialB = new SandboxCredential("caller-b", "key-b");

        _ = pool.GetOrCreateAgent(
            "thread-conflict",
            mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: null,
            callerCredential: credentialA
        );

        var act = () =>
            pool.GetOrCreateAgent(
                "thread-conflict",
                mode,
                requestedProviderId: null,
                requestResponseDumpFileName: null,
                requestedWorkspaceId: null,
                callerCredential: credentialB
            );

        var thrown = act.Should().Throw<SandboxCredentialConflictException>().Which;
        thrown.ThreadId.Should().Be("thread-conflict");
        thrown.ExistingAppId.Should().Be("caller-a");
        thrown.RequestedAppId.Should().Be("caller-b");
        thrown.Message.Should().NotContain("key-a").And.NotContain("key-b");
    }

    [Fact]
    public async Task GetOrCreateAgent_DoesNotThrow_WhenSecondCallHasSameAppId()
    {
        await using var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)),
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

        // Same AppId; the key need not even match (only AppId is compared) — this simulates the same
        // caller re-sending its credential on a later turn.
        var first = pool.GetOrCreateAgent(
            "thread-same-caller",
            mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: null,
            callerCredential: new SandboxCredential("caller-same", "key-1")
        );

        var second = pool.GetOrCreateAgent(
            "thread-same-caller",
            mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: null,
            callerCredential: new SandboxCredential("caller-same", "key-2")
        );

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task GetOrCreateAgent_DoesNotThrow_WhenBothCallsAreNullCredential()
    {
        // UI <-> UI: two plain interactive reconnects, neither carrying a caller credential.
        await using var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)),
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var first = pool.GetOrCreateAgent("thread-ui-ui", mode);
        var second = pool.GetOrCreateAgent("thread-ui-ui", mode);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task GetOrCreateAgent_Throws_WhenUiCallerFollowsS2SCreator()
    {
        // S2S-A creates, then a plain UI reconnect (no credential) tries to continue -> conflict.
        await using var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)),
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent(
            "thread-s2s-then-ui",
            mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: null,
            callerCredential: new SandboxCredential("caller-s2s", "key-s2s")
        );

        var act = () => pool.GetOrCreateAgent("thread-s2s-then-ui", mode);

        var thrown = act.Should().Throw<SandboxCredentialConflictException>().Which;
        thrown.ExistingAppId.Should().Be("caller-s2s");
        thrown.RequestedAppId.Should().BeNull();
    }

    [Fact]
    public async Task GetOrCreateAgent_Throws_WhenS2SCallerFollowsUiCreator()
    {
        // Plain UI creates (no credential), then S2S tries to continue -> conflict (reverse direction).
        await using var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)),
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent("thread-ui-then-s2s", mode);

        var act = () =>
            pool.GetOrCreateAgent(
                "thread-ui-then-s2s",
                mode,
                requestedProviderId: null,
                requestResponseDumpFileName: null,
                requestedWorkspaceId: null,
                callerCredential: new SandboxCredential("caller-s2s", "key-s2s")
            );

        var thrown = act.Should().Throw<SandboxCredentialConflictException>().Which;
        thrown.ExistingAppId.Should().BeNull();
        thrown.RequestedAppId.Should().Be("caller-s2s");
    }

    [Fact]
    public async Task GetOrCreateAgent_ConcurrentAlternatingCredentials_NeverCorruptsPoolState()
    {
        // Stress test: ~100 concurrent first-touch calls on ONE threadId, alternating between two
        // distinct caller credentials. Whichever call wins the per-thread creation lock first freezes
        // its credential onto the entry; every other call must either match (same AppId -> success,
        // same instance) or conflict (different AppId -> SandboxCredentialConflictException). The
        // factory must run exactly once — no torn/duplicate entries, no leaked double-construction.
        const string threadId = "thread-concurrent-cred";
        const int callCount = 100;
        var credentialX = new SandboxCredential("caller-x", "key-x");
        var credentialY = new SandboxCredential("caller-y", "key-y");

        var factoryInvocations = 0;
        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                Interlocked.Increment(ref factoryInvocations);
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId));
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        using var barrier = new Barrier(callCount);

        // Avoid ThreadPool ramp-up throttling: with the default min thread count, injecting 100
        // simultaneously-blocking (Barrier.SignalAndWait) work items can trickle out slowly instead
        // of actually racing. Raise the floor so all participants can start promptly.
        ThreadPool.GetMinThreads(out var minWorkerThreads, out var minCompletionPortThreads);
        ThreadPool.SetMinThreads(Math.Max(minWorkerThreads, callCount + 10), minCompletionPortThreads);

        var tasks = Enumerable
            .Range(0, callCount)
            .Select(i =>
                Task.Run<(bool Success, IMultiTurnAgent? Agent, SandboxCredential Credential, Exception? Exception)>(
                    () =>
                {
                    var credential = i % 2 == 0 ? credentialX : credentialY;
                    barrier.SignalAndWait();
                    try
                    {
                        var agent = pool.GetOrCreateAgent(
                            threadId,
                            mode,
                            requestedProviderId: null,
                            requestResponseDumpFileName: null,
                            requestedWorkspaceId: null,
                            callerCredential: credential
                        );
                        return (Success: true, Agent: agent, Credential: credential, Exception: null);
                    }
                    catch (SandboxCredentialConflictException ex)
                    {
                        return (
                            Success: false,
                            Agent: null,
                            Credential: credential,
                            Exception: ex
                        );
                    }
                })
            )
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Exactly one factory invocation: no torn/duplicate entries, no double-construction race.
        factoryInvocations.Should().Be(1);
        pool.ActiveAgentCount.Should().Be(1);

        // Exactly one credential "won" the race and got frozen onto the entry.
        var winningAppIds = results.Where(r => r.Success).Select(r => r.Credential.AppId).Distinct().ToList();
        winningAppIds.Should().ContainSingle("exactly one caller credential must win the creation race");
        var winningAppId = winningAppIds[0];

        // Every successful call carried the winning credential and got back the SAME agent instance.
        var successes = results.Where(r => r.Success).ToList();
        successes.Should().NotBeEmpty();
        successes.Should().OnlyContain(r => r.Credential.AppId == winningAppId);
        successes
            .Select(r => r.Agent)
            .Distinct()
            .Should()
            .ContainSingle("every winning call must return the same pooled agent");

        // Every failed call carried the losing credential and threw the conflict exception with the
        // correct existing/requested app ids.
        var failures = results.Where(r => !r.Success).ToList();
        failures.Should().NotBeEmpty();
        failures.Should().OnlyContain(r => r.Credential.AppId != winningAppId);
        foreach (var failure in failures)
        {
            var conflict = failure.Exception.Should().BeOfType<SandboxCredentialConflictException>().Which;
            conflict.ExistingAppId.Should().Be(winningAppId);
            conflict.RequestedAppId.Should().Be(failure.Credential.AppId);
        }

        successes.Count.Should().Be(callCount / 2, "half the calls carry the winning credential's AppId");
        failures.Count.Should().Be(callCount / 2, "the other half carry the losing credential's AppId");
    }
}
