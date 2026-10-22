using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// <see cref="SubAgentOptions.InheritedToolOverride"/> (slice 1c): what a spawned child's FIRST model request
/// carries when the host hands it its own version of an inherited tool, hands nothing back, or fails. The
/// child's request is the observable: it is what the model sees, whatever the registry does internally.
/// </summary>
public sealed class SubAgentInheritedToolOverrideTests : IAsyncLifetime
{
    private const string SkillTool = "Skill";
    private const string SearchTool = "SearchSkill";
    private const string ReadTool = "Read";

    private static readonly FunctionContract ParentSkill = Contract(SkillTool, "Skills: every skill (the parent's)");
    private static readonly FunctionContract ChildSkill = Contract(SkillTool, "Skills: alpha:p, beta:c (the child's)");

    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly List<SubAgentManager> _managers = [];
    private readonly CapturingLogger<SubAgentManager> _logger = new();
    private readonly TaskCompletionSource<IReadOnlyList<FunctionContract>> _childTools = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

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

    // Fails if the library adds the parent's plain function instead of (or as well as) the override's provider.
    [Fact]
    public async Task OverrideProvider_ReplacesTheInheritedTool_ChildRequestCarriesItsContract()
    {
        var offered = new ConcurrentQueue<InheritedTool>();
        var manager = CreateManager(
            enabledTools: null,
            (tool, _) =>
            {
                offered.Enqueue(tool);
                return ValueTask.FromResult<IFunctionProvider?>(
                    tool.Contract.Name == SkillTool ? new OneToolProvider(ChildSkill, tool.Handler) : null
                );
            }
        );

        var agentId = await SpawnAsync(manager);
        var tools = await ChildRequestToolsAsync();

        tools.Select(t => t.Name).Should().OnlyHaveUniqueItems();
        tools.Single(t => t.Name == SkillTool).Description.Should().Be(ChildSkill.Description);
        Json(tools.Single(t => t.Name == ReadTool)).Should().Be(Json(Contract(ReadTool)));
        offered
            .Select(t => (t.AgentId, t.Contract.Name))
            .Should()
            .BeEquivalentTo([(agentId, SkillTool), (agentId, SearchTool), (agentId, ReadTool)]);
    }

    // Fails if a null from the override drops or rewrites the inherited tool instead of inheriting it as before.
    [Fact]
    public async Task OverrideReturnsNull_ChildInheritsTheParentContract_ByteIdentical()
    {
        var offered = 0;
        var manager = CreateManager(
            enabledTools: null,
            (_, _) =>
            {
                _ = Interlocked.Increment(ref offered);
                return ValueTask.FromResult<IFunctionProvider?>(null);
            }
        );

        _ = await SpawnAsync(manager);
        var tools = await ChildRequestToolsAsync();

        offered.Should().Be(3, "the override was consulted for each inherited tool");
        Json(tools.Single(t => t.Name == SkillTool)).Should().Be(Json(ParentSkill));
        Json(tools.Single(t => t.Name == SearchTool)).Should().Be(Json(Contract(SearchTool)));
        _logger.WarningCount("Inherited tool override failed").Should().Be(0);
    }

    // Fails if a host fault escapes the override (the spawn fails) or is swallowed without a WARN. The library
    // passes no caller token to the override (none reaches a spawn), so an OperationCanceledException from it is
    // the host's own timeout, a fault like any other; caller cancellation is pinned at the host seam
    // (CapabilityTiersTests.ForInheritedTool_CallerCancelled_Propagates_NoWarn).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverrideThrows_ChildInheritsTheParentTool_OneWarn_SpawnSucceeds(bool hostTimeout)
    {
        var manager = CreateManager(
            enabledTools: null,
            (tool, _) =>
                tool.Contract.Name == SkillTool
                    ? throw (
                        hostTimeout
                            ? new OperationCanceledException("host budget")
                            : new InvalidOperationException("gateway down")
                    )
                    : ValueTask.FromResult<IFunctionProvider?>(null)
        );

        var agentId = await SpawnAsync(manager);
        var tools = await ChildRequestToolsAsync();

        agentId.Should().NotBeNullOrEmpty();
        Json(tools.Single(t => t.Name == SkillTool)).Should().Be(Json(ParentSkill));
        _logger.WarningCount("Inherited tool override failed").Should().Be(1);
    }

    // Fails if the override is offered a tool the template did not inherit (it could then ADD a tool).
    [Fact]
    public async Task TemplateWithoutSkillOrSearchSkill_OverrideNeverOfferedThem_ChildHasNeither()
    {
        var offered = new ConcurrentQueue<string>();
        var manager = CreateManager(
            enabledTools: [ReadTool],
            (tool, _) =>
            {
                offered.Enqueue(tool.Contract.Name);
                // Would hand the child its own Skill or SearchSkill if it were ever offered one.
                return ValueTask.FromResult<IFunctionProvider?>(
                    tool.Contract.Name == ReadTool ? null : new OneToolProvider(tool.Contract, tool.Handler)
                );
            }
        );

        _ = await SpawnAsync(manager);
        var tools = await ChildRequestToolsAsync();

        offered.Should().Equal(ReadTool);
        tools.Select(t => t.Name).Should().Contain(ReadTool).And.NotContain([SkillTool, SearchTool]);
    }

    // Fails if a companion does not follow its key into an explicit tools list: a child narrowed to [Skill, Read]
    // would get a Skill without the SearchSkill that finds the skills its Skill no longer lists.
    [Fact]
    public async Task TemplateListsSkillNotSearchSkill_Companion_ChildGetsBoth_OverrideOfferedBoth()
    {
        var offered = new ConcurrentQueue<string>();
        var manager = CreateManager(
            enabledTools: [SkillTool, ReadTool],
            (tool, _) =>
            {
                offered.Enqueue(tool.Contract.Name);
                return ValueTask.FromResult<IFunctionProvider?>(null);
            },
            companions: new Dictionary<string, IReadOnlyList<string>> { [SkillTool] = [SearchTool] }
        );

        _ = await SpawnAsync(manager);
        var tools = await ChildRequestToolsAsync();

        tools.Select(t => t.Name).Should().Contain([SkillTool, SearchTool, ReadTool]);
        offered.Should().BeEquivalentTo([SkillTool, SearchTool, ReadTool]);
    }

    private SubAgentManager CreateManager(
        IReadOnlyList<string>? enabledTools,
        Func<InheritedTool, CancellationToken, ValueTask<IFunctionProvider?>> inheritedToolOverride,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? companions = null
    )
    {
        var template = new SubAgentTemplate
        {
            Name = "worker",
            SystemPrompt = "You are a worker.",
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
                        (messages, options, ct) =>
                        {
                            _ = _childTools.TrySetResult([.. options?.Functions ?? []]);
                            return Task.FromResult(BlockingStream(ct));
                        }
                    );
                return mock.Object;
            },
        };
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["worker"] = template },
            MaxConcurrentSubAgents = 5,
            InheritedToolOverride = inheritedToolOverride,
            CompanionToolNames = companions,
        };

        FunctionContract[] parentContracts = [ParentSkill, Contract(SearchTool), Contract(ReadTool)];
        var manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: parentContracts,
            parentHandlers: parentContracts.ToDictionary(c => c.Name, _ => OkHandler(), StringComparer.Ordinal),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates),
            logger: _logger
        );
        _managers.Add(manager);
        return manager;
    }

    private static async Task<string> SpawnAsync(SubAgentManager manager)
    {
        var receipt = await manager.SpawnAsync("worker", "work", runInBackground: true);
        using var doc = JsonDocument.Parse(receipt);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    /// <summary>The tools of the child's first model request (a bound, not a sleep: it normally arrives at once).</summary>
    private Task<IReadOnlyList<FunctionContract>> ChildRequestToolsAsync() =>
        _childTools.Task.WaitAsync(TimeSpan.FromSeconds(5));

    private static FunctionContract Contract(string name, string? description = null) =>
        new()
        {
            Name = name,
            Description = description ?? name,
            Parameters = [],
        };

    private static string Json(FunctionContract contract) => JsonSerializer.Serialize(contract);

    private static ToolHandler OkHandler() =>
        (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok"));

    private static async IAsyncEnumerable<IMessage> BlockingStream([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    /// <summary>A host's own version of one tool, served with the handler it was given.</summary>
    private sealed class OneToolProvider(FunctionContract contract, ToolHandler handler) : IFunctionProvider
    {
        public string ProviderName => "HostOwn";

        public int Priority => 100;

        public IEnumerable<FunctionDescriptor> GetFunctions() =>
            [
                new FunctionDescriptor
                {
                    Contract = contract,
                    Handler = handler,
                    ProviderName = ProviderName,
                },
            ];
    }
}
