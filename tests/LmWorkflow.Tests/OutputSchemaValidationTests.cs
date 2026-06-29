using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Proves the "controller re-spawns; runtime bounds the attempts" validation-retry model: a result that
///     fails schema validation is re-surfaced as a pending unit (with its error) while the attempt budget
///     allows, and is terminally failed (routing to <c>onFailure</c>) once the budget is exhausted. It also
///     pins the de-identification invariant across <b>every</b> terminal-failure path (sub-agent error,
///     invalid JSON, schema validation failure, and state-write failure): the failure reason recorded into the
///     three durable/projected sinks is a STABLE message plus safe metadata only — never the raw payload.
/// </summary>
public class OutputSchemaValidationTests
{
    private const string Unit = "analyze:1:task";
    private const string Valid = """{ "summary": "ok" }""";
    private const string Invalid = """{ "other": 1 }""";

    // The EUII sentinel sits at the very START of every offending payload/value below (standing in for
    // prompts, names, emails, document excerpts), so a leading-prefix truncation would have leaked it — the
    // de-identification invariant is that the raw text is dropped ENTIRELY, not merely truncated.
    private const string Sentinel = "EUII_SECRET_user@example.com";

    // A single-task workflow whose task writes with mode `merge` and has NO outputSchema, so a non-object
    // validated output passes (there is nothing to validate against) and then throws when StateWriter tries to
    // merge it — driving the de-identified state-write-failure path.
    private const string MergeNonObjectWorkflow = """
        {
          "schemaVersion": 1,
          "objective": "Analyze the topic.",
          "sharedContext": "SHARED_CTX",
          "inputs": { "topic": "widgets" },
          "state": {},
          "maxStepBudget": 50,
          "nodes": [
            { "id": "start", "type": "start", "title": "Start", "next": ["analyze"] },
            {
              "id": "analyze",
              "type": "procedural",
              "title": "Analyze",
              "tasksMode": "authored",
              "joinPolicy": { "mode": "all" },
              "onFailure": "fail",
              "taskList": [
                {
                  "id": "task",
                  "delegate": "agent",
                  "subagent_type": "general-purpose",
                  "promptTemplate": "Analyze {{inputs.topic}}.",
                  "writes": { "to": "state.analysis", "mode": "merge" },
                  "onFailure": "fail",
                  "maxValidationRetries": 0
                }
              ],
              "next": ["done"]
            },
            { "id": "done", "type": "terminal", "title": "Done" },
            { "id": "fail", "type": "terminal", "title": "Failed" }
          ]
        }
        """;

    private static WorkflowRuntime RuntimeAtAnalyze(int maxValidationRetries) =>
        RuntimeAtAnalyze(Phase4Fixtures.SingleTask(maxValidationRetries));

    private static WorkflowRuntime RuntimeAtAnalyze(string definitionJson)
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(definitionJson));
        runtime.AdvanceTo("start", "analyze", null);
        _ = runtime.ComposeNextExpectedAction();
        return runtime;
    }

    private static string StatusOf(WorkflowRuntime runtime, string unit) =>
        runtime.GetProjection(null)["tasks"]![unit]!.GetValue<string>();

    private static bool ReSurfaced(WorkflowRuntime runtime, string unit) =>
        runtime
            .GetProjection(null)["nextExpectedAction"]!.AsArray()
            .Any(n => n!["name"]!.GetValue<string>() == unit);

    [Fact]
    public void ValidOutput_IsRecorded()
    {
        var runtime = RuntimeAtAnalyze(maxValidationRetries: 1);
        runtime.RegisterSpawn("tc1", Unit);

        runtime.ObserveResult("tc1", Valid, isError: false);

        StatusOf(runtime, Unit).Should().Be("validated");
        runtime.Outputs["analyze"]!["task"]!["summary"]!.GetValue<string>().Should().Be("ok");
    }

    [Fact]
    public void InvalidOutput_WithinBudget_ReSurfacesPendingWithLastError()
    {
        var runtime = RuntimeAtAnalyze(maxValidationRetries: 1);
        runtime.RegisterSpawn("tc1", Unit);

        runtime.ObserveResult("tc1", Invalid, isError: false);

        var projection = runtime.GetProjection(null);
        projection["tasks"]![Unit]!.GetValue<string>().Should().Be("pending");
        projection["taskErrors"]![Unit]!
            .GetValue<string>()
            .Should()
            .Contain("did not match the required schema");
        ReSurfaced(runtime, Unit).Should().BeTrue();

        // No terminal error marker is recorded while the unit is still retryable.
        runtime.Outputs["analyze"]!.AsObject().Should().NotContainKey("task");
    }

    [Fact]
    public void InvalidOutput_BudgetExhausted_FailsAndSurfacesOnFailure()
    {
        var runtime = RuntimeAtAnalyze(maxValidationRetries: 1);

        // First attempt fails (retryable) ...
        runtime.RegisterSpawn("tc1", Unit);
        runtime.ObserveResult("tc1", Invalid, isError: false);

        // ... controller re-spawns the re-surfaced unit (fresh correlation) and it fails again.
        _ = runtime.ComposeNextExpectedAction();
        runtime.RegisterSpawn("tc2", Unit);
        runtime.ObserveResult("tc2", Invalid, isError: false);

        var projection = runtime.GetProjection(null);
        projection["tasks"]![Unit]!.GetValue<string>().Should().Be("failed");
        projection["onFailure"]!.GetValue<string>().Should().Be("fail");
        runtime.Outputs["analyze"]!["task"]!["_error"].Should().NotBeNull();
    }

    [Fact]
    public void InvalidOutput_ZeroRetries_FailsImmediately()
    {
        var runtime = RuntimeAtAnalyze(maxValidationRetries: 0);
        runtime.RegisterSpawn("tc1", Unit);

        runtime.ObserveResult("tc1", Invalid, isError: false);

        var projection = runtime.GetProjection(null);
        projection["tasks"]![Unit]!.GetValue<string>().Should().Be("failed");
        projection["onFailure"]!.GetValue<string>().Should().Be("fail");
        runtime.Outputs["analyze"]!["task"]!["_error"].Should().NotBeNull();
    }

    /// <summary>
    ///     The <b>sub-agent error</b> terminal-failure reason is a STABLE, non-sensitive message plus safe
    ///     metadata (the payload length) only — never the raw payload — across all three sinks it flows into:
    ///     the <c>taskErrors</c> projection, the <c>_error</c> output marker, and the persisted
    ///     <see cref="Persistence.WorkflowTaskSnapshot.LastError"/>. Sensitive content (EUII) commonly appears
    ///     at the START of the payload, so truncation would not de-identify it; the raw text is dropped entirely.
    /// </summary>
    [Fact]
    public void SubAgentErrorFailureReason_IsStableMessageWithoutRawPayload_AcrossAllSinks()
    {
        var runtime = RuntimeAtAnalyze(maxValidationRetries: 0);
        runtime.RegisterSpawn("tc1", Unit);

        var rawPayload = Sentinel + new string('x', 600);

        runtime.ObserveResult("tc1", rawPayload, isError: true);

        AssertReasonDeIdentified(
            runtime,
            reason => reason.Should().Be($"sub-agent reported an error ({rawPayload.Length} chars)")
        );
    }

    /// <summary>
    ///     The <b>invalid-JSON</b> terminal-failure reason records only the payload length (the parser message
    ///     can echo the payload), and never the raw, sentinel-bearing text — across all three sinks.
    /// </summary>
    [Fact]
    public void InvalidJsonFailureReason_IsStableMessageWithoutRawPayload_AcrossAllSinks()
    {
        var runtime = RuntimeAtAnalyze(maxValidationRetries: 0);
        runtime.RegisterSpawn("tc1", Unit);

        // Non-JSON text with the EUII sentinel at the very start (so JsonNode.Parse throws immediately).
        var rawPayload = Sentinel + " is not valid json " + new string('x', 600);

        runtime.ObserveResult("tc1", rawPayload, isError: false);

        AssertReasonDeIdentified(
            runtime,
            reason => reason.Should().Be($"task output was not valid JSON ({rawPayload.Length} chars)")
        );
    }

    /// <summary>
    ///     The <b>schema-validation-failure</b> terminal-failure reason records only the validation-error COUNT
    ///     (the joined error strings can echo submitted values), and never the raw, sentinel-bearing offending
    ///     value — across all three sinks.
    /// </summary>
    [Fact]
    public void SchemaFailureReason_IsStableMessageWithoutRawPayload_AcrossAllSinks()
    {
        var runtime = RuntimeAtAnalyze(maxValidationRetries: 0);
        runtime.RegisterSpawn("tc1", Unit);

        // Valid JSON that violates the outputSchema: `summary` must be a string but is an array whose first
        // element is the EUII sentinel — so the offending value carries the sentinel.
        var offendingValue = Sentinel + new string('x', 600);
        var rawPayload = "{ \"summary\": [\"" + offendingValue + "\"] }";

        runtime.ObserveResult("tc1", rawPayload, isError: false);

        AssertReasonDeIdentified(
            runtime,
            reason =>
                reason
                    .Should()
                    .Match("task output did not match the required schema (* validation error(s))")
        );
    }

    /// <summary>
    ///     The <b>state-write-failure</b> terminal-failure reason records only the structural exception TYPE
    ///     name (<c>ex.Message</c> can echo the submitted value), and never the raw, sentinel-bearing submitted
    ///     value — across all three sinks. Driven by a <c>merge</c> write whose validated output is a non-object.
    /// </summary>
    [Fact]
    public void StateWriteFailureReason_IsStableMessageWithoutRawPayload_AcrossAllSinks()
    {
        var runtime = RuntimeAtAnalyze(MergeNonObjectWorkflow);
        runtime.RegisterSpawn("tc1", Unit);

        // A `merge` write requires a JSON object; this validated output is an array (a non-object) whose first
        // element is the EUII sentinel, so StateWriter throws on apply and a leak would surface the sentinel.
        var offendingValue = Sentinel + new string('x', 600);
        var rawPayload = "[\"" + offendingValue + "\"]";

        runtime.ObserveResult("tc1", rawPayload, isError: false);

        AssertReasonDeIdentified(
            runtime,
            reason =>
                reason.Should().Be("task output could not be written to state (InvalidOperationException)")
        );
    }

    /// <summary>
    ///     Asserts that the terminal-failure reason for <see cref="Unit"/> is recorded identically across all
    ///     three durable/projected sinks — the <c>taskErrors</c> projection entry, the terminal
    ///     <c>{ "_error": ... }</c> output marker, and the persisted
    ///     <see cref="Persistence.WorkflowTaskSnapshot.LastError"/> — and that NONE of them leaks the raw
    ///     <see cref="Sentinel"/>. The caller supplies <paramref name="assertReason"/> to pin the stable
    ///     message and its safe metadata (length / count / exception-type).
    /// </summary>
    private static void AssertReasonDeIdentified(WorkflowRuntime runtime, Action<string> assertReason)
    {
        var projected = runtime.GetProjection(null)["taskErrors"]![Unit]!.GetValue<string>();
        var outputMarker = runtime.Outputs["analyze"]!["task"]!["_error"]!.GetValue<string>();
        var persisted = runtime.Snapshot().Tasks.Single(t => t.Name == Unit).LastError!;

        foreach (var reason in new[] { projected, outputMarker, persisted })
        {
            assertReason(reason);
            reason.Should().NotContain(Sentinel);
        }
    }
}
