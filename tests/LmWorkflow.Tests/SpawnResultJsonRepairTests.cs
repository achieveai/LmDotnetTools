using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Pins the best-effort sub-agent JSON-repair seam (PR-222-review run oddity #4): when a schema'd blocking
///     spawn returns text that does NOT extract + parse + validate, the drive pump gives a cheap LLM ONE chance
///     to rewrite it into schema-valid JSON BEFORE the deterministic validation failure is recorded. Repair is
///     substituted ONLY when the rewrite re-validates, so it can turn a would-be failure into a recorded
///     success but can NEVER regress the deterministic failure path — a null/still-invalid rewrite leaves the
///     original result to fail exactly as before. Repair is auto-on only when a repairer is wired; a no-schema
///     task, an already-valid reply, an error result, and an unknown tool-call id never call the repair LLM.
/// </summary>
public class SpawnResultJsonRepairTests
{
    private const string Unit = "analyze:1:task";

    // The user's exact scenario: a sub-agent returned JSON that does not parse (trailing comma). Extraction
    // finds the braces span but System.Text.Json rejects it, so it fails validation identically in the pure
    // check and in the deterministic recording path.
    private const string BrokenJson = "{ \"summary\": \"ok\", }";

    // A schema-valid rewrite the repair LLM might produce from BrokenJson.
    private const string RepairedJson = "{ \"summary\": \"ok\" }";

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

    private static ToolCallResultMessage Result(string text, bool isError = false) =>
        new()
        {
            ToolCallId = "tc1",
            Result = text,
            IsError = isError,
        };

    /// <summary>A repair agent that answers every call with <paramref name="text"/> as a single text reply.</summary>
    private static Mock<IAgent> RepairAgentReturning(string text)
    {
        var mock = new Mock<IAgent>();
        mock.Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([new TextMessage { Text = text, Role = Role.Assistant }]);
        return mock;
    }

    private static void VerifyNeverCalled(Mock<IAgent> mock) =>
        mock.Verify(
            a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );

    // ---- Integration through the pump seam (MaybeRepairSpawnResultAsync) ----

    [Fact]
    public void Contrast_SchemaInvalid_WithoutRepairer_TaskFails()
    {
        // Baseline: with NO repairer wired the pump observes the raw message untouched (production's
        // `repairer is null ? message` branch), so the invalid reply fails the schema'd task as before.
        var runtime = RuntimeAtAnalyze(Phase4Fixtures.SingleTask(maxValidationRetries: 0));
        runtime.RegisterSpawn("tc1", Unit);

        runtime.ObserveMessage(Result(BrokenJson));

        StatusOf(runtime, Unit).Should().Be("failed");
    }

    [Fact]
    public async Task SchemaInvalid_WithRepairerReturningValidJson_TaskValidatedWithRepairedOutput()
    {
        // KEY behaviour: the cheap LLM rewrites the broken reply into schema-valid JSON, the pump substitutes
        // it, and the task records validated with the REPAIRED payload — the retry storm is averted.
        var runtime = RuntimeAtAnalyze(Phase4Fixtures.SingleTask(maxValidationRetries: 0));
        runtime.RegisterSpawn("tc1", Unit);
        var message = Result(BrokenJson);
        var repairer = new WorkflowJsonRepairer(RepairAgentReturning(RepairedJson).Object, "repair-model");

        var observed = await WorkflowSession.MaybeRepairSpawnResultAsync(
            runtime,
            repairer,
            message,
            CancellationToken.None
        );
        runtime.ObserveMessage(observed);

        observed.Should().NotBeSameAs(message);
        ((ToolCallResultMessage)observed).Result.Should().Be(RepairedJson);
        StatusOf(runtime, Unit).Should().Be("validated");
        runtime.Outputs["analyze"]!["task"]!["summary"]!.GetValue<string>().Should().Be("ok");
    }

    [Fact]
    public async Task SchemaInvalid_WithRepairerReturningStillInvalid_NoSubstitution_TaskFailsDeIdentified()
    {
        // Non-regression + de-identification: a still-invalid rewrite is discarded, the ORIGINAL message flows
        // to the deterministic failure path, and neither the raw reply nor the failed rewrite leaks into the
        // recorded (durable/projected) failure reason — only a de-identified count survives.
        const string GarbageRepair = "this is not valid json at all";
        var runtime = RuntimeAtAnalyze(Phase4Fixtures.SingleTask(maxValidationRetries: 0));
        runtime.RegisterSpawn("tc1", Unit);
        var message = Result(BrokenJson);
        var repairer = new WorkflowJsonRepairer(RepairAgentReturning(GarbageRepair).Object, "repair-model");

        var observed = await WorkflowSession.MaybeRepairSpawnResultAsync(
            runtime,
            repairer,
            message,
            CancellationToken.None
        );
        runtime.ObserveMessage(observed);

        observed.Should().BeSameAs(message);
        StatusOf(runtime, Unit).Should().Be("failed");
        var errorText = runtime.Outputs["analyze"]!["task"]!["_error"]!.GetValue<string>();
        errorText.Should().NotContain(BrokenJson);
        errorText.Should().NotContain(GarbageRepair);
        errorText.Should().Contain("chars");
    }

    [Fact]
    public async Task SchemaValidResult_RepairerNotInvoked_PassesThrough()
    {
        var runtime = RuntimeAtAnalyze(Phase4Fixtures.SingleTask(maxValidationRetries: 0));
        runtime.RegisterSpawn("tc1", Unit);
        var message = Result(RepairedJson);
        var mock = RepairAgentReturning("SHOULD NOT BE USED");

        var observed = await WorkflowSession.MaybeRepairSpawnResultAsync(
            runtime,
            new WorkflowJsonRepairer(mock.Object, "repair-model"),
            message,
            CancellationToken.None
        );

        observed.Should().BeSameAs(message);
        VerifyNeverCalled(mock);
    }

    [Fact]
    public async Task ErrorResult_RepairerNotInvoked_PassesThrough()
    {
        var runtime = RuntimeAtAnalyze(Phase4Fixtures.SingleTask(maxValidationRetries: 0));
        runtime.RegisterSpawn("tc1", Unit);
        var message = Result(BrokenJson, isError: true);
        var mock = RepairAgentReturning("SHOULD NOT BE USED");

        var observed = await WorkflowSession.MaybeRepairSpawnResultAsync(
            runtime,
            new WorkflowJsonRepairer(mock.Object, "repair-model"),
            message,
            CancellationToken.None
        );

        observed.Should().BeSameAs(message);
        VerifyNeverCalled(mock);
    }

    [Fact]
    public async Task NoSchemaTask_RepairerNotInvoked_PassesThrough()
    {
        // A task with no outputSchema accepts free-form output, so repair does not apply even for prose.
        var runtime = RuntimeAtAnalyze(Phase4Fixtures.NoSchemaSingleTask(maxValidationRetries: 0));
        runtime.RegisterSpawn("tc1", Unit);
        var message = Result("Free-form Markdown report, not JSON.");
        var mock = RepairAgentReturning("SHOULD NOT BE USED");

        var observed = await WorkflowSession.MaybeRepairSpawnResultAsync(
            runtime,
            new WorkflowJsonRepairer(mock.Object, "repair-model"),
            message,
            CancellationToken.None
        );

        observed.Should().BeSameAs(message);
        VerifyNeverCalled(mock);
    }

    [Fact]
    public async Task UnknownToolCallId_RepairerNotInvoked_PassesThrough()
    {
        var runtime = RuntimeAtAnalyze(Phase4Fixtures.SingleTask(maxValidationRetries: 0));
        // Deliberately NOT registered.
        var message = Result(BrokenJson);
        var mock = RepairAgentReturning("SHOULD NOT BE USED");

        var observed = await WorkflowSession.MaybeRepairSpawnResultAsync(
            runtime,
            new WorkflowJsonRepairer(mock.Object, "repair-model"),
            message,
            CancellationToken.None
        );

        observed.Should().BeSameAs(message);
        VerifyNeverCalled(mock);
    }

    // ---- The pure schema-check the pump gates on ----

    [Fact]
    public void CheckSpawnResult_SchemaValid_ReportsValid()
    {
        var runtime = RuntimeAtAnalyze(Phase4Fixtures.SingleTask(maxValidationRetries: 0));
        runtime.RegisterSpawn("tc1", Unit);

        var check = runtime.CheckSpawnResult("tc1", RepairedJson);

        check.HasSchema.Should().BeTrue();
        check.IsValid.Should().BeTrue();
    }

    [Fact]
    public void CheckSpawnResult_SchemaInvalid_ReportsInvalidWithSchema()
    {
        var runtime = RuntimeAtAnalyze(Phase4Fixtures.SingleTask(maxValidationRetries: 0));
        runtime.RegisterSpawn("tc1", Unit);

        var check = runtime.CheckSpawnResult("tc1", BrokenJson);

        check.HasSchema.Should().BeTrue();
        check.IsValid.Should().BeFalse();
        check.SchemaJson.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CheckSpawnResult_NoSchemaTask_ReportsNoSchema()
    {
        var runtime = RuntimeAtAnalyze(Phase4Fixtures.NoSchemaSingleTask(maxValidationRetries: 0));
        runtime.RegisterSpawn("tc1", Unit);

        runtime.CheckSpawnResult("tc1", "anything").HasSchema.Should().BeFalse();
    }

    [Fact]
    public void CheckSpawnResult_UnknownToolCall_ReportsNoSchema()
    {
        var runtime = RuntimeAtAnalyze(Phase4Fixtures.SingleTask(maxValidationRetries: 0));

        runtime.CheckSpawnResult("ghost", "{}").HasSchema.Should().BeFalse();
    }
}

/// <summary>
///     Unit-level contract for <see cref="WorkflowJsonRepairer"/>: it hands the raw reply + schema to a cheap
///     LLM and returns the (trimmed) rewrite, degrading to <c>null</c> — never throwing — whenever the agent
///     yields no usable text or faults, so the caller can always fall back to the deterministic failure path.
///     Cancellation is the one exception that propagates.
/// </summary>
public class WorkflowJsonRepairerTests
{
    private const string SchemaJson = """{ "type": "object", "required": ["summary"] }""";

    private static WorkflowJsonRepairer RepairerFrom(
        Func<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken, Task<IEnumerable<IMessage>>> handler
    )
    {
        var mock = new Mock<IAgent>();
        mock.Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(handler);
        return new WorkflowJsonRepairer(mock.Object, "repair-model");
    }

    private static WorkflowJsonRepairer RepairerReplying(params IMessage[] reply) =>
        RepairerFrom((_, _, _) => Task.FromResult<IEnumerable<IMessage>>(reply));

    [Fact]
    public async Task TryRepairAsync_SendsTheResolvedRepairModel()
    {
        GenerateReplyOptions? captured = null;
        var mock = new Mock<IAgent>();
        mock.Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>((_, options, _) => captured = options)
            .ReturnsAsync([new TextMessage { Text = "{}", Role = Role.Assistant }]);
        var repairer = new WorkflowJsonRepairer(mock.Object, "cheap-model");

        _ = await repairer.TryRepairAsync("broken", SchemaJson, CancellationToken.None);

        captured!.ModelId.Should().Be("cheap-model");
    }

    [Fact]
    public async Task TryRepairAsync_TrimsTheRewrittenText()
    {
        var repairer = RepairerReplying(
            new TextMessage { Text = "  { \"summary\": \"ok\" }\n", Role = Role.Assistant }
        );

        var result = await repairer.TryRepairAsync("broken", SchemaJson, CancellationToken.None);

        result.Should().Be("{ \"summary\": \"ok\" }");
    }

    [Fact]
    public async Task TryRepairAsync_ReturnsNull_WhenReplyHasNoText()
    {
        var repairer = RepairerReplying();

        var result = await repairer.TryRepairAsync("broken", SchemaJson, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryRepairAsync_ReturnsNull_WhenReplyTextIsWhitespace()
    {
        var repairer = RepairerReplying(new TextMessage { Text = "   \n", Role = Role.Assistant });

        var result = await repairer.TryRepairAsync("broken", SchemaJson, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryRepairAsync_ReturnsNull_WhenAgentThrows()
    {
        var repairer = RepairerFrom((_, _, _) => throw new InvalidOperationException("transport blew up"));

        var result = await repairer.TryRepairAsync("broken", SchemaJson, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryRepairAsync_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var repairer = RepairerFrom((_, _, ct) => throw new OperationCanceledException(ct));

        var act = () => repairer.TryRepairAsync("broken", SchemaJson, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
