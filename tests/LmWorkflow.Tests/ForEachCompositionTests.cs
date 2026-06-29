using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Loop-free proof of forEach fan-out: a single authored task with <c>forEach</c> over a 3-element array
///     composes into three indexed units whose prompts carry the right <c>item</c>/<c>index</c>/<c>count</c>
///     bindings, and whose validated results land at the correct index in an output array — even when the
///     results arrive out of order.
/// </summary>
public class ForEachCompositionTests
{
    private static WorkflowRuntime RuntimeAtFan(string joinMode = "all")
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Phase4Fixtures.ForEachWorkflow(joinMode)));
        runtime.AdvanceTo("start", "fan", null);
        return runtime;
    }

    [Fact]
    public void Compose_ForEachOverThreeElements_ProducesThreeIndexedUnits()
    {
        var runtime = RuntimeAtFan();

        var units = runtime.ComposeNextExpectedAction();

        units.Select(u => u.Name)
            .Should()
            .Equal("fan:1:task:0", "fan:1:task:1", "fan:1:task:2");
        units.Should().OnlyContain(u => u.SubagentType == "general-purpose");
    }

    [Fact]
    public void Compose_ForEachUnitPrompts_CarryItemIndexAndCountBindings()
    {
        var runtime = RuntimeAtFan();

        var units = runtime.ComposeNextExpectedAction();

        units[0].Prompt.Should().Contain(Phase4Fixtures.SharedContextMarker);
        units[0].Prompt.Should().Contain("Process alpha at 0 of 3.");
        units[1].Prompt.Should().Contain("Process beta at 1 of 3.");
        units[2].Prompt.Should().Contain("Process gamma at 2 of 3.");
    }

    [Fact]
    public void ObserveResult_ForEachOutputs_RecordAsIndexedArray()
    {
        var runtime = RuntimeAtFan();
        _ = runtime.ComposeNextExpectedAction();

        for (var i = 0; i < 3; i++)
        {
            runtime.RegisterSpawn($"tc_{i}", $"fan:1:task:{i}");
            runtime.ObserveResult($"tc_{i}", $$"""{ "text": "result-{{i}}" }""", isError: false);
        }

        var array = runtime.Outputs["fan"]!["task"]!.AsArray();
        array.Should().HaveCount(3);
        array[0]!["text"]!.GetValue<string>().Should().Be("result-0");
        array[1]!["text"]!.GetValue<string>().Should().Be("result-1");
        array[2]!["text"]!.GetValue<string>().Should().Be("result-2");
    }

    [Fact]
    public void ObserveResult_OutOfOrderCompletion_LandsAtCorrectIndex()
    {
        var runtime = RuntimeAtFan();
        _ = runtime.ComposeNextExpectedAction();

        // Complete the units out of order: index 2 first, then 0, then 1.
        foreach (var i in new[] { 2, 0, 1 })
        {
            runtime.RegisterSpawn($"tc_{i}", $"fan:1:task:{i}");
            runtime.ObserveResult($"tc_{i}", $$"""{ "text": "v{{i}}" }""", isError: false);
        }

        var array = runtime.Outputs["fan"]!["task"]!.AsArray();
        array.Should().HaveCount(3);
        array[0]!["text"]!.GetValue<string>().Should().Be("v0");
        array[1]!["text"]!.GetValue<string>().Should().Be("v1");
        array[2]!["text"]!.GetValue<string>().Should().Be("v2");

        // Each element's text was appended to state.results (one append per validated unit).
        runtime.State["results"]!.AsArray().Should().HaveCount(3);
    }

    [Fact]
    public void RegisterSpawn_AfterUnitValidated_IsNoOp_DoesNotReapplyAppendOrChangeOutput()
    {
        var runtime = RuntimeAtFan();
        _ = runtime.ComposeNextExpectedAction();

        // Validate unit 0: its text is appended to state.results and recorded at outputs[fan][task][0].
        runtime.RegisterSpawn("tc_0", "fan:1:task:0");
        runtime.ObserveResult("tc_0", """{ "text": "first" }""", isError: false);

        runtime.State["results"]!.AsArray().Should().HaveCount(1);
        runtime.Outputs["fan"]!["task"]![0]!["text"]!.GetValue<string>().Should().Be("first");

        // Re-issuing an Agent call for the already-validated unit must NOT reset its status, re-map the
        // toolCallId, or re-run validation — otherwise the append write would be applied a second time
        // (silent state corruption). The second result is therefore surfaced as unmatched and changes nothing.
        runtime.RegisterSpawn("tc_0_again", "fan:1:task:0");
        runtime.ObserveResult("tc_0_again", """{ "text": "second" }""", isError: false);

        // The append target did NOT grow and the recorded output is unchanged.
        runtime.State["results"]!.AsArray().Should().HaveCount(1);
        runtime.Outputs["fan"]!["task"]![0]!["text"]!.GetValue<string>().Should().Be("first");

        // The status stays validated, and the second toolCallId is surfaced as unmatched.
        var projection = runtime.GetProjection(null);
        projection["tasks"]!["fan:1:task:0"]!.GetValue<string>().Should().Be("validated");
        projection["unmatched"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .Should()
            .Contain("tc_0_again");
    }
}
