using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Ingest;
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

    /// <summary>A variant of <see cref="LoadedRuntime"/> with a definition-level <c>onBudgetExhausted</c>
    /// escape node ("escape") that is reachable ONLY via that field, not via any node's "next" edges.</summary>
    private static WorkflowRuntime LoadedRuntimeWithBudgetEscape()
    {
        var runtime = new WorkflowRuntime();
        var baseDefinition = WorkflowJson.Deserialize(Phase3Fixtures.LinearBlockingAgent);
        var withEscape = baseDefinition with
        {
            Nodes = [.. baseDefinition.Nodes, new TerminalNode { Id = "escape", Title = "Escape" }],
            OnBudgetExhausted = "escape",
        };
        runtime.LoadDefinition(withEscape);
        return runtime;
    }

    /// <summary>A variant of <see cref="LoadedRuntime"/> where "analyze" transitions into a
    /// <see cref="ConditionalNode"/> ("gate") that falls back to "done", so a previously-existing
    /// conditional node is available as an <c>AddNode</c> splice target.</summary>
    private static WorkflowRuntime LoadedRuntimeWithConditionalNode()
    {
        var runtime = new WorkflowRuntime();
        var baseDefinition = WorkflowJson.Deserialize(Phase3Fixtures.LinearBlockingAgent);
        var analyze = (ProceduralNode)baseDefinition.Nodes.Single(n => n.Id == "analyze");
        var rewiredAnalyze = analyze with { Next = ["gate"] };
        var gate = new ConditionalNode
        {
            Id = "gate",
            Title = "Gate",
            Branches = [],
            Else = "done",
        };
        var withGate = baseDefinition with
        {
            Nodes = [.. baseDefinition.Nodes.Where(n => n.Id != "analyze"), rewiredAnalyze, gate],
        };
        runtime.LoadDefinition(withGate);
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

        // The getters hand back DEEP COPIES — two reads are not the same instance, and mutating a
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

        // The previously-returned copy is STABLE (the in-place mutation cannot race into it),
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

        // The PUBLIC BuildContext clones the channels, so mutating the returned context's state does
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

    [Fact]
    public void FindNode_ResolvesEveryNodeId_ReflectedInProjectionNodeType()
    {
        // The O(1) node index must resolve each declared id to its correct node: the projection surfaces the
        // procedural-only "join" view exactly when the active node is the procedural one, proving the lookup
        // returns the right typed node for start/procedural/terminal ids alike.
        var runtime = LoadedRuntime();

        // start -> StartNode: no procedural join surface.
        runtime.GetProjection(null).ContainsKey("join").Should().BeFalse();

        // analyze -> ProceduralNode: the join surface is present.
        runtime.AdvanceTo("start", "analyze", null);
        runtime.GetProjection(null).Should().ContainKey("join");

        // done -> TerminalNode: completes, no join surface.
        runtime.AdvanceTo("analyze", "done", JsonNode.Parse("""{ "summary": "x" }"""));
        runtime.GetProjection(null).ContainsKey("join").Should().BeFalse();
        runtime.IsComplete.Should().BeTrue();

        // An undeclared target resolves to no node and is refused (unknown-id path).
        var advanceUnknown = () => runtime.AdvanceTo("done", "ghost", null);
        advanceUnknown.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ObserveResult_UnmatchedDiagnostics_AreCappedAt50_KeepingMostRecent()
    {
        var runtime = RuntimeAtAnalyze();
        _ = runtime.ComposeNextExpectedAction();

        // 60 uncorrelated tool-call ids arrive; only the most recent 50 are retained.
        for (var i = 0; i < 60; i++)
        {
            runtime.ObserveResult($"unmatched_{i}", """{ "summary": "x" }""", isError: false);
        }

        var unmatched = runtime
            .GetProjection(null)["unmatched"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .ToList();

        unmatched.Should().HaveCount(50);
        unmatched.Should().Contain("unmatched_59"); // most recent retained
        unmatched.Should().Contain("unmatched_10"); // oldest retained (boundary)
        unmatched.Should().NotContain("unmatched_9"); // dropped
        unmatched.Should().NotContain("unmatched_0"); // dropped
    }

    [Fact]
    public void AddNode_ViaPreviousNodeIdOnly_AppendsToPreviousNextAndSeedsOutputs()
    {
        var runtime = LoadedRuntime();

        runtime.AddNode(new TerminalNode { Id = "extra", Title = "Extra" }, previousNodeId: "analyze", nextNodeId: null);

        var analyze = runtime.Definition!.Nodes.Single(n => n.Id == "analyze");
        analyze.Should().BeOfType<ProceduralNode>().Which.Next.Should().Contain("extra");
        runtime.Definition!.Nodes.Should().Contain(n => n.Id == "extra");
        runtime.Outputs.Should().ContainKey("extra");
        runtime.Outputs["extra"].Should().BeOfType<JsonObject>().Which.Should().BeEmpty();
    }

    [Fact]
    public void AddNode_ViaNextNodeIdOnly_ThrowsForUnreachableNewNode()
    {
        var runtime = LoadedRuntime();

        // "nextNodeId" alone only wires an OUTGOING edge from the new node; with no "previousNodeId" it
        // gets no incoming edge, so it can never be reachable from "start".
        var act = () => runtime.AddNode(
            new ProceduralNode { Id = "spare", Title = "Spare", Next = [] },
            previousNodeId: null,
            nextNodeId: "done");

        act.Should().Throw<WorkflowValidationException>().WithMessage("*unreachable*");
        runtime.Definition!.Nodes.Should().NotContain(n => n.Id == "spare");
    }

    [Fact]
    public void AddNode_ViaBothPreviousAndNextNodeId_WiresIncomingAndOutgoingEdges()
    {
        var runtime = LoadedRuntime();

        runtime.AddNode(
            new ProceduralNode { Id = "extra", Title = "Extra", Next = [] },
            previousNodeId: "analyze",
            nextNodeId: "done");

        var analyze = runtime.Definition!.Nodes.Single(n => n.Id == "analyze");
        analyze.Should().BeOfType<ProceduralNode>().Which.Next.Should().Contain("extra");
        var extra = runtime.Definition!.Nodes.Single(n => n.Id == "extra");
        extra.Should().BeOfType<ProceduralNode>().Which.Next.Should().Contain("done");
    }

    [Fact]
    public void AddNode_BothPreviousAndNextOmitted_ThrowsInvalidOperationException()
    {
        var runtime = LoadedRuntime();

        var act = () =>
            runtime.AddNode(new TerminalNode { Id = "extra", Title = "Extra" }, previousNodeId: null, nextNodeId: null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*At least one of*");
        runtime.Definition!.Nodes.Should().NotContain(n => n.Id == "extra");
    }

    [Fact]
    public void AddNode_DuplicateId_ThrowsInvalidOperationException()
    {
        var runtime = LoadedRuntime();

        var act = () =>
            runtime.AddNode(new TerminalNode { Id = "analyze", Title = "Duplicate" }, previousNodeId: "start", nextNodeId: null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*already exists*");
    }

    [Fact]
    public void AddNode_PreviousIsConditionalNode_ThrowsInvalidOperationException()
    {
        var runtime = LoadedRuntimeWithConditionalNode();

        var act = () =>
            runtime.AddNode(new TerminalNode { Id = "extra", Title = "Extra" }, previousNodeId: "gate", nextNodeId: null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SetWorkflow*");
        runtime.Definition!.Nodes.Should().NotContain(n => n.Id == "extra");
    }

    [Fact]
    public void RemoveNode_CurrentNode_ThrowsInvalidOperationException()
    {
        var runtime = RuntimeAtAnalyze();

        var act = () => runtime.RemoveNode("analyze");

        act.Should().Throw<InvalidOperationException>().WithMessage("*currently positioned*");
        runtime.Definition!.Nodes.Should().Contain(n => n.Id == "analyze");
    }

    [Fact]
    public void RemoveNode_ProceduralNode_NeutersToNoOpPassThrough_PreservingIdAndNext()
    {
        var runtime = LoadedRuntime(); // start → analyze(proc, one task, next=[done]) → done; current = start.

        runtime.RemoveNode("analyze");

        // The node is NOT deleted — it stays in the graph, keeps its id and inbound edge (start → analyze),
        // but loses its task list and just advances along its existing "next".
        var analyze = runtime.Definition!.Nodes.Single(n => n.Id == "analyze");
        var proc = analyze.Should().BeOfType<ProceduralNode>().Which;
        proc.TaskList.Should().BeEmpty();
        proc.Next.Should().ContainSingle().Which.Should().Be("done");
    }

    [Fact]
    public void RemoveNode_TerminalNode_ThrowsInvalidOperationException()
    {
        // "escape" is a terminal node reachable only via the definition-level onBudgetExhausted edge.
        var runtime = LoadedRuntimeWithBudgetEscape();

        var act = () => runtime.RemoveNode("escape");

        act.Should().Throw<InvalidOperationException>().WithMessage("*terminal*");
        runtime.Definition!.Nodes.Single(n => n.Id == "escape").Should().BeOfType<TerminalNode>();
    }

    [Fact]
    public void RemoveNode_ConditionalNode_CollapsesToFirstBranch_DefaultsTrue()
    {
        // start → gate(conditional: branch → keep, else → other) → keep(proc, next=[other]) → other → done.
        // "other" is reachable via the kept branch's own chain, so collapsing gate to its FIRST branch
        // ("keep") and dropping the else does NOT orphan it.
        const string json = """
            {
              "schemaVersion": 1,
              "objective": "conditional collapse",
              "nodes": [
                { "id": "start", "type": "start", "title": "Start", "next": ["gate"] },
                {
                  "id": "gate",
                  "type": "conditional",
                  "title": "Gate",
                  "branches": [ { "when": "keep it", "to": "keep" } ],
                  "else": "other"
                },
                { "id": "keep", "type": "procedural", "title": "Keep", "next": ["other"] },
                { "id": "other", "type": "procedural", "title": "Other", "next": ["done"] },
                { "id": "done", "type": "terminal", "title": "Done" }
              ]
            }
            """;
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(json));

        runtime.RemoveNode("gate");

        // The conditional is now a procedural pass-through whose single "next" is its first branch's target.
        var gate = runtime.Definition!.Nodes.Single(n => n.Id == "gate");
        var proc = gate.Should().BeOfType<ProceduralNode>().Which;
        proc.TaskList.Should().BeEmpty();
        proc.Next.Should().ContainSingle().Which.Should().Be("keep");
    }

    [Fact]
    public void RemoveNode_ConditionalCollapseOrphansElseSubtree_ThrowsValidationException()
    {
        // Here "orphan" is reachable ONLY via gate's else; collapsing gate to its first branch ("keep")
        // drops the else edge, so "orphan" becomes unreachable and the validator rejects the removal.
        const string json = """
            {
              "schemaVersion": 1,
              "objective": "conditional collapse orphan",
              "nodes": [
                { "id": "start", "type": "start", "title": "Start", "next": ["gate"] },
                {
                  "id": "gate",
                  "type": "conditional",
                  "title": "Gate",
                  "branches": [ { "when": "keep it", "to": "keep" } ],
                  "else": "orphan"
                },
                { "id": "keep", "type": "procedural", "title": "Keep", "next": ["done"] },
                { "id": "orphan", "type": "terminal", "title": "Orphan" },
                { "id": "done", "type": "terminal", "title": "Done" }
              ]
            }
            """;
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(json));

        var act = () => runtime.RemoveNode("gate");

        act.Should().Throw<WorkflowValidationException>().WithMessage("*unreachable*");
        // Left untouched: gate is still a conditional (the failed candidate was never committed).
        runtime.Definition!.Nodes.Single(n => n.Id == "gate").Should().BeOfType<ConditionalNode>();
    }

    private static string StatusOf(WorkflowRuntime runtime, string unitName) =>
        runtime.GetProjection(null)["tasks"]![unitName]!.GetValue<string>();
}
