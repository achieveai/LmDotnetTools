using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Tests for the workflow model: polymorphic node deserialization, JsonNode round-tripping of
///     schema fragments / templates, structured-condition parsing, and serialize stability.
/// </summary>
public class WorkflowModelTests
{
    [Fact]
    public void Deserialize_ResolvesPolymorphicNodeTypes()
    {
        var def = WorkflowJson.Deserialize(WorkflowFixtures.ValidWorkflow);

        def.Nodes.Should().HaveCount(6);
        def.Nodes[0].Should().BeOfType<StartNode>();
        def.Nodes[1].Should().BeOfType<ProceduralNode>();
        def.Nodes[2].Should().BeOfType<ConditionalNode>();
        def.Nodes[3].Should().BeOfType<ProceduralNode>();
        def.Nodes[4].Should().BeOfType<TerminalNode>();
        def.Nodes[5].Should().BeOfType<TerminalNode>();

        // Type discriminator mirrors the concrete runtime type.
        def.Nodes[0].Type.Should().Be(NodeType.Start);
        def.Nodes[2].Type.Should().Be(NodeType.Conditional);
    }

    [Fact]
    public void Deserialize_ReadsProceduralTaskDetails()
    {
        var def = WorkflowJson.Deserialize(WorkflowFixtures.ValidWorkflow);

        var procedural = def.Nodes.OfType<ProceduralNode>().First();
        procedural.TasksMode.Should().Be(TasksMode.Authored);
        procedural.JoinPolicy.Mode.Should().Be(JoinMode.All);

        var task = procedural.TaskList.Should().ContainSingle().Subject;
        task.Delegate.Should().Be(DelegateKind.Agent);
        task.SubagentType.Should().Be("summarizer");
        task.ForEach.Should().Be("state.documents");
        task.Writes!.Mode.Should().Be(WriteMode.Append);
        task.Writes.To.Should().Be("state.summaries");
    }

    [Fact]
    public void Deserialize_KeepsForwardCompatAuthoringFields_EvenThoughValidatorRejectsThem()
    {
        // node.maxParallel and task.parallel are forward-compat model properties: the V1 validator rejects
        // them, but the model must still round-trip them so a future runtime can honor them without a
        // schema change. This is a pure deserialization assertion that never runs through the validator.
        const string json = """
            {
              "schemaVersion": 1,
              "objective": "forward-compat authoring fields",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["p"] },
                {
                  "id": "p",
                  "type": "procedural",
                  "title": "P",
                  "maxParallel": 3,
                  "next": ["t"],
                  "taskList": [
                    {
                      "id": "a",
                      "delegate": "agent",
                      "subagent_type": "x",
                      "forEach": "state.items",
                      "parallel": true,
                      "promptTemplate": "do {{item}}"
                    }
                  ]
                },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        var def = WorkflowJson.Deserialize(json);

        var procedural = def.Nodes.OfType<ProceduralNode>().Single();
        procedural.MaxParallel.Should().Be(3);
        procedural.TaskList!.Single().Parallel.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_KeepsSchemaFragmentsAndTemplatesAsJsonNode()
    {
        var def = WorkflowJson.Deserialize(WorkflowFixtures.ValidWorkflow);

        var task = def.Nodes.OfType<ProceduralNode>().First().TaskList!.First();
        task.OutputSchema.Should().BeAssignableTo<JsonNode>();
        task.OutputSchema!["$ref"]!.GetValue<string>().Should().Be("#/$defs/Summary");

        var terminal = def.Nodes.OfType<TerminalNode>().First(t => t.ResultTemplate is not null);
        terminal.ResultTemplate.Should().BeAssignableTo<JsonNode>();
        terminal.ResultTemplate!["result"]!.GetValue<string>().Should().Be("{{state.final}}");
    }

    [Fact]
    public void Deserialize_ParsesStructuredWhenIntoCondition()
    {
        var def = WorkflowJson.Deserialize(WorkflowFixtures.ValidWorkflow);

        var gate = def.Nodes.OfType<ConditionalNode>().Single();
        var condition = gate.Branches.Single().StructuredCondition;

        condition.Should().NotBeNull();
        condition!.Op.Should().Be(ConditionOp.Gt);
        condition.Path.Should().Be("state.summaries.length");
        condition.Value!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public void StructuredCondition_IsNull_ForProseWhen()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "objective": "prose gate",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["c"] },
                {
                  "id": "c",
                  "type": "conditional",
                  "title": "Gate",
                  "branches": [ { "when": "when there is at least one summary", "to": "t" } ],
                  "else": "t"
                },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        var def = WorkflowJson.Deserialize(json);

        var branch = def.Nodes.OfType<ConditionalNode>().Single().Branches.Single();
        branch.When!.GetValueKind().Should().Be(System.Text.Json.JsonValueKind.String);
        branch.StructuredCondition.Should().BeNull();
    }

    [Fact]
    public void NodeType_IsDerivedFromConcreteRecordType()
    {
        var def = WorkflowJson.Deserialize(WorkflowFixtures.ValidWorkflow);

        def.Nodes.OfType<StartNode>().Single().Type.Should().Be(NodeType.Start);
        def.Nodes.OfType<ProceduralNode>().First().Type.Should().Be(NodeType.Procedural);
        def.Nodes.OfType<ConditionalNode>().Single().Type.Should().Be(NodeType.Conditional);
        def.Nodes.OfType<TerminalNode>().First().Type.Should().Be(NodeType.Terminal);
    }

    [Fact]
    public void UnknownNode_PreservesRawType_AndDoesNotCollapseToStart_OnRoundTrip()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "objective": "unknown node round trip",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["t"] },
                { "id": "r", "type": "reduce", "title": "Reduce" },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        var def = WorkflowJson.Deserialize(json);
        var unknown = def.Nodes.OfType<UnknownNode>().Single();
        unknown.RawType.Should().Be("reduce");
        unknown.Type.Should().Be(NodeType.Unknown);

        // Re-serializing then re-reading preserves the original discriminator and does NOT collapse to a
        // StartNode (the round-trip bug the derived-Type fix addresses).
        var roundTripped = WorkflowJson.Deserialize(WorkflowJson.Serialize(def));
        var node = roundTripped.Nodes.Single(n => n.Id == "r");
        node.Should().BeOfType<UnknownNode>();
        ((UnknownNode)node).RawType.Should().Be("reduce");
        node.Type.Should().Be(NodeType.Unknown);
    }

    [Fact]
    public void Serialize_Then_Deserialize_IsStable()
    {
        var def = WorkflowJson.Deserialize(WorkflowFixtures.ValidWorkflow);

        var first = WorkflowJson.Serialize(def);
        var roundTripped = WorkflowJson.Deserialize(first);
        var second = WorkflowJson.Serialize(roundTripped);

        second.Should().Be(first);
    }
}
