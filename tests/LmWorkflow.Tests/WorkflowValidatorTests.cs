using AchieveAi.LmDotnetTools.LmWorkflow.Ingest;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Tests for <see cref="WorkflowValidator"/>: the valid fixtures pass, positive reachability over
///     side edges (onFailure / onMaxVisits / onBudgetExhausted) is honored, and every V1 rule rejects
///     its violating fixture with a clear error.
/// </summary>
public class WorkflowValidatorTests
{
    private static ValidationResult Validate(string json) =>
        new WorkflowValidator().Validate(WorkflowJson.Deserialize(json));

    // ---- Valid fixtures ------------------------------------------------------------------------

    [Fact]
    public void RepresentativeWorkflow_IsValid()
    {
        var result = Validate(WorkflowFixtures.ValidWorkflow);

        result.IsValid.Should().BeTrue(because: string.Join(" | ", result.Errors));
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void MinimalWorkflow_IsValid()
    {
        Validate(WorkflowFixtures.MinimalValid).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateAndThrow_DoesNotThrow_ForValidWorkflow()
    {
        var def = WorkflowJson.Deserialize(WorkflowFixtures.MinimalValid);
        var act = () => new WorkflowValidator().ValidateAndThrow(def);
        act.Should().NotThrow();
    }

    // ---- Positive reachability over side edges -------------------------------------------------

    [Fact]
    public void Terminal_ReachableOnlyViaProceduralOnFailure_IsValid()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "objective": "onFailure reachability",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["p"] },
                {
                  "id": "p", "type": "procedural", "title": "P",
                  "joinPolicy": { "mode": "all" }, "next": ["t_ok"], "onFailure": "t_err"
                },
                { "id": "t_ok", "type": "terminal", "title": "OK" },
                { "id": "t_err", "type": "terminal", "title": "Err" }
              ]
            }
            """;

        var result = Validate(json);
        result.IsValid.Should().BeTrue(because: string.Join(" | ", result.Errors));
    }

    [Fact]
    public void Node_ReachableOnlyViaOnMaxVisits_IsValid()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "objective": "onMaxVisits reachability",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["c"] },
                {
                  "id": "c", "type": "conditional", "title": "Gate",
                  "branches": [ { "when": "keep looping", "to": "c" } ],
                  "else": "t", "maxVisits": 3, "onMaxVisits": "recover"
                },
                { "id": "recover", "type": "procedural", "title": "Recover", "joinPolicy": { "mode": "all" }, "next": ["t"] },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        var result = Validate(json);
        result.IsValid.Should().BeTrue(because: string.Join(" | ", result.Errors));
    }

    [Fact]
    public void Node_ReachableOnlyViaOnBudgetExhausted_IsValid()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "objective": "onBudgetExhausted reachability",
              "onBudgetExhausted": "cleanup",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["t"] },
                { "id": "t", "type": "terminal", "title": "Done" },
                { "id": "cleanup", "type": "procedural", "title": "Cleanup", "joinPolicy": { "mode": "all" }, "next": ["t"] }
              ]
            }
            """;

        var result = Validate(json);
        result.IsValid.Should().BeTrue(because: string.Join(" | ", result.Errors));
    }

    // ---- Invalid: structural rules -------------------------------------------------------------

    [Fact]
    public void TwoStartNodes_AreRejected()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "two starts",
              "nodes": [
                { "id": "s1", "type": "start", "title": "S1", "next": ["t"] },
                { "id": "s2", "type": "start", "title": "S2", "next": ["t"] },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        AssertInvalid(json, "exactly one start");
    }

    [Fact]
    public void NoTerminalNode_IsRejected()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "no terminal",
              "nodes": [ { "id": "s", "type": "start", "title": "Start", "next": ["s"] } ]
            }
            """;

        AssertInvalid(json, "at least one terminal");
    }

    [Fact]
    public void TrulyUnreachableNode_IsRejected()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "unreachable",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["t"] },
                { "id": "t", "type": "terminal", "title": "Done" },
                { "id": "orphan", "type": "procedural", "title": "Orphan", "joinPolicy": { "mode": "all" }, "next": ["t"] }
              ]
            }
            """;

        AssertInvalid(json, "unreachable");
    }

    [Fact]
    public void DanglingEdgeTarget_IsRejected()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "dangling",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["p"] },
                {
                  "id": "p", "type": "procedural", "title": "P",
                  "joinPolicy": { "mode": "all" }, "next": ["t"], "onFailure": "ghost"
                },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        var result = Validate(json);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Dangling edge") && e.Contains("ghost"));
    }

    [Fact]
    public void MissingElse_IsRejected()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "missing else",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["c"] },
                { "id": "c", "type": "conditional", "title": "Gate", "branches": [ { "when": "go", "to": "t" } ] },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        AssertInvalid(json, "else");
    }

    [Fact]
    public void StartNode_WithTwoNext_IsRejected()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "two next",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["t", "t2"] },
                { "id": "t", "type": "terminal", "title": "Done" },
                { "id": "t2", "type": "terminal", "title": "Done2" }
              ]
            }
            """;

        AssertInvalid(json, "exactly one 'next'");
    }

    [Fact]
    public void ProceduralNode_WithNoNext_IsRejected()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "no next",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["p"] },
                { "id": "p", "type": "procedural", "title": "P", "joinPolicy": { "mode": "all" }, "next": [] },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        AssertInvalid(json, "at least one 'next'");
    }

    [Fact]
    public void DuplicateTaskIdWithinNode_IsRejected()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "dup task",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["p"] },
                {
                  "id": "p", "type": "procedural", "title": "P", "joinPolicy": { "mode": "all" }, "next": ["t"],
                  "taskList": [
                    { "id": "a", "delegate": "agent", "subagent_type": "x", "promptTemplate": "1" },
                    { "id": "a", "delegate": "agent", "subagent_type": "x", "promptTemplate": "2" }
                  ]
                },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        AssertInvalid(json, "Duplicate task id");
    }

    // ---- Invalid: schema / condition rules -----------------------------------------------------

    [Fact]
    public void UnresolvedRef_IsRejected()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "bad ref",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["t"] },
                { "id": "t", "type": "terminal", "title": "Done", "finalOutputSchema": { "$ref": "#/$defs/Missing" } }
              ]
            }
            """;

        var result = Validate(json);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Unresolved $ref") && e.Contains("Missing"));
    }

    [Fact]
    public void UnknownConditionOp_IsRejected()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "bad op",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["c"] },
                {
                  "id": "c", "type": "conditional", "title": "Gate",
                  "branches": [ { "when": { "op": "between", "path": "state.x", "value": 1 }, "to": "t" } ],
                  "else": "t"
                },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        var result = Validate(json);
        result.IsValid.Should().BeFalse();
        result
            .Errors.Should()
            .Contain(e => e.Contains("unknown condition op") && e.Contains("between"));
    }

    // ---- Invalid: agent / write rules ----------------------------------------------------------

    [Fact]
    public void AgentTaskMissingSubagentType_IsRejected()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "no subagent",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["p"] },
                {
                  "id": "p", "type": "procedural", "title": "P", "joinPolicy": { "mode": "all" }, "next": ["t"],
                  "taskList": [ { "id": "a", "delegate": "agent", "promptTemplate": "x" } ]
                },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        AssertInvalid(json, "subagent_type");
    }

    [Fact]
    public void WritesTo_NotStatePrefixed_IsRejected()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "bad write target",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["p"] },
                {
                  "id": "p", "type": "procedural", "title": "P", "joinPolicy": { "mode": "all" }, "next": ["t"],
                  "taskList": [
                    { "id": "a", "delegate": "agent", "subagent_type": "x", "promptTemplate": "y", "writes": { "to": "outputs.x", "mode": "set" } }
                  ]
                },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        AssertInvalid(json, "must start with 'state.'");
    }

    // ---- Invalid: budget rules -----------------------------------------------------------------

    [Fact]
    public void NonPositiveMaxStepBudget_IsRejected()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "bad budget", "maxStepBudget": 0,
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["t"] },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        AssertInvalid(json, "maxStepBudget must be greater than 0");
    }

    [Fact]
    public void NonPositiveMaxVisits_IsRejected()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "bad maxVisits",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["c"] },
                {
                  "id": "c", "type": "conditional", "title": "Gate",
                  "branches": [ { "when": "go", "to": "t" } ], "else": "t", "maxVisits": 0
                },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        AssertInvalid(json, "maxVisits must be greater than 0");
    }

    // ---- Invalid: V1 restrictions --------------------------------------------------------------

    [Fact]
    public void ReduceNodeType_IsRejectedAsNotSupportedInV1()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "reduce node",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["t"] },
                { "id": "r", "type": "reduce", "title": "Reduce" },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        var result = Validate(json);
        result.IsValid.Should().BeFalse();
        result
            .Errors.Should()
            .Contain(e => e.Contains("reduce") && e.Contains("not supported in V1"));
    }

    [Fact]
    public void TasksModeRuntime_IsRejectedAsNotSupportedInV1()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "runtime tasks",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["p"] },
                {
                  "id": "p", "type": "procedural", "title": "P",
                  "tasksMode": "runtime", "joinPolicy": { "mode": "all" }, "next": ["t"]
                },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        var result = Validate(json);
        result.IsValid.Should().BeFalse();
        result
            .Errors.Should()
            .Contain(e => e.Contains("tasksMode 'runtime'") && e.Contains("not supported in V1"));
    }

    [Fact]
    public void JoinPolicyQuorum_IsRejectedAsNotSupportedInV1()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "quorum join",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["p"] },
                {
                  "id": "p", "type": "procedural", "title": "P",
                  "joinPolicy": { "mode": "quorum", "threshold": 0.5 }, "next": ["t"]
                },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        var result = Validate(json);
        result.IsValid.Should().BeFalse();
        result
            .Errors.Should()
            .Contain(e => e.Contains("quorum") && e.Contains("not supported in V1"));
    }

    [Fact]
    public void WritesModeUpsert_IsRejectedAsNotSupportedInV1()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "upsert write",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["p"] },
                {
                  "id": "p", "type": "procedural", "title": "P", "joinPolicy": { "mode": "all" }, "next": ["t"],
                  "taskList": [
                    { "id": "a", "delegate": "agent", "subagent_type": "x", "promptTemplate": "y", "writes": { "to": "state.x", "mode": "upsert" } }
                  ]
                },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        var result = Validate(json);
        result.IsValid.Should().BeFalse();
        result
            .Errors.Should()
            .Contain(e => e.Contains("upsert") && e.Contains("not supported in V1"));
    }

    // ---- ValidateAndThrow ----------------------------------------------------------------------

    [Fact]
    public void ValidateAndThrow_Throws_WithAllErrors_ForInvalidWorkflow()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "no terminal",
              "nodes": [ { "id": "s", "type": "start", "title": "Start", "next": ["s"] } ]
            }
            """;

        var def = WorkflowJson.Deserialize(json);
        var act = () => new WorkflowValidator().ValidateAndThrow(def);

        act.Should()
            .Throw<WorkflowValidationException>()
            .Which.Errors.Should()
            .Contain(e => e.Contains("at least one terminal"));
    }

    private static void AssertInvalid(string json, string expectedFragment)
    {
        var result = Validate(json);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains(expectedFragment));
    }
}
