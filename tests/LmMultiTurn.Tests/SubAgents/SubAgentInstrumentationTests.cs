using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// The measurement seam #670 needs and #669's fixes will be judged against: per-spawn tool-registry
/// construction, per-spawn context fan-out, finished-agent reconstruction, template-catalog
/// serialization, and <c>GetAgents</c> responses.
/// </summary>
/// <remarks>
/// Measurement does not pre-judge any of these as defects. What it does buy is a number that exists
/// BEFORE a fix lands, so a claimed improvement can be checked rather than believed - which is the
/// whole point of the evidence layer. The sink is opt-in (null by default) so every host that does
/// not ask for it pays nothing and behaves byte-for-byte as before.
/// </remarks>
public class SubAgentInstrumentationTests : IAsyncLifetime
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
        if (_manager is not null)
        {
            await Wait.ForTeardownAsync(_manager, "the sub-agent manager under test");
        }
    }

    [Fact]
    public void FreshSink_ReportsZeros_NotAbsence()
    {
        // A zero row must be emitted rather than omitted: a reader of the scored run has to be able
        // to tell "measured, and nothing happened" from "this host never measured".
        var work = new SubAgentInstrumentation().Snapshot();

        work.Spawns.Should().Be(0);
        work.Reconstructions.Should().Be(0);
        work.TemplateCatalogBuilds.Should().Be(0);
        work.DirectoryListings.Should().Be(0);
        work.DirectoryListingBytes.Should().Be(0);
    }

    [Fact]
    public void RecordSpawn_AccumulatesPhaseTotalsAndKeepsEachTiming()
    {
        var sink = new SubAgentInstrumentation();

        sink.RecordSpawn(Timing("a-1", "reviewer", registryMs: 4, fanOutMs: 7, totalMs: 30));
        sink.RecordSpawn(Timing("a-2", "writer", registryMs: 6, fanOutMs: 1, totalMs: 12, reconstructed: true));

        var work = sink.Snapshot();
        work.Spawns.Should().Be(2);

        // A reconstruction is counted separately AND as a spawn: it pays the same construction cost,
        // and the question "how much of this run's construction was re-construction?" is exactly the
        // one the finished-agent-restart bullet asks.
        work.Reconstructions.Should().Be(1);
        work.SpawnToolRegistryMs.Should().Be(10);
        work.SpawnContextFanOutMs.Should().Be(8);
        work.SpawnTotalMs.Should().Be(42);

        sink.Spawns.Select(s => s.AgentId).Should().Equal("a-1", "a-2");
    }

    [Fact]
    public void RecordDirectoryListing_KeepsEntriesAndBytesApart()
    {
        // Both, because they answer different questions: entries is how many agents the directory is
        // being asked to describe, bytes is what the model actually pays for describing them.
        var sink = new SubAgentInstrumentation();

        sink.RecordDirectoryListing(entries: 3, bytes: 900);
        sink.RecordDirectoryListing(entries: 5, bytes: 1500);

        var work = sink.Snapshot();
        work.DirectoryListings.Should().Be(2);
        work.DirectoryListingEntries.Should().Be(8);
        work.DirectoryListingBytes.Should().Be(2400);
    }

    [Fact]
    public void ConcurrentRecording_LosesNothing()
    {
        // The seams are hit from tool dispatch, descriptor building, and spawn construction, which run
        // on different threads. A racy counter would under-report exactly when the run is busiest -
        // the case the measurement exists for.
        var sink = new SubAgentInstrumentation();

        Parallel.For(
            0,
            200,
            i =>
            {
                sink.RecordSpawn(Timing($"a-{i}", "t", registryMs: 1, fanOutMs: 1, totalMs: 1));
                sink.RecordTemplateCatalog(bytes: 10);
                sink.RecordDirectoryListing(entries: 1, bytes: 5);
            }
        );

        var work = sink.Snapshot();
        work.Spawns.Should().Be(200);
        work.SpawnToolRegistryMs.Should().Be(200);
        work.TemplateCatalogBuilds.Should().Be(200);
        work.TemplateCatalogBytes.Should().Be(2000);
        work.DirectoryListingBytes.Should().Be(1000);
        sink.Spawns.Should().HaveCount(200);
    }

    [Fact]
    public void ChildLoopOptions_KeepTheSink()
    {
        // Deliberately NOT cleared alongside the three spawn-authority hooks: a grandchild's spawn is
        // still this run's construction cost, and a sink that stopped at depth 1 would under-report a
        // fan-out shape precisely when it is deepest.
        var sink = new SubAgentInstrumentation();
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>(),
            Instrumentation = sink,
        };

        options.ForChildLoop().Instrumentation.Should().BeSameAs(sink);
    }

    [Fact]
    public void OptionsDefault_LeaveTheSinkOff()
    {
        new SubAgentOptions { Templates = new Dictionary<string, SubAgentTemplate>() }
            .Instrumentation.Should()
            .BeNull("instrumentation is opt-in, so an unconfigured host pays nothing");
    }

    [Fact]
    public async Task Spawn_RecordsRegistryFanOutAndTheInheritedToolCatalogSize()
    {
        var sink = new SubAgentInstrumentation();
        var provider = new Mock<IStreamingAgent>();
        SetupCompletingProvider(provider);

        var manager = CreateManager(provider.Object, sink);
        _ = await manager.SpawnAsync("worker", "do the thing", name: "w", runInBackground: true);

        var timing = sink.Spawns.Should().ContainSingle().Subject;
        timing.Template.Should().Be("worker");
        timing.Reconstructed.Should().BeFalse();
        timing.ToolRegistryMs.Should().BeGreaterThanOrEqualTo(0);
        timing.ContextFanOutMs.Should().BeGreaterThanOrEqualTo(0);
        timing.TotalMs.Should().BeGreaterThanOrEqualTo(timing.ToolRegistryMs);

        // Two inheritable parent contracts, so the catalog the child is handed is non-empty. This is
        // the number the per-spawn tool-registry bullet is about: not how long the copy loop took on
        // one machine, but how much tool surface each spawn re-materializes.
        timing.InheritedToolCount.Should().Be(2);
        timing.ToolCatalogBytes.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// The counter tracks catalog serializations, and since #675 memoized the descriptor surface that
    /// is once per REBUILD rather than once per turn.
    /// </summary>
    /// <remarks>
    /// Asserting both halves is what makes this a measurement rather than a restatement of the memo: a
    /// second identical call must add nothing (or the sink would inflate every long run by its turn
    /// count), and a template change must add exactly one (or the sink would go silent and report a
    /// saving that was really a blind spot). The two directions fail to different numbers, so neither
    /// can be satisfied by a counter that is simply stuck.
    /// </remarks>
    [Fact]
    public void SpawnToolDescriptor_RecordsOneCatalogPerRebuild_NotPerAdvertisement()
    {
        var sink = new SubAgentInstrumentation();
        var provider = new Mock<IStreamingAgent>();
        SetupCompletingProvider(provider);

        var manager = CreateManager(provider.Object, sink);
        var source = new MutableSubAgentTemplateSource(manager.Templates);
        var toolProvider = new SubAgentToolProvider(manager, source);

        _ = toolProvider.GetFunctions().ToList();
        var afterFirst = sink.Snapshot();

        // Same templates, same suppression state: a memo hit, so no new bytes are serialized.
        _ = toolProvider.GetFunctions().ToList();
        var afterRepeat = sink.Snapshot();

        afterFirst.TemplateCatalogBuilds.Should().Be(1);
        afterFirst.TemplateCatalogBytes.Should().BeGreaterThan(0);
        afterRepeat.TemplateCatalogBuilds.Should().Be(1, "an unchanged surface is not re-serialized");
        afterRepeat.TemplateCatalogBytes.Should().Be(afterFirst.TemplateCatalogBytes);

        // Registering a template publishes a new snapshot reference, which is the memo key, so the
        // catalog genuinely has to be rebuilt - and the sink must see it.
        source
            .TryRegister(
                "second",
                new SubAgentTemplate
                {
                    Name = "second",
                    SystemPrompt = "You are another test agent.",
                    AgentFactory = () => provider.Object,
                }
            )
            .Should()
            .BeTrue();

        _ = toolProvider.GetFunctions().ToList();

        var afterChange = sink.Snapshot();
        afterChange.TemplateCatalogBuilds.Should().Be(2, "a changed template set forces a rebuild");
        afterChange.TemplateCatalogBytes.Should().BeGreaterThan(afterFirst.TemplateCatalogBytes);
    }

    private SubAgentManager CreateManager(IStreamingAgent provider, SubAgentInstrumentation sink)
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    Name = "worker",
                    SystemPrompt = "You are a test agent.",
                    AgentFactory = () => provider,
                },
            },
            Instrumentation = sink,
        };

        var manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: InheritableContracts,
            parentHandlers: InheritableHandlers,
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates)
        );
        _manager = manager;
        return manager;
    }

    [Fact]
    public async Task Projection_RoundTripsBothArtifactsThroughThePropertyBag()
    {
        // Offline readability is the whole point: the eval scorer never sees a live host, only the
        // archived store, so anything not in the property bag does not exist as far as it is concerned.
        var store = new InMemoryConversationStore();
        var sink = new SubAgentInstrumentation();
        sink.RecordSpawn(Timing("a-1", "reviewer", registryMs: 4, fanOutMs: 7, totalMs: 30));
        sink.RecordDirectoryListing(entries: 2, bytes: 640);

        await SubAgentInstrumentationProjection.SaveAsync(store, "thread-1", sink);

        var metadata = await store.LoadMetadataAsync("thread-1");
        var work = SubAgentInstrumentationProjection.Persisted(metadata);
        work!.Spawns.Should().Be(1);
        work.SpawnContextFanOutMs.Should().Be(7);
        work.DirectoryListingBytes.Should().Be(640);

        var timings = JsonSerializer.Deserialize<List<SubAgentSpawnTiming>>(
            metadata!.Properties![SubAgentInstrumentationProjection.SpawnTimingsPropertyKey].ToString()!
        );
        timings.Should().ContainSingle().Which.AgentId.Should().Be("a-1");
    }

    [Fact]
    public async Task Projection_PreservesUnrelatedPropertiesAndAFullerEarlierSnapshot()
    {
        var store = new InMemoryConversationStore();
        await store.UpdateMetadataAsync(
            "thread-1",
            _ => new ThreadMetadata
            {
                ThreadId = "thread-1",
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty.SetItem("sample.title", "keep me"),
            }
        );

        var busy = new SubAgentInstrumentation();
        busy.RecordSpawn(Timing("a-1", "reviewer", registryMs: 4, fanOutMs: 7, totalMs: 30));
        busy.RecordSpawn(Timing("a-2", "reviewer", registryMs: 4, fanOutMs: 7, totalMs: 30));
        await SubAgentInstrumentationProjection.SaveAsync(store, "thread-1", busy);

        // A fresh, emptier sink is exactly what a host RESTART produces. Letting it win would report a
        // conversation that spawned two agents as having done no coordination work at all.
        await SubAgentInstrumentationProjection.SaveAsync(store, "thread-1", new SubAgentInstrumentation());

        var metadata = await store.LoadMetadataAsync("thread-1");
        SubAgentInstrumentationProjection.Persisted(metadata)!.Spawns.Should().Be(2);
        metadata!.Properties!["sample.title"].ToString().Should().Be("keep me");
    }

    [Fact]
    public void Projection_TreatsAnUnreadableStampAsNotMeasured()
    {
        // Never a throw: one corrupt property must not take a whole run's projection down with it.
        var metadata = new ThreadMetadata
        {
            ThreadId = "thread-1",
            LastUpdated = 1,
            Properties = ImmutableDictionary<string, object>.Empty.SetItem(
                SubAgentInstrumentationProjection.StartupWorkPropertyKey,
                "{ not json"
            ),
        };

        SubAgentInstrumentationProjection.Persisted(metadata).Should().BeNull();
        SubAgentInstrumentationProjection.Persisted(null).Should().BeNull();
    }

    /// <summary>
    /// Two inheritable parent tools, so a spawn actually re-materializes a non-empty catalog. Their
    /// handlers are never invoked; only the contracts are copied into the child's fresh registry.
    /// </summary>
    private static readonly IReadOnlyList<FunctionContract> InheritableContracts =
    [
        new FunctionContract
        {
            Name = "list-tasks",
            Description = "List the tasks on the board.",
            Parameters = [],
        },
        new FunctionContract
        {
            Name = "claim-task",
            Description = "Claim a task by id.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "id",
                    Description = "The task id.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
            ],
        },
    ];

    private static readonly Dictionary<string, ToolHandler> InheritableHandlers = InheritableContracts.ToDictionary(
        c => c.Name,
        ToolHandler (_) => (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok")),
        StringComparer.Ordinal
    );

    private static void SetupCompletingProvider(Mock<IStreamingAgent> provider) =>
        provider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                (IEnumerable<IMessage> _, GenerateReplyOptions? _, CancellationToken _) =>
                    Task.FromResult(ToAsyncEnumerable(new TextMessage { Text = "done", Role = Role.Assistant }))
            );

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(params IMessage[] messages)
    {
        foreach (var message in messages)
        {
            yield return message;
            await Task.Yield();
        }
    }

    private static SubAgentSpawnTiming Timing(
        string agentId,
        string template,
        long registryMs,
        long fanOutMs,
        long totalMs,
        bool reconstructed = false
    ) =>
        new()
        {
            AgentId = agentId,
            Template = template,
            ToolRegistryMs = registryMs,
            ContextFanOutMs = fanOutMs,
            TotalMs = totalMs,
            InheritedToolCount = 0,
            ToolCatalogBytes = 0,
            Reconstructed = reconstructed,
        };
}
