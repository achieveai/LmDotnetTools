using System.Text.Json;
using AchieveAi.LmDotnetTools.LmWorkflow.Ingest;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Coverage for the flat, LLM-facing <see cref="SimpleWorkflow"/> authoring surface and its translation
///     into the internal <see cref="WorkflowDefinition"/>: a realistic pipeline authored as JSON round-trips
///     and passes the real <see cref="WorkflowValidator"/>, each step kind maps to the right node, data-flow
///     (<c>saveAs</c>) becomes a state write, and missing kind-specific fields surface as one batched,
///     LLM-readable error.
/// </summary>
public class SimpleWorkflowTranslationTests
{
    private static readonly JsonSerializerOptions LlmJson = new() { PropertyNameCaseInsensitive = true };

    private static SimpleWorkflow Parse(string json) =>
        JsonSerializer.Deserialize<SimpleWorkflow>(json, LlmJson)!;

    [Fact]
    public void RealisticPipeline_AuthoredAsFlatJson_TranslatesAndPassesTheValidator()
    {
        // The exact shape a model naturally emits — uniform steps, single 'next', 'agent'+'prompt', a
        // branch with 'else'. This is the workflow the internal schema made impossible to author.
        const string json = """
            {
              "objective": "Review PR #11194 with specialists, grade, and finish",
              "steps": [
                { "id": "start", "kind": "start", "next": "review" },
                { "id": "review", "kind": "agent", "title": "Specialist review",
                  "agent": "code-reviewer", "prompt": "Review the diff for PR #11194.",
                  "saveAs": "findings", "next": "grade" },
                { "id": "grade", "kind": "branch",
                  "branches": [ { "when": "there are blocking findings", "goto": "blocked" } ],
                  "else": "done" },
                { "id": "blocked", "kind": "end" },
                { "id": "done", "kind": "end" }
              ]
            }
            """;

        var def = Parse(json).ToDefinition();

        // The internal validator (the same one SetWorkflow/StartWorkflowAgent use) accepts it as-is.
        new WorkflowValidator().ValidateAndThrow(def);

        def.Objective.Should().Contain("PR #11194");
        def.Nodes.Should().HaveCount(5);
        def.Nodes.OfType<StartNode>().Should().ContainSingle();
        def.Nodes.OfType<TerminalNode>().Should().HaveCount(2);
    }

    [Fact]
    public void AgentStep_BecomesOneTaskProceduralNode_WithSaveAsAsStateWrite()
    {
        var wf = Parse(
            """
            { "objective": "o", "steps": [
              { "id": "s", "kind": "start", "next": "a" },
              { "id": "a", "kind": "agent", "agent": "researcher", "prompt": "Do X.", "saveAs": "result", "next": "e" },
              { "id": "e", "kind": "end" }
            ] }
            """
        );

        var def = wf.ToDefinition();

        var proc = def.Nodes.OfType<ProceduralNode>().Single(n => n.Id == "a");
        proc.Next.Should().ContainSingle().Which.Should().Be("e");
        var task = proc.TaskList!.Single();
        task.SubagentType.Should().Be("researcher");
        task.PromptTemplate.Should().Be("Do X.");
        task.Writes!.To.Should().Be("state.result");
        task.Writes.Mode.Should().Be(WriteMode.Set);
    }

    [Fact]
    public void BranchStep_BecomesConditionalNode_WithBranchesAndElse()
    {
        var wf = Parse(
            """
            { "objective": "o", "steps": [
              { "id": "s", "kind": "start", "next": "g" },
              { "id": "g", "kind": "branch", "branches": [ { "when": "ready", "goto": "ok" } ], "else": "no" },
              { "id": "ok", "kind": "end" },
              { "id": "no", "kind": "end" }
            ] }
            """
        );

        var def = wf.ToDefinition();

        var gate = def.Nodes.OfType<ConditionalNode>().Single(n => n.Id == "g");
        gate.Branches.Should().ContainSingle();
        gate.Branches[0].To.Should().Be("ok");
        gate.Branches[0].When!.GetValue<string>().Should().Be("ready");
        gate.Else.Should().Be("no");
    }

    [Fact]
    public void MissingTitle_DefaultsToId()
    {
        var wf = Parse(
            """
            { "objective": "o", "steps": [
              { "id": "s", "kind": "start", "next": "e" }, { "id": "e", "kind": "end" }
            ] }
            """
        );

        wf.ToDefinition().Nodes.Single(n => n.Id == "e").Title.Should().Be("e");
    }

    [Theory]
    [InlineData("task", typeof(ProceduralNode))] // alias for agent
    [InlineData("if", typeof(ConditionalNode))] // alias for branch
    [InlineData("terminal", typeof(TerminalNode))] // alias for end
    public void KindAliases_AndCaseInsensitivity_AreAccepted(string kind, Type expectedNodeType)
    {
        // Every node kind reachable so the graph validates; the aliased/UPPER middle node is what we assert.
        var mid = kind switch
        {
            "if" => """{ "id": "m", "kind": "IF", "branches": [ { "when": "x", "goto": "e" } ], "else": "e" }""",
            "terminal" => """{ "id": "m", "kind": "TERMINAL" }""",
            _ => """{ "id": "m", "kind": "TASK", "agent": "a", "prompt": "p", "next": "e" }""",
        };
        var wf = Parse(
            $$"""
            { "objective": "o", "steps": [
              { "id": "s", "kind": "start", "next": "m" }, {{mid}}, { "id": "e", "kind": "end" }
            ] }
            """
        );

        wf.ToDefinition().Nodes.Single(n => n.Id == "m").Should().BeOfType(expectedNodeType);
    }

    [Fact]
    public void AgentStep_MissingAgentPromptNext_ThrowsOneBatchedReadableError()
    {
        var wf = Parse(
            """
            { "objective": "o", "steps": [
              { "id": "s", "kind": "start", "next": "a" },
              { "id": "a", "kind": "agent" },
              { "id": "e", "kind": "end" }
            ] }
            """
        );

        var act = () => wf.ToDefinition();

        var ex = act.Should().Throw<WorkflowValidationException>().Which;
        ex.Errors.Should().Contain(e => e.Contains("'a'") && e.Contains("'agent'"));
        ex.Errors.Should().Contain(e => e.Contains("'a'") && e.Contains("'prompt'"));
        ex.Errors.Should().Contain(e => e.Contains("'a'") && e.Contains("'next'"));
    }

    [Fact]
    public void UnknownKind_IsRejected_WithTheAllowedKindsListed()
    {
        var wf = Parse(
            """
            { "objective": "o", "steps": [
              { "id": "s", "kind": "start", "next": "x" },
              { "id": "x", "kind": "action", "next": "e" },
              { "id": "e", "kind": "end" }
            ] }
            """
        );

        var act = () => wf.ToDefinition();

        act.Should()
            .Throw<WorkflowValidationException>()
            .Which.Errors.Should()
            .Contain(e => e.Contains("unknown kind 'action'") && e.Contains("start, agent, parallel, branch, end"));
    }

    [Fact]
    public void ParallelStep_RunsSeveralAgentsConcurrently_AsAMultiTaskNode()
    {
        // The "dispatch specialists" case done right: three DIFFERENT agents in one node, joined together.
        var def = Parse(
            """
            { "objective": "review", "steps": [
              { "id": "start", "kind": "start", "next": "review" },
              { "id": "review", "kind": "parallel", "next": "merge", "agents": [
                { "agent": "correctness", "prompt": "Check correctness of {{state.diff}}", "saveAs": "c" },
                { "agent": "security",    "prompt": "Check security of {{state.diff}}",    "saveAs": "s" },
                { "agent": "tests",       "prompt": "Check tests of {{state.diff}}",       "saveAs": "t" }
              ] },
              { "id": "merge", "kind": "agent", "agent": "gp",
                "prompt": "Merge {{state.c}} {{state.s}} {{state.t}}", "next": "done" },
              { "id": "done", "kind": "end" }
            ] }
            """
        ).ToDefinition();

        new WorkflowValidator().ValidateAndThrow(def);

        var review = def.Nodes.OfType<ProceduralNode>().Single(n => n.Id == "review");
        review.TaskList.Should().HaveCount(3);
        review.TaskList!.Select(t => t.SubagentType).Should().Equal("correctness", "security", "tests");
        review.TaskList!.Select(t => t.Writes!.To).Should().Equal("state.c", "state.s", "state.t");
        // A multi-task node with the default all-join is what the engine composes into parallel spawns.
        review.JoinPolicy.Mode.Should().Be(JoinMode.All);
    }

    [Fact]
    public void ForEachStep_FansTheSameAgentOverACollection_Sequentially_AppendingResults()
    {
        var def = Parse(
            """
            { "objective": "review each file", "steps": [
              { "id": "start", "kind": "start", "next": "list" },
              { "id": "list", "kind": "agent", "agent": "finder", "prompt": "List changed files.",
                "saveAs": "files", "next": "map" },
              { "id": "map", "kind": "agent", "agent": "reviewer", "prompt": "Review {{item}}.",
                "forEach": "state.files", "saveAs": "reviews", "next": "done" },
              { "id": "done", "kind": "end" }
            ] }
            """
        ).ToDefinition();

        new WorkflowValidator().ValidateAndThrow(def);

        var map = def.Nodes.OfType<ProceduralNode>().Single(n => n.Id == "map");
        var task = map.TaskList!.Single();
        task.ForEach.Should().Be("state.files");
        task.Parallel.Should().BeFalse(); // V1 runs forEach sequentially; parallelism is the 'parallel' kind
        task.Writes!.To.Should().Be("state.reviews");
        task.Writes.Mode.Should().Be(WriteMode.Append); // one output per element accumulates into the array
    }

    [Fact]
    public void Loop_BackwardEdgeWithVisitCap_TranslatesAndValidates()
    {
        // A retry loop: work → check → (back to work | forward to done), with a hard visit cap + escape.
        var def = Parse(
            """
            { "objective": "retry until good", "steps": [
              { "id": "start", "kind": "start", "next": "work" },
              { "id": "work", "kind": "agent", "agent": "worker", "prompt": "Attempt the task.",
                "saveAs": "attempt", "next": "check", "maxVisits": 3, "onMaxVisits": "giveup" },
              { "id": "check", "kind": "branch",
                "branches": [ { "when": "the attempt still needs work", "goto": "work" } ],
                "else": "done" },
              { "id": "done", "kind": "end" },
              { "id": "giveup", "kind": "end" }
            ] }
            """
        ).ToDefinition();

        // The loop (work → check → work) plus the maxVisits escape is a valid graph.
        new WorkflowValidator().ValidateAndThrow(def);

        var work = def.Nodes.OfType<ProceduralNode>().Single(n => n.Id == "work");
        work.Next.Should().ContainSingle().Which.Should().Be("check");
        work.MaxVisits.Should().Be(3);
        work.OnMaxVisits.Should().Be("giveup");
        // The backward edge that forms the loop.
        def.Nodes.OfType<ConditionalNode>().Single(n => n.Id == "check").Branches[0].To.Should().Be("work");
    }

    [Fact]
    public void RoundTrip_DslToDefinitionAndBack_PreservesTheAuthoringShape()
    {
        // A workflow exercising start/parallel/branch/agent(+saveAs)/loop/end. A DSL-authored workflow
        // must survive ToDefinition() → FromDefinition() unchanged, so GetWorkflow reads back what was written.
        const string json = """
            { "objective": "review", "steps": [
              { "id": "start", "kind": "start", "next": "dispatch" },
              { "id": "dispatch", "kind": "parallel", "next": "grade", "agents": [
                { "agent": "a1", "prompt": "p1", "saveAs": "r1" },
                { "agent": "a2", "prompt": "p2", "saveAs": "r2" }
              ] },
              { "id": "grade", "kind": "branch",
                "branches": [ { "when": "needs work", "goto": "revise" } ], "else": "done" },
              { "id": "revise", "kind": "agent", "agent": "gp", "prompt": "Fix {{state.r1}}",
                "saveAs": "r1", "next": "grade", "maxVisits": 2, "onMaxVisits": "done" },
              { "id": "done", "kind": "end" }
            ] }
            """;
        var original = Parse(json);

        var roundTripped = SimpleWorkflowTranslator.FromDefinition(original.ToDefinition());

        // Compare the canonical camelCase DSL rendering of both — a faithful round-trip is byte-identical.
        JsonSerializer.Serialize(roundTripped, SimpleWorkflow.OutputJsonOptions)
            .Should()
            .Be(JsonSerializer.Serialize(original, SimpleWorkflow.OutputJsonOptions));
    }
}
