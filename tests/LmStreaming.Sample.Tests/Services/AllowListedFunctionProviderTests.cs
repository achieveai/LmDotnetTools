using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The editor offers a checkbox per workflow tool, so the runtime has to grant per tool.
/// </summary>
/// <remarks>
/// <c>WorkflowToolProvider</c> hands over its whole seven-tool family at once. Registered raw, a
/// mode that ticked one authoring tool received all seven — a runtime surface strictly larger than
/// what the user chose, and invisible in the editor that offered the choice.
/// </remarks>
public class AllowListedFunctionProviderTests
{
    private static WorkflowToolProvider CreateWorkflowProvider() =>
        new(WorkflowRuntime.CreateNew(logger: NullLogger<WorkflowRuntime>.Instance));

    private static IReadOnlyList<string> NamesFrom(IFunctionProvider provider) =>
        [.. provider.GetFunctions().Select(f => f.Contract.Name)];

    [Fact]
    public void NullAllowList_ReturnsTheProviderItself()
    {
        // Not merely "same names": an unnarrowed mode must not even pay for a wrapper, and the
        // identity is what lets every call site wire `Wrap(...)` unconditionally.
        var inner = CreateWorkflowProvider();

        AllowListedFunctionProvider.Wrap(inner, null).Should().BeSameAs(inner);
    }

    [Fact]
    public void SingleName_GrantsExactlyThatTool()
    {
        var wrapped = AllowListedFunctionProvider.Wrap(
            CreateWorkflowProvider(),
            new HashSet<string>(StringComparer.Ordinal) { WorkflowToolProvider.AddNodeToolName }
        );

        NamesFrom(wrapped).Should().ContainSingle().Which.Should().Be(WorkflowToolProvider.AddNodeToolName);
    }

    [Fact]
    public void UnnarrowedProvider_StillEmitsTheWholeFamily()
    {
        // Non-vacuity: the assertion above only means something if the family really is larger.
        NamesFrom(CreateWorkflowProvider()).Should().BeEquivalentTo(WorkflowToolProvider.AllToolNames);
    }

    [Fact]
    public void EmptyAllowList_GrantsNothing()
    {
        var wrapped = AllowListedFunctionProvider.Wrap(
            CreateWorkflowProvider(),
            new HashSet<string>(StringComparer.Ordinal)
        );

        NamesFrom(wrapped).Should().BeEmpty();
    }

    [Fact]
    public void UnknownName_CannotConjureATool()
    {
        var wrapped = AllowListedFunctionProvider.Wrap(
            CreateWorkflowProvider(),
            new HashSet<string>(StringComparer.Ordinal) { "NoSuchTool" }
        );

        NamesFrom(wrapped).Should().BeEmpty();
    }

    [Fact]
    public void ProviderIdentity_IsPreservedSoConflictResolutionIsUnchanged()
    {
        var inner = CreateWorkflowProvider();
        var wrapped = AllowListedFunctionProvider.Wrap(
            inner,
            new HashSet<string>(StringComparer.Ordinal) { WorkflowToolProvider.AddNodeToolName }
        );

        wrapped.ProviderName.Should().Be(inner.ProviderName);
        wrapped.Priority.Should().Be(inner.Priority);
    }
}
