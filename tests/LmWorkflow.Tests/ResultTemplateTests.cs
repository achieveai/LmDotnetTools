using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Proves the terminal <c>resultTemplate</c> deep-render path of <see cref="WorkflowRuntime.AdvanceTo"/>:
///     advancing into a terminal with no explicit result composes the final result from state via the
///     template (type-preserving) and validates it against the final output schema; an explicit result still
///     wins; a composed result that violates the schema is refused.
/// </summary>
public class ResultTemplateTests
{
    private static WorkflowRuntime Loaded(bool schemaFail = false)
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(
            WorkflowJson.Deserialize(Phase4bFixtures.ResultTemplateWorkflow(schemaFail))
        );
        return runtime;
    }

    [Fact]
    public void NoExplicitResult_ComposesFromTemplate_AndValidates()
    {
        var runtime = Loaded();

        runtime.AdvanceTo("start", "done", result: null);

        runtime.IsComplete.Should().BeTrue();
        var result = runtime.Result!.AsObject();

        // The whole-binding leaves resolved to the actual object/array nodes from state.
        result["curriculum"]!["problemCount"]!.GetValue<int>().Should().Be(2);
        result["curriculum"]!["problems"]!.AsArray().Should().HaveCount(2);
        result["authored"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public void ExplicitResult_WinsOverTemplate()
    {
        var runtime = Loaded();

        var explicitResult = JsonNode.Parse(
            """{ "curriculum": { "problemCount": 99 }, "authored": [] }"""
        );

        runtime.AdvanceTo("start", "done", explicitResult);

        // The explicit result is captured verbatim (99), not the state-derived template (2).
        runtime.Result!["curriculum"]!["problemCount"]!.GetValue<int>().Should().Be(99);
    }

    [Fact]
    public void ComposedResult_FailingSchema_IsRefused()
    {
        var runtime = Loaded(schemaFail: true);

        // The template never produces the required 'missing' field, so validation fails.
        var act = () => runtime.AdvanceTo("start", "done", result: null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*schema validation*");
        runtime.IsComplete.Should().BeFalse();
    }
}
