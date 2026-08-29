using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using AchieveAi.LmDotnetTools.Misc.Utils;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests;

/// <summary>
/// Pins that the host actually hands each mode's allow-list to the thing that enforces it.
/// </summary>
/// <remarks>
/// The enforcement primitives have their own tests, but those prove nothing about the wiring: a
/// composition root that passes <c>null</c> — or the wrong one of two same-typed lists — produces a
/// conversation with the full surface and no test notices. Both narrowings therefore go through a
/// named helper so the choice of list is a claim something can assert.
/// </remarks>
public sealed class ProgramModeToolNarrowingTests
{
    /// <summary>Capabilities whose two allow-lists DIFFER, so confusing them cannot go unnoticed.</summary>
    private static ModeCapabilities Caps(IReadOnlySet<string>? workflow, IReadOnlySet<string>? subAgents) =>
        ModeCapabilities.LegacyDefaults with
        {
            WorkflowToolAllowList = workflow,
            SubAgentToolAllowList = subAgents,
        };

    private static IFunctionProvider WorkflowFamily() =>
        new WorkflowToolProvider(WorkflowRuntime.CreateNew(logger: NullLogger<WorkflowRuntime>.Instance));

    private static SubAgentOptions Options() => new() { Templates = new Dictionary<string, SubAgentTemplate>() };

    private static string[] NamesOf(IFunctionProvider provider) =>
        [.. provider.GetFunctions().Select(f => f.Contract.Name)];

    [Fact]
    public void ScopeWorkflowProvider_LeavesTheFamilyIntactForAWildcardMode()
    {
        var inner = WorkflowFamily();

        var scoped = global::Program.ScopeWorkflowProvider(inner, Caps(null, null));

        scoped.Should().BeSameAs(inner);
    }

    [Fact]
    public void ScopeWorkflowProvider_EmitsOnlyTheSelectedNames()
    {
        var scoped = global::Program.ScopeWorkflowProvider(
            WorkflowFamily(),
            Caps(new HashSet<string> { WorkflowToolProvider.AllToolNames[0] }, null)
        );

        NamesOf(scoped).Should().Equal(WorkflowToolProvider.AllToolNames[0]);
    }

    [Fact]
    public void ScopeWorkflowProvider_IsNotVacuous_TheUnscopedFamilyIsLarger()
    {
        // Without this, the assertion above passes against a provider that only ever emitted one tool.
        NamesOf(WorkflowFamily()).Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void ScopeWorkflowProvider_ReadsTheWorkflowListRatherThanTheSubAgentOne()
    {
        // Two allow-lists of the same type sit on ModeCapabilities. Picking the wrong one would grant
        // a mode nothing at all here while silently widening delegation elsewhere.
        var scoped = global::Program.ScopeWorkflowProvider(
            WorkflowFamily(),
            Caps(new HashSet<string> { WorkflowToolProvider.AllToolNames[0] }, new HashSet<string> { "Agent" })
        );

        NamesOf(scoped).Should().Equal(WorkflowToolProvider.AllToolNames[0]);
    }

    [Fact]
    public void ApplySubAgentToolNarrowing_LeavesAWildcardModesSurfaceAlone()
    {
        var options = Options();

        global::Program.ApplySubAgentToolNarrowing(options, Caps(null, null)).Should().BeSameAs(options);
    }

    [Fact]
    public void ApplySubAgentToolNarrowing_KeepsNullWhenTheConversationHasNoDelegation()
    {
        global::Program.ApplySubAgentToolNarrowing(null, Caps(null, new HashSet<string> { "Agent" })).Should().BeNull();
    }

    [Fact]
    public void ApplySubAgentToolNarrowing_RecordsTheSelectedNames()
    {
        var narrowed = global::Program.ApplySubAgentToolNarrowing(
            Options(),
            Caps(null, new HashSet<string> { "Agent", "WaitAgent" })
        );

        narrowed!.ExposedToolNames.Should().BeEquivalentTo(["Agent", "WaitAgent"]);
    }

    [Fact]
    public void ApplySubAgentToolNarrowing_ReadsTheSubAgentListRatherThanTheWorkflowOne()
    {
        var narrowed = global::Program.ApplySubAgentToolNarrowing(
            Options(),
            Caps(new HashSet<string> { "StartWorkflowAgent" }, new HashSet<string> { "Agent" })
        );

        narrowed!.ExposedToolNames.Should().BeEquivalentTo(["Agent"]);
    }

    [Fact]
    public void ApplySubAgentToolNarrowing_DoesNotTouchWhatTheCHILDRENInherit()
    {
        // ExposedToolNames governs the parent's own surface; NonInheritedToolNames governs the
        // children's. Writing the mode's selection into the wrong one would strip a delegate's tools.
        var options = Options() with
        {
            NonInheritedToolNames = new HashSet<string> { "Agent" },
        };

        var narrowed = global::Program.ApplySubAgentToolNarrowing(
            options,
            Caps(null, new HashSet<string> { "SendMessage" })
        );

        narrowed!.NonInheritedToolNames.Should().BeEquivalentTo(["Agent"]);
        narrowed.ExposedToolNames.Should().BeEquivalentTo(["SendMessage"]);
    }

    /// <summary>
    ///     The task family enumerated from a live <see cref="TaskManager" />, the same way the runtime
    ///     wiring and the Modes editor's catalog enumerate it — never a hand-written name list, so a
    ///     newly added task tool makes the pins below fail instead of silently skipping a mode.
    /// </summary>
    private static IReadOnlyList<string> TaskToolNames()
    {
        var registry = new FunctionRegistry();
        _ = registry.AddFunctionsFromObject(new TaskManager(), providerName: "TaskManager");
        var (contracts, _) = registry.Build();
        return [.. contracts.Select(c => c.Name)];
    }

    /// <summary>A conversation registry shaped like Program's: sample demo tools plus the task family.</summary>
    private static FunctionRegistry ConversationRegistry()
    {
        var registry = new FunctionRegistry();
        _ = registry.AddFunction(
            new FunctionContract
            {
                Name = "get_weather",
                Description = "Stand-in for the sample demo tools the workspace agent must NOT get.",
                Parameters = [],
            },
            (_, _, _) =>
                Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Resolved(new ToolHandlerResultPayload("{}"))),
            "SampleTools"
        );
        _ = registry.AddFunctionsFromObject(new TaskManager(), providerName: "TaskManager");
        return registry;
    }

    private static string[] RegisteredNames(FunctionRegistry registry)
    {
        var (contracts, _) = registry.Build();
        return [.. contracts.Select(c => c.Name)];
    }

    [Fact]
    public void TaskToolEnumeration_IsNotVacuous()
    {
        // The board ships fifteen tools today; anything shrinking the enumeration to a handful would
        // quietly weaken every containment assertion below.
        TaskToolNames().Should().HaveCountGreaterThanOrEqualTo(15).And.Contain(["add-task", "claim-task"]);
    }

    /// <summary>
    ///     The todo board (#583) is the coordination surface for the multi-agent Workspace Agent mode,
    ///     so its Prompts.yaml selection must name every task tool. This is the D5 fix's anchor: with
    ///     <c>enabledTools: []</c>, <c>Program.BuildModeFilteredRegistry</c> starves workspace-agent
    ///     turns of the whole family.
    /// </summary>
    [Fact]
    public void WorkspaceAgentMode_EnablesEveryTaskManagerTool()
    {
        var mode = SystemChatModes.GetById(SystemChatModes.WorkspaceAgentModeId);

        // Set equality, not Contain: this pins BOTH halves of the contract — every task tool is
        // enabled, and nothing but task tools is (additive drift, e.g. a sample tool name slipping
        // into the YAML list, goes red too).
        mode!.EnabledTools.Should().BeEquivalentTo(TaskToolNames());
    }

    [Fact]
    public void BuildModeFilteredRegistry_WorkspaceAgent_RegistersTheTaskFamilyAndNoSampleTools()
    {
        var mode = SystemChatModes.GetById(SystemChatModes.WorkspaceAgentModeId);

        var filtered = global::Program.BuildModeFilteredRegistry(ConversationRegistry(), mode!.EnabledTools);

        var names = RegisteredNames(filtered);
        names.Should().Contain(TaskToolNames());
        // Function-tool curation still holds: the sandbox gateway owns file/shell tools, and the
        // sample demo tools stay out of this mode. Only the task family joins.
        names.Should().NotContain("get_weather");
    }

    [Fact]
    public void BuildModeFilteredRegistry_NullEnabledTools_KeepsEverything()
    {
        // The Default mode records no EnabledTools list; null means "all tools" and must keep meaning
        // that, or the fix for workspace-agent would regress every legacy mode.
        SystemChatModes.GetById(SystemChatModes.DefaultModeId)!.EnabledTools.Should().BeNull();

        var registry = ConversationRegistry();
        RegisteredNames(global::Program.BuildModeFilteredRegistry(registry, null))
            .Should()
            .BeEquivalentTo(RegisteredNames(registry));
    }

    /// <summary>
    ///     The sub-agent half of D5: children inherit the parent loop's registry minus
    ///     <see cref="SubAgentOptions.NonInheritedToolNames" />. The workflow families are excluded on
    ///     purpose; the task tools must never be, because a child's task tools close over the parent
    ///     conversation's one <see cref="TaskManager" /> — that shared closure IS the shared board.
    /// </summary>
    [Fact]
    public void AddWorkflowNonInheritedTools_ExcludesWorkflowFamiliesButLeavesTaskToolsInheritable()
    {
        var excluded = global::Program
            .AddWorkflowNonInheritedTools(Options() with { NonInheritedToolNames = new HashSet<string> { "Agent" } })
            .NonInheritedToolNames;

        excluded.Should().Contain("Agent");
        excluded.Should().Contain(StartWorkflowToolProvider.ToolNames);
        excluded.Should().Contain(WorkflowToolProvider.AllToolNames);
        excluded.Should().NotIntersectWith(TaskToolNames());
    }
}
