using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Proves the "controller re-spawns; runtime bounds the attempts" validation-retry model: a result that
///     fails schema validation is re-surfaced as a pending unit (with its error) while the attempt budget
///     allows, and is terminally failed (routing to <c>onFailure</c>) once the budget is exhausted.
/// </summary>
public class OutputSchemaValidationTests
{
    private const string Unit = "analyze:1:task";
    private const string Valid = """{ "summary": "ok" }""";
    private const string Invalid = """{ "other": 1 }""";

    private static WorkflowRuntime RuntimeAtAnalyze(int maxValidationRetries)
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(Phase4Fixtures.SingleTask(maxValidationRetries)));
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
    ///     A sub-agent error failure reason is a STABLE, non-sensitive message plus safe metadata (the payload
    ///     length) only — never the raw payload — across all three sinks it flows into: the <c>taskErrors</c>
    ///     projection, the <c>_error</c> output marker, and the persisted
    ///     <see cref="Persistence.WorkflowTaskSnapshot.LastError"/>. Sensitive content (EUII) commonly appears
    ///     at the START of the payload, so truncation would not de-identify it; the raw text is dropped entirely.
    /// </summary>
    [Fact]
    public void FailureReason_IsStableMessageWithoutRawPayload_AcrossProjectionOutputAndSnapshot()
    {
        var runtime = RuntimeAtAnalyze(maxValidationRetries: 0);
        runtime.RegisterSpawn("tc1", Unit);

        // EUII sits at the very START of the payload (an email, standing in for prompts, names, document
        // excerpts), so a leading-prefix truncation would have leaked it. It must be dropped entirely.
        const string sentinel = "EUII_SECRET_user@example.com";
        var rawPayload = sentinel + new string('x', 600);

        runtime.ObserveResult("tc1", rawPayload, isError: true);

        var expected = $"sub-agent reported an error ({rawPayload.Length} chars)";

        var projected = runtime.GetProjection(null)["taskErrors"]![Unit]!.GetValue<string>();
        var outputMarker = runtime.Outputs["analyze"]!["task"]!["_error"]!.GetValue<string>();
        var persisted = runtime.Snapshot().Tasks.Single(t => t.Name == Unit).LastError!;

        foreach (var reason in new[] { projected, outputMarker, persisted })
        {
            // The stable message with safe length metadata is recorded; the raw EUII payload is never present.
            reason.Should().Be(expected);
            reason.Should().NotContain(sentinel);
        }
    }
}
