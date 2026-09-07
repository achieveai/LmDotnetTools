using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// A completed sub-agent now RETAINS its loop and its owned provider so a follow-up resumes warm
/// (#758 follow-up). That retention has no natural end, so without a total bound a parent that
/// spawns sequentially — each child finishing before the next starts — accumulates live runtimes and
/// providers without limit: <c>MaxConcurrentSubAgents</c> bounds only how many run AT ONCE, and each
/// completion hands its permit straight back.
///
/// A collaboration already bounds this through <c>AgentCollaborationOptions.MaxTotalAgents</c>, but
/// that path is inert when collaboration is off — which is the default. These tests pin the
/// per-manager equivalent: a finite admission ceiling, no eviction (an admitted child is never
/// killed to make room), and a refusal the calling model can act on.
/// </summary>
public class SubAgentRetainedCapacityTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
        _parentMock
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager != null)
        {
            await Wait.ForTeardownAsync(_manager, "the sub-agent manager under test");
        }
    }

    [Fact]
    public async Task SequentialSpawnsPastTheRetainedBound_AreRefusedRatherThanRetainingUnboundedRuntimes()
    {
        // Each child completes before the next is requested, so the concurrency gate is free every
        // time and never refuses anything. The retained runtimes are what accumulate, so the bound
        // that has to bite here is the TOTAL one.
        const int retained = 3;
        _manager = CreateManager(maxConcurrent: 2, maxRetained: retained);
        _manager.TestAgentFactoryOverride = (_, _) =>
            new FakeMultiTurnAgent
            {
                SubscribeImpl = (_, ct) => FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct),
            };

        for (var i = 0; i < retained; i++)
        {
            var json = await _manager.SpawnAsync("worker", $"task-{i}", name: $"worker-{i}", runInBackground: true);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("status").GetString().Should().Be("spawned");

            var id = doc.RootElement.GetProperty("agent_id").GetString()!;
            await Wait.UntilAsync(
                () =>
                    _manager!.TryPeek(id, out var status)
                    && JsonDocument.Parse(status).RootElement.GetProperty("status").GetString() == "completed",
                $"sub-agent {id} reported completed",
                TimeSpan.FromSeconds(10)
            );
        }

        // Every earlier child is finished and holds no permit, so nothing about concurrency stops
        // this spawn. Only the retained-runtime ceiling can.
        var overflow = () => _manager!.SpawnAsync("worker", "one-too-many", runInBackground: true);

        var refusal = await overflow.Should().ThrowAsync<SubAgentCollaborationException>();
        refusal.Which.FailureCode.Should().Be(SubAgentCollaborationFailureCodes.CapacityExhausted);
        refusal
            .Which.Message.Should()
            .Contain(
                retained.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "the refusal has to tell the calling model what the ceiling actually is"
            );

        _manager.ListAgents().Should().HaveCount(retained, "a refusal must not evict or kill an admitted child");
    }

    [Fact]
    public async Task WarmResumingAnAlreadyAdmittedSubAgent_IsNotChargedAgainstTheRetainedBound()
    {
        // Admission counts distinct identities, not runs. Resuming a child that is already admitted
        // must cost nothing — otherwise a long-lived agent that is messaged repeatedly would exhaust
        // the ceiling by itself, and warm resumption would be strictly worse than a cold spawn.
        const int retained = 2;
        _manager = CreateManager(maxConcurrent: 2, maxRetained: retained);
        _manager.TestAgentFactoryOverride = (_, _) =>
            new FakeMultiTurnAgent
            {
                SubscribeImpl = (callIndex, ct) =>
                    callIndex == 1
                        ? FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct)
                        : FakeMultiTurnAgent.WaitForeverStream(ct),
            };

        var spawnJson = await _manager.SpawnAsync("worker", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        await Wait.UntilAsync(
            () =>
                _manager!.TryPeek(agentId, out var status)
                && JsonDocument.Parse(status).RootElement.GetProperty("status").GetString() == "completed",
            "the sub-agent reported completed",
            TimeSpan.FromSeconds(10)
        );

        // Three resumes of the SAME identity against a ceiling of two: a per-resume charge would
        // refuse the second one.
        for (var i = 0; i < 3; i++)
        {
            var resumed = await _manager.SendMessageAsync(agentId, $"follow-up-{i}", runInBackground: true);
            using var resumedDoc = JsonDocument.Parse(resumed);
            resumedDoc.RootElement.GetProperty("status").GetString().Should().NotBeNull();
        }

        _manager.ListAgents().Should().HaveCount(1, "resuming a child does not create another one");

        // And the ceiling is still honoured for genuinely NEW identities: one free slot, then refusal.
        _ = await _manager.SpawnAsync("worker", "second", runInBackground: true);
        var overflow = () => _manager!.SpawnAsync("worker", "third", runInBackground: true);
        _ = await overflow.Should().ThrowAsync<SubAgentCollaborationException>();
    }

    [Fact]
    public async Task ARolledBackSpawn_ReturnsItsRetainedSlotInsteadOfLeakingIt()
    {
        // Admission happens before the spawn can fail, so every pre-start exit has to hand the slot
        // back. A leak here is invisible until the ceiling is silently gone.
        const int retained = 2;
        _manager = CreateManager(maxConcurrent: 2, maxRetained: retained);
        _manager.TestAgentFactoryOverride = (_, template) =>
            new FakeMultiTurnAgent
            {
                SendImpl =
                    template.Name == "explodes"
                        ? _ => ValueTask.FromException<SendReceipt>(new InvalidOperationException("send failed"))
                        : null,
                SubscribeImpl = (_, ct) => FakeMultiTurnAgent.WaitForeverStream(ct),
            };

        for (var i = 0; i < 5; i++)
        {
            var failing = () => _manager!.SpawnAsync("explodes", $"doomed-{i}", runInBackground: true);
            _ = await failing.Should().ThrowAsync<InvalidOperationException>();
        }

        // Five rolled-back spawns must have consumed nothing: the full ceiling is still available.
        for (var i = 0; i < retained; i++)
        {
            var json = await _manager!.SpawnAsync("worker", $"real-{i}", runInBackground: true);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("status").GetString().Should().Be("spawned");
        }
    }

    [Fact]
    public async Task ConcurrentSpawnsRacingTheLastSlots_AdmitExactlyTheCeilingAndRefuseTheRest()
    {
        // The ceiling is charged before any per-manager gate or queue, from whatever thread the spawn
        // arrived on. A check-then-take would let simultaneous spawns all read "room left" and all take
        // it, which is the failure mode a sequential test can never see.
        const int retained = 4;
        const int attempts = 12;
        _manager = CreateManager(maxConcurrent: retained, maxRetained: retained);
        _manager.TestAgentFactoryOverride = (_, _) =>
            new FakeMultiTurnAgent { SubscribeImpl = (_, ct) => FakeMultiTurnAgent.WaitForeverStream(ct) };

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawns = Enumerable
            .Range(0, attempts)
            .Select(i =>
                Task.Run(async () =>
                {
                    await start.Task;
                    try
                    {
                        _ = await _manager!.SpawnAsync("worker", $"racer-{i}", runInBackground: true);
                        return true;
                    }
                    catch (SubAgentCollaborationException)
                    {
                        return false;
                    }
                })
            )
            .ToArray();

        start.SetResult();
        var outcomes = await Task.WhenAll(spawns).WaitAsync(TimeSpan.FromSeconds(30));

        outcomes
            .Count(admitted => admitted)
            .Should()
            .Be(retained, "the ceiling is exactly that many, however many callers race for it");
        _manager.ListAgents().Should().HaveCount(retained);
    }

    private SubAgentManager CreateManager(int maxConcurrent, int maxRetained)
    {
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["worker"] = DummyTemplate("worker"),
            ["explodes"] = DummyTemplate("explodes"),
        };

        var options = new SubAgentOptions
        {
            Templates = templates,
            MaxConcurrentSubAgents = maxConcurrent,
            MaxRetainedSubAgents = maxRetained,
        };

        return new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates)
        );
    }

    private static SubAgentTemplate DummyTemplate(string name) =>
        new()
        {
            Name = name,
            SystemPrompt = "You are a test agent.",
            AgentFactory = () =>
                throw new NotSupportedException("Bypassed by TestAgentFactoryOverride; should never be invoked."),
        };
}
