using System.Collections.Concurrent;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Agents;

/// <summary>
/// The pool's terminal boundary. <see cref="MultiTurnAgentPool.DisposeAsync"/> used to read a plain
/// <c>_disposed</c> flag that every creation path had already checked and moved past: a creation whose
/// factory was still running when disposal began committed its entry into a dictionary disposal had
/// already snapshotted and was about to <c>Clear()</c> — so the agent, its background run loop and its
/// owned resources (MCP clients in production) survived the pool with nobody left to stop them.
/// <para>
/// The barrier is the agent factory itself: parking inside it holds a creation in flight, past the
/// disposed check and before the commit, with no sleeping and no reliance on thread timing.
/// </para>
/// </summary>
[Collection("EnvironmentVariables")]
public sealed class MultiTurnAgentPoolDisposalRaceTests
{
    /// <summary>Failure budget for a wait a correct implementation satisfies immediately.</summary>
    private static readonly TimeSpan Guardrail = TimeSpan.FromSeconds(15);

    /// <summary>Stands in for the MCP clients a real factory hands the pool to own and dispose.</summary>
    private sealed class TrackingOwnedResource : IAsyncDisposable
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync()
        {
            _ = Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private static AgentProfile DefaultMode => SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

    private static async Task<Exception?> CaptureAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task GetOrCreateAgent_WhoseFactoryOutlivesPoolDisposal_IsRefused_AndItsAgentIsNotLeaked()
    {
        var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeMultiTurnAgent? built = null;
        var ownedResource = new TrackingOwnedResource();

        var pool = new MultiTurnAgentPool(
            (threadId, _, _) =>
            {
                built = new FakeMultiTurnAgent(threadId);
                _ = factoryEntered.TrySetResult();

                // Parked INSIDE creation: the caller is past GetOrCreateAgent's disposed check and has
                // not yet committed. This is the whole window the finding is about.
                factoryGate.Task.Wait(Guardrail);
                return new MultiTurnAgentPool.AgentCreationResult(built, [ownedResource]);
            },
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var create = Task.Run(() => pool.GetOrCreateAgent("late-thread", DefaultMode));
        await factoryEntered.Task.WaitAsync(Guardrail);

        await pool.DisposeAsync();
        factoryGate.SetResult();

        var error = await CaptureAsync(create);

        // The leak IS the finding, so it is asserted first: before the fix this agent (and its owned,
        // MCP-client-shaped resource) were handed to a pool that had already snapshotted and cleared its
        // agent map, leaving a live agent nobody would ever stop.
        built.Should().NotBeNull();
        await built!.DisposedSignal.Task.WaitAsync(Guardrail);
        await ownedResource.Disposed.Task.WaitAsync(Guardrail);

        error
            .Should()
            .BeOfType<ObjectDisposedException>("a pool that has been disposed must refuse a creation, not complete it")
            .Which.ObjectName.Should()
            .Contain(
                nameof(MultiTurnAgentPool),
                "the refusal must come from the pool's own lifecycle boundary — an incidental "
                    + "ObjectDisposedException from the pool's CancellationTokenSource means the "
                    + "creation got that far and its agent was already built"
            );

        pool.ActiveAgentCount.Should().Be(0);
    }

    [Fact]
    public async Task RecreateAgentWithModeAsync_WhoseFactoryOutlivesPoolDisposal_IsRefused_AndItsAgentIsNotLeaked()
    {
        var factoryCalls = 0;
        var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeMultiTurnAgent? replacement = null;
        var replacementResource = new TrackingOwnedResource();

        var pool = new MultiTurnAgentPool(
            (threadId, _, _) =>
            {
                // Only the SECOND creation (the swap) parks; the first has to complete so there is an
                // agent to recreate.
                if (Interlocked.Increment(ref factoryCalls) == 1)
                {
                    return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId));
                }

                replacement = new FakeMultiTurnAgent(threadId);
                _ = factoryEntered.TrySetResult();
                factoryGate.Task.Wait(Guardrail);
                return new MultiTurnAgentPool.AgentCreationResult(replacement, [replacementResource]);
            },
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var original = (FakeMultiTurnAgent)pool.GetOrCreateAgent("swap-thread", DefaultMode);

        // Task.Run because the swap runs its factory synchronously, under the per-thread creation lock,
        // before its first await — calling it inline would park the test thread inside the factory.
        var swap = Task.Run(() => pool.RecreateAgentWithModeAsync("swap-thread", DefaultMode));
        await factoryEntered.Task.WaitAsync(Guardrail);

        await pool.DisposeAsync();
        factoryGate.SetResult();

        var error = await CaptureAsync(swap);

        replacement.Should().NotBeNull();
        await replacement!.DisposedSignal.Task.WaitAsync(Guardrail);
        await replacementResource.Disposed.Task.WaitAsync(Guardrail);

        error
            .Should()
            .BeOfType<ObjectDisposedException>()
            .Which.ObjectName.Should()
            .Contain(nameof(MultiTurnAgentPool));

        // The entry that WAS committed before disposal began is still the pool's to dispose.
        original.DisposeCount.Should().Be(1);
        pool.ActiveAgentCount.Should().Be(0);
    }

    /// <summary>
    /// Positive control for the snapshot-and-clear the fix moves under the lifecycle lock: an agent
    /// committed before disposal begins must still be disposed exactly once. Passes before the fix too —
    /// it exists so a fix that made the race impossible by disposing nothing would fail loudly.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_DisposesEveryCommittedAgentExactlyOnce()
    {
        var agents = new List<FakeMultiTurnAgent>();
        var pool = new MultiTurnAgentPool(
            (threadId, _, _) =>
            {
                var agent = new FakeMultiTurnAgent(threadId);
                lock (agents)
                {
                    agents.Add(agent);
                }

                return new MultiTurnAgentPool.AgentCreationResult(agent);
            },
            NullLogger<MultiTurnAgentPool>.Instance
        );

        _ = pool.GetOrCreateAgent("committed-1", DefaultMode);
        _ = pool.GetOrCreateAgent("committed-2", DefaultMode);

        await pool.DisposeAsync();

        agents.Should().HaveCount(2);
        agents.Should().OnlyContain(a => a.DisposeCount == 1);
        pool.ActiveAgentCount.Should().Be(0);
    }

    /// <summary>
    /// <see cref="MultiTurnAgentPool.RemoveAgentAsync"/> against <see cref="MultiTurnAgentPool.DisposeAsync"/>:
    /// exactly one path may own an entry's teardown.
    /// <para>
    /// Pool disposal snapshots <c>_agents.Values</c> and then clears the map. A bare
    /// <c>ConcurrentDictionary.TryRemove</c> in RemoveAgentAsync took neither of those locks, so a remove
    /// landing BETWEEN the snapshot and the clear handed the same entry to both paths. Running
    /// <c>AgentEntry.DisposeAsync</c> twice is destructive, not merely wasteful: it cancels a
    /// CancellationTokenSource it later disposes (cancelling a disposed source throws
    /// ObjectDisposedException) and calls <c>IMultiTurnAgent.StopAsync</c>, which is not concurrency-safe.
    /// </para>
    /// <para>
    /// The window is two instructions wide and sits between two lock acquisitions inside the pool, so no
    /// external barrier can be scheduled into it — the start is synchronised (one gate releases every
    /// racer at once) but the interleaving is not. The ASSERTIONS are exact: a double claim shows up
    /// either as a throw out of RemoveAgentAsync or as a DisposeCount of 2, and neither is possible when
    /// one entry has one owner.
    /// </para>
    /// <para>
    /// The trial count is calibrated, not guessed. Measured against the pre-claim-protocol build
    /// (bare <c>TryRemove</c>, everything else in this patch already applied): at 40 trials it detected
    /// the defect in 2 of 5 runs — a guard that misses a broken build three times in five is worse than
    /// none. At 250 trials it detected it in 10 of 10, always with the same signature
    /// (<c>DisposeCount == 2</c>), while the fixed build's full 250 trials cost well under a second.
    /// If this test ever has to be weakened for runtime, weaken it by measuring again, not by halving
    /// the constant.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RemoveAgentAsync_RacingPoolDisposal_NeverLetsTwoPathsDisposeTheSameEntry()
    {
        const int threadsPerTrial = 32;
        const int trials = 250;

        for (var trial = 0; trial < trials; trial++)
        {
            var agents = new ConcurrentDictionary<string, FakeMultiTurnAgent>();
            var pool = new MultiTurnAgentPool(
                (threadId, _, _) =>
                {
                    var agent = new FakeMultiTurnAgent(threadId);
                    agents[threadId] = agent;
                    return new MultiTurnAgentPool.AgentCreationResult(agent);
                },
                NullLogger<MultiTurnAgentPool>.Instance
            );

            var threadIds = Enumerable.Range(0, threadsPerTrial).Select(i => $"t{trial}-{i}").ToArray();
            foreach (var threadId in threadIds)
            {
                _ = pool.GetOrCreateAgent(threadId, DefaultMode);
            }

            // One gate releases every racer at once, so the removes and the disposal are genuinely
            // concurrent rather than serialised by their own start-up cost.
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var racers = threadIds
                .Select(threadId =>
                    Task.Run(async () =>
                    {
                        await start.Task;
                        await pool.RemoveAgentAsync(threadId);
                    })
                )
                .Append(
                    Task.Run(async () =>
                    {
                        await start.Task;
                        await pool.DisposeAsync();
                    })
                )
                .ToArray();

            start.SetResult();

            var failures = new List<Exception>();
            foreach (var racer in racers)
            {
                try
                {
                    await racer.WaitAsync(Guardrail);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            failures
                .Should()
                .BeEmpty(
                    "a second owner reaches AgentEntry.DisposeAsync after the first disposed the entry's "
                        + "CancellationTokenSource, and cancelling a disposed source throws"
                );

            agents
                .Values.Should()
                .OnlyContain(a => a.DisposeCount == 1, "each entry has exactly one owner, so exactly one teardown");

            pool.ActiveAgentCount.Should().Be(0);
        }
    }

    /// <summary>
    /// The claim protocol must not change the ordinary sequential contract: a removed agent is disposed
    /// once by the remover, and the pool's later disposal must not touch it again. Passes before the fix
    /// as well — it is the control that stops the protocol from being satisfied by removing nothing.
    /// </summary>
    [Fact]
    public async Task RemoveAgentAsync_ThenPoolDisposal_DisposesTheRemovedAgentExactlyOnce()
    {
        FakeMultiTurnAgent? agent = null;
        var pool = new MultiTurnAgentPool(
            (threadId, _, _) =>
            {
                agent = new FakeMultiTurnAgent(threadId);
                return new MultiTurnAgentPool.AgentCreationResult(agent);
            },
            NullLogger<MultiTurnAgentPool>.Instance
        );

        _ = pool.GetOrCreateAgent("removed-thread", DefaultMode);
        await pool.RemoveAgentAsync("removed-thread");

        agent.Should().NotBeNull();
        agent!.DisposeCount.Should().Be(1);
        pool.ActiveAgentCount.Should().Be(0);

        await pool.DisposeAsync();

        agent.DisposeCount.Should().Be(1, "the remover already owned this entry; disposal must not re-own it");

        // A remove after the pool drained is a no-op, not a second teardown and not a throw.
        await pool.RemoveAgentAsync("removed-thread");
        agent.DisposeCount.Should().Be(1);
    }
}
