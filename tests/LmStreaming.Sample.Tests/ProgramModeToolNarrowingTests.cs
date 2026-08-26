using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
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
    private static ModeCapabilities Caps(
        IReadOnlySet<string>? workflow,
        IReadOnlySet<string>? subAgents
    ) =>
        ModeCapabilities.LegacyDefaults with
        {
            WorkflowToolAllowList = workflow,
            SubAgentToolAllowList = subAgents,
        };

    private static IFunctionProvider WorkflowFamily() =>
        new WorkflowToolProvider(
            WorkflowRuntime.CreateNew(logger: NullLogger<WorkflowRuntime>.Instance)
        );

    private static SubAgentOptions Options() =>
        new() { Templates = new Dictionary<string, SubAgentTemplate>() };

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
            Caps(
                new HashSet<string> { WorkflowToolProvider.AllToolNames[0] },
                new HashSet<string> { "Agent" }
            )
        );

        NamesOf(scoped).Should().Equal(WorkflowToolProvider.AllToolNames[0]);
    }

    [Fact]
    public void ApplySubAgentToolNarrowing_LeavesAWildcardModesSurfaceAlone()
    {
        var options = Options();

        global::Program
            .ApplySubAgentToolNarrowing(options, Caps(null, null))
            .Should()
            .BeSameAs(options);
    }

    [Fact]
    public void ApplySubAgentToolNarrowing_KeepsNullWhenTheConversationHasNoDelegation()
    {
        global::Program
            .ApplySubAgentToolNarrowing(null, Caps(null, new HashSet<string> { "Agent" }))
            .Should()
            .BeNull();
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
}
