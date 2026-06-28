using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Focused, loop-free tests of <see cref="WorkflowRuntime"/>: they exercise compose / observe / advance
///     directly so the architectural core is proven deterministically without the controller loop.
/// </summary>
public class WorkflowRuntimeTests
{
    private const string AnalyzeUnit = "analyze:1:task";

    private static WorkflowRuntime LoadedRuntime()
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Phase3Fixtures.LinearBlockingAgent));
        return runtime;
    }

    private static WorkflowRuntime RuntimeAtAnalyze()
    {
        var runtime = LoadedRuntime();
        runtime.AdvanceTo("start", "analyze", null);
        return runtime;
    }

    [Fact]
    public void LoadDefinition_SeedsChannelsAndPositionsAtStart()
    {
        var runtime = LoadedRuntime();

        runtime.CurrentNodeId.Should().Be("start");
        runtime.Step.Should().Be(0);
        runtime.IsComplete.Should().BeFalse();
        runtime.Visits.Should().Contain(new KeyValuePair<string, int>("start", 1));

        // Inputs are cloned from the definition.
        runtime.Inputs["topic"]!.GetValue<string>().Should().Be("widgets");

        // Every node gets an empty outputs bag.
        runtime.Outputs.Should().ContainKeys("start", "analyze", "done");
        runtime.Outputs["analyze"].Should().BeOfType<JsonObject>().Which.Should().BeEmpty();
    }

    [Fact]
    public void ComposeNextExpectedAction_StartNode_ReturnsEmpty()
    {
        var runtime = LoadedRuntime();

        runtime.ComposeNextExpectedAction().Should().BeEmpty();
    }

    [Fact]
    public void ComposeNextExpectedAction_ProceduralNode_ComposesPromptWithSharedContextBindingAndSchemaDirective()
    {
        var runtime = RuntimeAtAnalyze();

        var units = runtime.ComposeNextExpectedAction();

        var unit = units.Should().ContainSingle().Which;
        unit.Name.Should().Be(AnalyzeUnit);
        unit.SubagentType.Should().Be("general-purpose");
        unit.OutputSchema.Should().NotBeNull();

        // Shared context is prepended, the {{inputs.topic}} binding is substituted, and the schema-return
        // directive is appended — while the node's controllerInstructions never appear.
        unit.Prompt.Should().Contain(Phase3Fixtures.SharedContextMarker);
        unit.Prompt.Should().Contain("Analyze the topic widgets.");
        unit.Prompt.Should().Contain(Phase3Fixtures.SchemaDirective);
        unit.Prompt.Should().NotContain(Phase3Fixtures.ControllerInstructionsMarker);

        // Composition order: shared context first, schema directive last.
        unit.Prompt.IndexOf(Phase3Fixtures.SharedContextMarker, StringComparison.Ordinal)
            .Should()
            .BeLessThan(unit.Prompt.IndexOf(Phase3Fixtures.SchemaDirective, StringComparison.Ordinal));
    }

    [Fact]
    public void ObserveResult_ValidOutput_RecordsOutputAppliesWriteAndMarksValidated()
    {
        var runtime = RuntimeAtAnalyze();
        _ = runtime.ComposeNextExpectedAction();
        runtime.RegisterSpawn("tc_agent", AnalyzeUnit);

        runtime.ObserveResult("tc_agent", """{ "summary": "all good" }""", isError: false);

        runtime.Outputs["analyze"]!["task"]!["summary"]!.GetValue<string>().Should().Be("all good");
        runtime.State["analysis"]!["summary"]!.GetValue<string>().Should().Be("all good");
        StatusOf(runtime, AnalyzeUnit).Should().Be("validated");
    }

    [Fact]
    public void ObserveResult_SchemaInvalidOutput_RecordsErrorMarkerAndMarksFailed()
    {
        var runtime = RuntimeAtAnalyze();
        _ = runtime.ComposeNextExpectedAction();
        runtime.RegisterSpawn("tc_agent", AnalyzeUnit);

        // Missing the required "summary" property.
        runtime.ObserveResult("tc_agent", """{ "other": 1 }""", isError: false);

        runtime.Outputs["analyze"]!["task"]!["_error"].Should().NotBeNull();
        runtime.State.Should().NotContainKey("analysis");
        StatusOf(runtime, AnalyzeUnit).Should().Be("failed");
    }

    [Fact]
    public void ObserveResult_ErrorResult_RecordsErrorMarkerAndMarksFailed()
    {
        var runtime = RuntimeAtAnalyze();
        _ = runtime.ComposeNextExpectedAction();
        runtime.RegisterSpawn("tc_agent", AnalyzeUnit);

        runtime.ObserveResult("tc_agent", "the sub-agent blew up", isError: true);

        runtime.Outputs["analyze"]!["task"]!["_error"].Should().NotBeNull();
        StatusOf(runtime, AnalyzeUnit).Should().Be("failed");
    }

    [Fact]
    public void ObserveResult_UnknownToolCallId_IsSurfacedAsUnmatched()
    {
        var runtime = RuntimeAtAnalyze();
        _ = runtime.ComposeNextExpectedAction();

        runtime.ObserveResult("never_registered", """{ "summary": "x" }""", isError: false);

        var projection = runtime.GetProjection(null);
        projection["unmatched"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should()
            .Contain("never_registered");
    }

    [Fact]
    public void AdvanceTo_UndeclaredTransition_Throws()
    {
        var runtime = LoadedRuntime();

        // start only declares an edge to "analyze", not directly to "done".
        var act = () => runtime.AdvanceTo("start", "done", null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Undeclared transition*");
    }

    [Fact]
    public void AdvanceTo_IntoTerminalWithValidResult_CompletesAndCapturesResult()
    {
        var runtime = RuntimeAtAnalyze();

        runtime.AdvanceTo("analyze", "done", JsonNode.Parse("""{ "summary": "final" }"""));

        runtime.IsComplete.Should().BeTrue();
        runtime.CurrentNodeId.Should().Be("done");
        runtime.Result!["summary"]!.GetValue<string>().Should().Be("final");
        runtime.Visits.Should().Contain(new KeyValuePair<string, int>("done", 1));
        runtime.Step.Should().Be(2);
    }

    [Fact]
    public void AdvanceTo_IntoTerminalWithSchemaInvalidResult_Throws()
    {
        var runtime = RuntimeAtAnalyze();

        // Missing the required "summary" property in the terminal's finalOutputSchema.
        var act = () => runtime.AdvanceTo("analyze", "done", JsonNode.Parse("""{ "x": 1 }"""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*schema validation*");
        runtime.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void PublicChannelGetters_ReturnIsolatedCopies_MutatingThemDoesNotLeakIntoRuntime()
    {
        var runtime = RuntimeAtAnalyze();
        _ = runtime.ComposeNextExpectedAction();
        runtime.RegisterSpawn("tc_agent", AnalyzeUnit);
        runtime.ObserveResult("tc_agent", """{ "summary": "v1" }""", isError: false);

        // Fix H1: the getters hand back DEEP COPIES — two reads are not the same instance, and mutating a
        // returned copy must NOT change runtime state.
        ReferenceEquals(runtime.Outputs, runtime.Outputs).Should().BeFalse();

        var outputs = runtime.Outputs;
        outputs["analyze"]!["task"]!["summary"] = JsonValue.Create("HACKED");
        var state = runtime.State;
        state["analysis"]!["summary"] = JsonValue.Create("HACKED");

        runtime.Outputs["analyze"]!["task"]!["summary"]!.GetValue<string>().Should().Be("v1");
        runtime.State["analysis"]!["summary"]!.GetValue<string>().Should().Be("v1");
    }

    [Fact]
    public void PublicChannelGetter_ReturnsStableCopy_UnaffectedByLaterRuntimeMutation()
    {
        var runtime = RuntimeAtAnalyze();
        _ = runtime.ComposeNextExpectedAction();
        runtime.RegisterSpawn("tc_agent", AnalyzeUnit);
        runtime.ObserveResult("tc_agent", """{ "summary": "v1" }""", isError: false);

        // Capture a copy, then mutate the live state channel under the lock.
        var captured = runtime.State;
        runtime.SetState("state.analysis.summary", JsonValue.Create("v2"), "set");

        // Fix H1: the previously-returned copy is STABLE (the in-place mutation cannot race into it),
        // while a fresh read observes the update.
        captured["analysis"]!["summary"]!.GetValue<string>().Should().Be("v1");
        runtime.State["analysis"]!["summary"]!.GetValue<string>().Should().Be("v2");
    }

    [Fact]
    public void BuildContext_ChannelsAreClonedCopies_NotLiveAliases()
    {
        var runtime = RuntimeAtAnalyze();
        _ = runtime.ComposeNextExpectedAction();
        runtime.RegisterSpawn("tc_agent", AnalyzeUnit);
        runtime.ObserveResult("tc_agent", """{ "summary": "v1" }""", isError: false);

        // Fix H1: the PUBLIC BuildContext clones the channels, so mutating the returned context's state does
        // not leak back into the runtime.
        var context = runtime.BuildContext();
        context.State["analysis"]!["summary"] = JsonValue.Create("HACKED");

        runtime.State["analysis"]!["summary"]!.GetValue<string>().Should().Be("v1");
    }

    [Fact]
    public void MergeInputs_SeedsHostInputs_AndBindsInComposedPrompt()
    {
        var runtime = LoadedRuntime();

        // Host-supplied inputs override the definition seed and become visible to {{inputs.*}} bindings.
        runtime.MergeInputs(new JsonObject { ["topic"] = "custom-topic" });
        runtime.Inputs["topic"]!.GetValue<string>().Should().Be("custom-topic");

        runtime.AdvanceTo("start", "analyze", null);
        var unit = runtime.ComposeNextExpectedAction().Should().ContainSingle().Subject;
        unit.Prompt.Should().Contain("Analyze the topic custom-topic.");
    }

    private static string StatusOf(WorkflowRuntime runtime, string unitName) =>
        runtime.GetProjection(null)["tasks"]![unitName]!.GetValue<string>();
}
