using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Pins the tolerant task-output contract that keeps a sub-agent's free-form reply from triggering a retry
///     storm (the PR-222-review run oddity): a task with NO <c>outputSchema</c> accepts free-form output
///     verbatim (parsed JSON when the reply is JSON, otherwise the raw text stored as a JSON string) and never
///     fails for "not valid JSON"; a schema'd task tolerates JSON wrapped in a Markdown fence or surrounding
///     prose — the embedded JSON is extracted before parse + schema validation. Before the fix,
///     <c>ValidateAndRecord</c> force-parsed the WHOLE reply as JSON, so a Markdown report failed, re-surfaced,
///     and (with escalating strictness) consolidated to nothing.
/// </summary>
public class TaskOutputToleranceTests
{
    private const string Unit = "analyze:1:task";

    // A realistic specialized-reviewer reply: a Markdown findings report with NO JSON-parseable span.
    private const string MarkdownReport = """
        ## Findings

        No blocking issues found. The change looks correct and well tested.

        - Naming is consistent with the surrounding code.
        - Error handling routes through the existing failure policy.
        """;

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
    public void NoSchema_FreeFormMarkdown_IsStoredAsStringWithoutFailing()
    {
        var runtime = RuntimeAtAnalyze(Phase4Fixtures.NoSchemaSingleTask(maxValidationRetries: 0));
        runtime.RegisterSpawn("tc1", Unit);

        runtime.ObserveResult("tc1", MarkdownReport, isError: false);

        StatusOf(runtime, Unit).Should().Be("validated");
        ReSurfaced(runtime, Unit).Should().BeFalse();
        // The free-form reply is preserved verbatim as the task output (a JSON string value), not discarded.
        runtime.Outputs["analyze"]!["task"]!.GetValue<string>().Should().Be(MarkdownReport);
    }

    [Fact]
    public void NoSchema_FencedJson_IsUnwrappedAndStoredAsJson()
    {
        var runtime = RuntimeAtAnalyze(Phase4Fixtures.NoSchemaSingleTask(maxValidationRetries: 0));
        runtime.RegisterSpawn("tc1", Unit);

        var reply = "```json\n{ \"summary\": \"ok\", \"count\": 2 }\n```";
        runtime.ObserveResult("tc1", reply, isError: false);

        StatusOf(runtime, Unit).Should().Be("validated");
        runtime.Outputs["analyze"]!["task"]!["summary"]!.GetValue<string>().Should().Be("ok");
        runtime.Outputs["analyze"]!["task"]!["count"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void Schema_FencedJson_IsUnwrappedValidatedAndRecorded()
    {
        var runtime = RuntimeAtAnalyze(Phase4Fixtures.SingleTask(maxValidationRetries: 0));
        runtime.RegisterSpawn("tc1", Unit);

        var reply = "```json\n{ \"summary\": \"ok\" }\n```";
        runtime.ObserveResult("tc1", reply, isError: false);

        StatusOf(runtime, Unit).Should().Be("validated");
        runtime.Outputs["analyze"]!["task"]!["summary"]!.GetValue<string>().Should().Be("ok");
    }

    [Fact]
    public void Schema_ProseWrappedJson_IsExtractedValidatedAndRecorded()
    {
        var runtime = RuntimeAtAnalyze(Phase4Fixtures.SingleTask(maxValidationRetries: 0));
        runtime.RegisterSpawn("tc1", Unit);

        var reply = "Sure! Here is the result:\n{ \"summary\": \"ok\" }\nHope that helps.";
        runtime.ObserveResult("tc1", reply, isError: false);

        StatusOf(runtime, Unit).Should().Be("validated");
        runtime.Outputs["analyze"]!["task"]!["summary"]!.GetValue<string>().Should().Be("ok");
    }
}
