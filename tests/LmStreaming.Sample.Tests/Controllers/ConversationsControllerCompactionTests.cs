using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Agents;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// <c>POST api/conversations/{threadId}/compaction</c> and the <c>manualCompaction</c> capability. What the loop does
/// with a request is pinned in <c>LmMultiTurn.Tests.CompactionLoopTests</c>; these pin the host contract around it:
/// guards, agent creation, and the 202/409 bodies the client lane builds against.
/// </summary>
public sealed class ConversationsControllerCompactionTests
{
    private static readonly CompactionOptions CompactOn = new() { Mode = CompactionMode.Compact };

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly InMemoryConversationStore _store = new();

    private int _created;

    private ConversationsController CreateController(MultiTurnAgentPool pool, CompactionOptions? options) =>
        new(
            _store,
            pool,
            ConversationsControllerTests.ModeStoreResolvingSystemModes(),
            Mock.Of<IWorkspaceStore>(),
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            new ConversationStatusResolver(_store, _store),
            TimeProvider.System,
            new WorkflowRunRegistry(),
            TestAuthorizers.Disabled(),
            NullLogger<ConversationsController>.Instance,
            NullLogger<AgentHierarchyService>.Instance,
            new SubAgentScanCoverageCache(),
            new ConversationDescendantScanner(_store, NullLogger<ConversationDescendantScanner>.Instance),
            options
        );

    private MultiTurnAgentPool CreatePool(Func<string, IMultiTurnAgent> agent) =>
        new(
            (threadId, _, _) =>
            {
                _created++;
                return new MultiTurnAgentPool.AgentCreationResult(agent(threadId));
            },
            NullLogger<MultiTurnAgentPool>.Instance
        );

    private Task SeedAsync(string threadId) =>
        _store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty.SetItem(
                    MultiTurnAgentPool.ModePropertyKey,
                    SystemChatModes.DefaultModeId
                ),
            }
        );

    private static AgentProfile DefaultMode() => SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

    [Theory]
    [InlineData(ManualCompaction.StatusQueued)]
    [InlineData(ManualCompaction.StatusRunning)]
    public async Task APooledAgentThatTakesTheRequest_Answers202WithItsRequestIdAndStatus_AndGetsTheFocus(string status)
    {
        await SeedAsync("t1");
        var agent = new CompactingFakeAgent("t1")
        {
            Answer = new ManualCompactionResult { RequestId = "req-42", Status = status },
        };
        await using var pool = CreatePool(_ => agent);
        _ = pool.GetOrCreateAgent("t1", DefaultMode());

        var result = await CreateController(pool, CompactOn)
            .RequestCompaction("t1", new ManualCompactionRequest { Focus = "the auth refactor" });

        var accepted = Assert.IsType<ObjectResult>(result);
        accepted.StatusCode.Should().Be(202);
        JsonSerializer.Serialize(accepted.Value, Web).Should().Be($$"""{"requestId":"req-42","status":"{{status}}"}""");
        agent.Requests.Should().Equal("the auth refactor");
        _created.Should().Be(1, "the pooled agent answered; nothing was built for the request");
    }

    [Fact]
    public async Task AnAgentThatIsNotPooled_IsCreatedTheWaySendMessageCreatesIt_AndTakesTheRequest()
    {
        await SeedAsync("t1");
        CompactingFakeAgent? agent = null;
        await using var pool = CreatePool(id => agent = new CompactingFakeAgent(id));

        var result = await CreateController(pool, CompactOn).RequestCompaction("t1", request: null);

        Assert.IsType<ObjectResult>(result).StatusCode.Should().Be(202);
        _created.Should().Be(1);
        agent!.Requests.Should().Equal([null]);
    }

    [Theory]
    [InlineData(ManualCompactionRefusals.CompactionOff)]
    [InlineData(ManualCompactionRefusals.ProviderOwnedSession)]
    [InlineData(ManualCompactionRefusals.AlreadyPending)]
    [InlineData(ManualCompactionRefusals.InProgress)]
    [InlineData(ManualCompactionRefusals.NothingToCompact)]
    [InlineData(ManualCompactionRefusals.NoSafeBoundary)]
    public async Task ARefusalFromTheAgent_Answers409WithOnlyItsReason(string reason)
    {
        await SeedAsync("t1");
        var agent = new CompactingFakeAgent("t1") { Answer = ManualCompactionResult.Refused(reason) };
        await using var pool = CreatePool(_ => agent);

        var result = await CreateController(pool, CompactOn).RequestCompaction("t1", request: null);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        conflict.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(conflict.Value, Web).Should().Be($$"""{"reason":"{{reason}}"}""");
    }

    [Fact]
    public async Task AHostWithCompactionOff_Answers409CompactionOff_WithoutBuildingAnAgent()
    {
        await SeedAsync("t1");
        await using var pool = CreatePool(id => new CompactingFakeAgent(id));

        var result = await CreateController(pool, new CompactionOptions { Mode = CompactionMode.Warn })
            .RequestCompaction("t1", request: null);

        JsonSerializer
            .Serialize(Assert.IsType<ConflictObjectResult>(result).Value, Web)
            .Should()
            .Be("""{"reason":"compaction_off"}""");
        _created.Should().Be(0);
    }

    /// <summary>
    /// The real loop is dispatched through its compaction interface: with no compaction setup it answers
    /// <c>compaction_off</c>. Were it treated as just another provider-hosted <see cref="MultiTurnAgentBase"/>, the
    /// answer would be <c>provider_owned_session</c> instead.
    /// </summary>
    [Fact]
    public async Task TheRealLoop_AnswersForItself()
    {
        await SeedAsync("t1");
        await using var pool = CreatePool(id => new MultiTurnAgentLoop(
            Mock.Of<IStreamingAgent>(),
            new FunctionRegistry(),
            id,
            logger: NullLogger<MultiTurnAgentLoop>.Instance
        ));

        var result = await CreateController(pool, CompactOn).RequestCompaction("t1", request: null);

        JsonSerializer
            .Serialize(Assert.IsType<ConflictObjectResult>(result).Value, Web)
            .Should()
            .Be("""{"reason":"compaction_off"}""");
    }

    [Fact]
    public async Task AnAgentWithNoCompactionRuntime_Answers409CompactionOff()
    {
        await SeedAsync("t1");
        await using var pool = CreatePool(id => new FakeMultiTurnAgent(id));

        var result = await CreateController(pool, CompactOn).RequestCompaction("t1", request: null);

        JsonSerializer
            .Serialize(Assert.IsType<ConflictObjectResult>(result).Value, Web)
            .Should()
            .Be("""{"reason":"compaction_off"}""");
    }

    [Fact]
    public async Task AnAgentOwnedThread_Is403_AndReachesNoAgent()
    {
        await using var pool = CreatePool(id => new CompactingFakeAgent(id));

        var result = await CreateController(pool, CompactOn).RequestCompaction("subagent-child-1", request: null);

        Assert.IsType<ObjectResult>(result).StatusCode.Should().Be(403);
        JsonSerializer.Serialize(((ObjectResult)result).Value).Should().Contain("agent_owned_thread");
        _created.Should().Be(0);
    }

    [Fact]
    public async Task AnUnknownThread_Is404_WithTheBodySendMessageGives()
    {
        await using var pool = CreatePool(id => new CompactingFakeAgent(id));
        var controller = CreateController(pool, CompactOn);

        var compaction = await controller.RequestCompaction("nope", request: null);
        var send = await controller.SendMessage("nope", new SendMessageRequest { Text = "hi" });

        JsonSerializer
            .Serialize(Assert.IsType<NotFoundObjectResult>(compaction).Value)
            .Should()
            .Be(JsonSerializer.Serialize(Assert.IsType<NotFoundObjectResult>(send).Value));
        _created.Should().Be(0);
    }

    /// <summary>
    /// The status frame goes out through the socket's own serializer options as a <c>compaction_status</c> frame with
    /// the camelCase fields the client reads, absent optionals omitted.
    /// </summary>
    [Fact]
    public void TheStatusFrame_ReachesTheSocketAsCompactionStatus()
    {
        IMessage frame = new CompactionStatusMessage
        {
            ThreadId = "t1",
            AgentId = "root",
            RequestId = "req-1",
            Trigger = "manual",
            Phase = CompactionStatusMessage.Phases.Applied,
            CheckpointId = "cp-1",
            Focus = "auth",
        };

        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(
                frame,
                AchieveAi.LmDotnetTools.LmCore.Utils.JsonSerializerOptionsFactory.CreateForProduction()
            )
        );

        var root = json.RootElement;
        root.GetProperty("$type").GetString().Should().Be("compaction_status");
        root.GetProperty("threadId").GetString().Should().Be("t1");
        root.GetProperty("agentId").GetString().Should().Be("root");
        root.GetProperty("requestId").GetString().Should().Be("req-1");
        root.GetProperty("trigger").GetString().Should().Be("manual");
        root.GetProperty("phase").GetString().Should().Be("applied");
        root.GetProperty("checkpointId").GetString().Should().Be("cp-1");
        root.GetProperty("focus").GetString().Should().Be("auth");
        root.TryGetProperty("reason", out _).Should().BeFalse();
    }

    public static TheoryData<CompactionOptions?, bool> CapabilityCases =>
        new()
        {
            { null, false },
            { new CompactionOptions(), false },
            {
                new CompactionOptions { Mode = CompactionMode.Warn },
                false
            },
            {
                new CompactionOptions { Mode = CompactionMode.Compact },
                true
            },
            {
                new CompactionOptions { Mode = CompactionMode.Compact, KillSwitch = true },
                false
            },
            {
                new CompactionOptions
                {
                    ModeByRoute = new Dictionary<string, CompactionMode> { ["test/m"] = CompactionMode.Compact },
                },
                true
            },
        };

    [Theory]
    [MemberData(nameof(CapabilityCases))]
    public async Task Capabilities_AdvertiseManualCompaction_OnlyWhenSomeRouteCompacts(
        CompactionOptions? options,
        bool expected
    )
    {
        await using var pool = CreatePool(id => new FakeMultiTurnAgent(id));

        var ok = Assert.IsType<OkObjectResult>(CreateController(pool, options).GetCapabilities());

        Assert.IsType<ConversationCapabilitiesResponse>(ok.Value).ManualCompaction.Should().Be(expected);
        JsonSerializer
            .Serialize(ok.Value, Web)
            .Should()
            .Contain($"\"manualCompaction\":{(expected ? "true" : "false")}");
    }
}
