using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// Pins for #671: the spawn receipt has to say what the delegate can actually do. A dispatcher that
/// reads only <c>{agent_id, name, template, status}</c> cannot tell a delegate that holds the board
/// tools from one whose <c>add_tools</c> matched nothing, so a known capability mismatch became
/// silent work debt — the sender believed it had delegated and the delegate could not comply.
/// </summary>
/// <remarks>
/// Every assertion here pins a VALUE or a code, never mere presence: dropping any one array, or
/// swapping <c>registered</c> for <c>projected</c>, has to redden exactly one named test. The
/// mismatch classifications are pure tool-name set membership — no template markers, no prompt
/// inspection — so the same fixtures work for any host's tool vocabulary.
/// </remarks>
public sealed class SubAgentSpawnCapabilityReceiptTests : IAsyncLifetime
{
    private const string TaskTool = "claim-task";
    private const string SecondTaskTool = "list-tasks";
    private const string DomainTool = "get_weather";

    /// <summary>A tool only the per-agent factory can supply — never a parent contract.</summary>
    private const string FactoryTool = "read_own_transcript";

    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly List<SubAgentManager> _managers = [];

    public Task InitializeAsync()
    {
        _ = _parentMock
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
        foreach (var manager in _managers)
        {
            await Wait.ForTeardownAsync(manager, "a sub-agent manager created by this test");
        }
    }

    #region Effective tools

    [Fact]
    public async Task Receipt_ExposesEffectiveTools_NotTemplateMetadata()
    {
        // The template ASKS for a tool the parent does not have. Authored metadata would report both;
        // the effective surface reports only the one that materialized.
        var (manager, _) = CreateManager(Template(enabledTools: [DomainTool, "ghost-tool"]));

        var receipt = await SpawnReceiptAsync(manager);

        Tools(receipt).Should().Contain(DomainTool);
        Tools(receipt)
            .Should()
            .NotContain("ghost-tool", "the receipt reports what the delegate holds, not what the template asked for");
        receipt.GetProperty("tools_source").GetString().Should().Be("registered");
    }

    [Fact]
    public async Task Receipt_ToolsAreTheChildsOwnRegisteredSurface()
    {
        // Discriminator for the test above: the receipt is not a re-derivation that could drift, it is
        // the loop's registered surface. Anything the child can call is on it and vice versa.
        var (manager, _) = CreateManager(InheritAllTemplate());

        var receipt = await SpawnReceiptAsync(manager);
        var childLoop = ChildLoop(manager, receipt.GetProperty("agent_id").GetString()!);

        Tools(receipt).Should().BeEquivalentTo(childLoop.RegisteredToolNames);
    }

    #endregion

    #region Mismatch reporting

    [Fact]
    public async Task Receipt_ReportsUnmatchedAddTools()
    {
        var (manager, _) = CreateManager(InheritAllTemplate());

        var receipt = await SpawnReceiptAsync(manager, addTools: ["tasks:*", DomainTool]);

        Strings(receipt, "unmatched_add_tools")
            .Should()
            .BeEquivalentTo(["tasks:*"], "the entry that DID match is not a mismatch");
        receipt.GetProperty("next_action").GetString().Should().Contain("add_tools");
    }

    [Fact]
    public async Task Receipt_WithNoMismatch_OmitsTheMismatchFields()
    {
        // The paired silence. Without it every assertion above could pass on a receipt that reports a
        // mismatch unconditionally.
        var (manager, _) = CreateManager(InheritAllTemplate());

        var receipt = await SpawnReceiptAsync(manager, addTools: [DomainTool]);

        receipt.TryGetProperty("unmatched_add_tools", out _).Should().BeFalse();
        receipt.TryGetProperty("remove_tools_withheld_nothing", out _).Should().BeFalse();
        receipt.TryGetProperty("empty_inherited_toolset", out _).Should().BeFalse();
        receipt.TryGetProperty("next_action", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Receipt_ReportsRemoveToolsThatWithheldNothing()
    {
        // #638 F-001 through the receipt: "everything except the board tools" silently OVER-grants,
        // because remove_tools has no group language. The dispatcher now sees it at dispatch time.
        var (manager, _) = CreateManager(InheritAllTemplate());

        var receipt = await SpawnReceiptAsync(manager, addTools: ["*"], removeTools: ["tasks:*", SecondTaskTool]);

        Strings(receipt, "remove_tools_withheld_nothing").Should().BeEquivalentTo(["tasks:*"]);
        Tools(receipt).Should().Contain(TaskTool, "the over-grant the array describes is real");
        Tools(receipt).Should().NotContain(SecondTaskTool, "the exact-name entry did withhold its tool");
    }

    [Fact]
    public async Task Receipt_ReportsRemoveToolsRestoredByRequiredTools()
    {
        // Removed, then put back by the mode's required-tools union (#623). Intentional precedence,
        // but from the dispatcher's seat the request was defeated — a different code from a no-op.
        var (manager, _) = CreateManager(
            InheritAllTemplate(),
            options => options with { RequiredToolNames = [TaskTool] }
        );

        var receipt = await SpawnReceiptAsync(manager, addTools: ["*"], removeTools: [TaskTool]);

        Strings(receipt, "remove_tools_restored_by_policy").Should().BeEquivalentTo([TaskTool]);
        receipt
            .TryGetProperty("remove_tools_withheld_nothing", out _)
            .Should()
            .BeFalse("the entry matched — it is not a no-op");
        Tools(receipt).Should().Contain(TaskTool);
    }

    [Fact]
    public async Task Receipt_ReportsEmptyInheritedToolset()
    {
        var (manager, _) = CreateManager(InheritAllTemplate());

        var receipt = await SpawnReceiptAsync(manager, addTools: ["nothing-here"]);

        receipt.GetProperty("empty_inherited_toolset").GetBoolean().Should().BeTrue();
        Tools(receipt)
            .Should()
            .NotContain([TaskTool, SecondTaskTool, DomainTool], "the flag describes a real capability collapse");
    }

    #endregion

    #region #644 — the factory-supplied half, verified by execution

    /// <summary>
    /// #644 asks that the truthful-message class be reused only after its factory-supplied half is
    /// verified by EXECUTION. It is: a tool the host supplies per agent through
    /// <see cref="SubAgentOptions.ChildToolProviderFactory"/> is not a parent contract, so
    /// <c>remove_tools</c> cannot withhold it — the child demonstrably still holds it. Reporting that
    /// entry as "withheld nothing" would be false in the one direction that matters, because the
    /// dispatcher would conclude the delegate lacks a capability it has.
    /// </summary>
    [Fact]
    public async Task FactorySuppliedTool_NamedInRemoveTools_IsReportedUnremovable()
    {
        var (manager, provider) = CreateManager(
            InheritAllTemplate(),
            options => options with { ChildToolProviderFactory = _ => new FactoryToolProvider() },
            collaboration: CreateRegisteredRoot()
        );

        // add_tools "*" only because remove_tools needs a base set to subtract from; the factory tool
        // is not a parent contract, so the wildcard cannot be where the child gets it.
        var receipt = await SpawnReceiptAsync(manager, provider, addTools: ["*"], removeTools: [FactoryTool]);
        var childLoop = ChildLoop(manager, receipt.GetProperty("agent_id").GetString()!);

        childLoop
            .RegisteredToolNames.Should()
            .Contain(FactoryTool, "execution — not inference — is what makes the classification below true");
        Strings(receipt, "unremovable_tools").Should().BeEquivalentTo([FactoryTool]);
        receipt
            .TryGetProperty("remove_tools_withheld_nothing", out _)
            .Should()
            .BeFalse("the child HAS the tool; calling that 'withheld nothing' tells the dispatcher the opposite");
    }

    #endregion

    #region Queued spawns

    [Fact]
    public async Task Receipt_QueuedSpawn_ProjectsToolsAndStillReportsMismatch()
    {
        // A queued spawn has no loop yet, so it cannot report a registered surface. It must not report
        // an EMPTY one either: the dispatcher would read "this delegate has no tools" and route the
        // obligation elsewhere. It projects, says so, and still reports the mismatch it already knows.
        var (manager, _) = CreateManager(InheritAllTemplate(), maxConcurrent: 1);

        _ = await SpawnReceiptAsync(manager);
        var queued = await SpawnReceiptAsync(manager, addTools: ["*", "tasks:*"]);

        queued.GetProperty("status").GetString().Should().Be("queued");
        queued.GetProperty("tools_source").GetString().Should().Be("projected");
        Tools(queued).Should().Contain([TaskTool, SecondTaskTool, DomainTool]);
        Strings(queued, "unmatched_add_tools").Should().BeEquivalentTo(["tasks:*"]);
    }

    #endregion

    #region Live depth and capacity

    [Fact]
    public async Task Receipt_CapacityAndDepthAreLive()
    {
        var root = CreateRegisteredRoot(new AgentCollaborationOptions { MaxDelegationDepth = 1, MaxTotalAgents = 7 });
        var (manager, provider) = CreateManager(InheritAllTemplate(), collaboration: root, maxConcurrent: 5);

        _ = await SpawnReceiptAsync(manager, provider);
        var second = await SpawnReceiptAsync(manager, provider);

        var capacity = second.GetProperty("capacity");
        capacity.GetProperty("running").GetInt32().Should().Be(2, "both blocked children still hold their permits");
        capacity.GetProperty("max_concurrent").GetInt32().Should().Be(5);
        capacity.GetProperty("total").GetInt32().Should().Be(2);
        capacity.GetProperty("max_total").GetInt32().Should().Be(7);

        second.GetProperty("delegation_depth").GetInt32().Should().Be(1);
        second.GetProperty("remaining_delegation_depth").GetInt32().Should().Be(0);
        second
            .GetProperty("can_delegate")
            .GetBoolean()
            .Should()
            .BeFalse("at MaxDelegationDepth 1 the child is a leaf — the dispatcher must not sub-delegate to it");
    }

    [Fact]
    public async Task Receipt_WithoutCollaboration_OmitsDepthAndCapacityRatherThanInventingThem()
    {
        var (manager, _) = CreateManager(InheritAllTemplate());

        var receipt = await SpawnReceiptAsync(manager);

        receipt.TryGetProperty("capacity", out _).Should().BeFalse();
        receipt.TryGetProperty("delegation_depth", out _).Should().BeFalse();
        receipt
            .GetProperty("can_delegate")
            .GetBoolean()
            .Should()
            .BeFalse("a non-collaborating child gets no Agent tool");
    }

    /// <summary>
    /// The projected receipt is built for a QUEUED spawn, and building it can reject the request
    /// (remove_tools with nothing to remove from). That rejection happens after the spawn has already
    /// been admitted to the collaboration, so it has to give the slot back: otherwise every such spawn
    /// permanently shrinks the root-wide cap and the pool eventually admits nobody.
    /// </summary>
    [Fact]
    public async Task QueuedSpawn_RejectedWhileProjectingItsCapability_ReturnsItsCollaborationSlot()
    {
        var root = CreateRegisteredRoot();

        // A pool of one, already full, so the next spawn takes the defer-queue path.
        var (manager, _) = CreateManager(InheritAllTemplate(), collaboration: root, maxConcurrent: 1);
        _ = await manager.SpawnAsync("worker", "work", runInBackground: true, role: "worker", description: "works");
        var admittedBefore = root.Directory.Capacity.InUse;

        // remove_tools with no base set: the template carries no tools list and no add_tools is given.
        var rejected = async () =>
            await manager.SpawnAsync(
                "worker",
                "work",
                runInBackground: true,
                removeTools: [TaskTool],
                role: "worker",
                description: "works"
            );

        // Pin the MESSAGE: SubAgentCollaborationException also derives from InvalidOperationException,
        // so a bare type assertion would pass on an admission failure that never reaches the queue.
        _ = await rejected.Should().ThrowAsync<InvalidOperationException>().WithMessage("*removeTools*");

        root.Directory.Capacity.InUse.Should()
            .Be(admittedBefore, "a spawn that was rejected must not keep the capacity slot it was admitted to");
    }

    #endregion

    #region Harness

    private static IReadOnlyList<string> Tools(JsonElement receipt) => Strings(receipt, "tools");

    private static IReadOnlyList<string> Strings(JsonElement receipt, string property) =>
        [.. receipt.GetProperty(property).EnumerateArray().Select(e => e.GetString()!)];

    private async Task<JsonElement> SpawnReceiptAsync(
        SubAgentManager manager,
        SubAgentToolProvider? provider = null,
        string[]? addTools = null,
        string[]? removeTools = null
    )
    {
        // Under collaboration the spawn must carry directory metadata, so route it through the real
        // Agent handler; without collaboration the manager's own entry point is the shorter path.
        var receipt = provider is null
            ? await manager.SpawnAsync(
                "worker",
                "work",
                runInBackground: true,
                addTools: addTools,
                removeTools: removeTools
            )
            : await SpawnViaToolAsync(provider, addTools, removeTools);

        return JsonDocument.Parse(receipt).RootElement.Clone();
    }

    private static async Task<string> SpawnViaToolAsync(
        SubAgentToolProvider provider,
        string[]? addTools,
        string[]? removeTools
    )
    {
        var handler = provider.GetFunctions().First(f => f.Contract.Name == "Agent").Handler;
        var result = await handler(
            JsonSerializer.Serialize(
                new
                {
                    subagent_type = "worker",
                    prompt = "work",
                    role = "worker role",
                    description = "Does a unit of work.",
                    run_in_background = true,
                    add_tools = addTools is null ? null : string.Join(",", addTools),
                    remove_tools = removeTools is null ? null : string.Join(",", removeTools),
                }
            ),
            new ToolCallContext(),
            CancellationToken.None
        );

        var payload = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload;
        payload.IsError.Should().BeFalse(payload.Text);
        return payload.Text;
    }

    private static MultiTurnAgentLoop ChildLoop(SubAgentManager manager, string agentId)
    {
        manager.TryGetAgent(agentId, out var agent).Should().BeTrue();
        return agent.Should().BeOfType<MultiTurnAgentLoop>().Subject;
    }

    private static AgentCollaborationSetup CreateRegisteredRoot(AgentCollaborationOptions? options = null)
    {
        var setup = AgentCollaborationSetup.CreateRoot(options ?? new AgentCollaborationOptions());
        _ = setup.Directory.TryRegister(
            setup.Context,
            setup.Name,
            AgentCollaborationStatuses.Running,
            new NullEndpoint()
        );
        return setup;
    }

    private (SubAgentManager Manager, SubAgentToolProvider Provider) CreateManager(
        SubAgentTemplate template,
        Func<SubAgentOptions, SubAgentOptions>? configure = null,
        AgentCollaborationSetup? collaboration = null,
        int maxConcurrent = 5
    )
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["worker"] = template },
            MaxConcurrentSubAgents = maxConcurrent,
        };
        options = configure?.Invoke(options) ?? options;

        string[] toolNames = [TaskTool, SecondTaskTool, DomainTool];
        var source = new MutableSubAgentTemplateSource(options.Templates);
        var manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [.. toolNames.Select(Contract)],
            parentHandlers: toolNames.ToDictionary(name => name, _ => OkHandler(), StringComparer.Ordinal),
            options: options,
            source: source,
            collaboration: collaboration
        );

        _managers.Add(manager);
        return (manager, new SubAgentToolProvider(manager, source));
    }

    private static FunctionContract Contract(string name) =>
        new()
        {
            Name = name,
            Description = name,
            Parameters = [],
        };

    private static ToolHandler OkHandler() =>
        (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok"));

    private SubAgentTemplate InheritAllTemplate() => Template(enabledTools: null);

    /// <summary>A template whose child never finishes, so a spawn's permit and capacity stay held.</summary>
    private SubAgentTemplate Template(IReadOnlyList<string>? enabledTools) =>
        new()
        {
            Name = "worker",
            SystemPrompt = "You are a worker.",
            Description = "Does work.",
            EnabledTools = enabledTools,
            AgentFactory = () =>
            {
                var mock = new Mock<IStreamingAgent>();
                _ = mock.Setup(a =>
                        a.GenerateReplyStreamingAsync(
                            It.IsAny<IEnumerable<IMessage>>(),
                            It.IsAny<GenerateReplyOptions>(),
                            It.IsAny<CancellationToken>()
                        )
                    )
                    .Returns<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                        (_, _, ct) => Task.FromResult(BlockingStream(ct))
                    );
                return mock.Object;
            },
        };

    private static async IAsyncEnumerable<IMessage> BlockingStream([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    /// <summary>Stands in for a host tool built per agent — the shape <c>remove_tools</c> cannot reach.</summary>
    private sealed class FactoryToolProvider : IFunctionProvider
    {
        public string ProviderName => "FactoryTools";

        public int Priority => 50;

        public IEnumerable<FunctionDescriptor> GetFunctions() =>
            [new FunctionDescriptor { Contract = Contract(FactoryTool), Handler = OkHandler() }];
    }

    private sealed class NullEndpoint : IAgentWriteEndpoint
    {
        public ValueTask<AgentDeliveryOutcome> DeliverAsync(
            AgentMessage message,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(new AgentDeliveryOutcome(AgentDeliveryDisposition.Delivered));
    }

    #endregion
}
