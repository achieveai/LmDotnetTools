using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Triggers;

/// <summary>
/// Provider-level tests for <see cref="WaitToolProvider"/>'s <c>ListWaits</c> and <c>CancelWait</c>
/// handlers against a real <see cref="TriggerRuntime"/>. Invokes the <see cref="FunctionDescriptor"/>
/// handlers directly (bypassing <c>MultiTurnAgentLoop</c>, whose send-guard rejects a new turn while
/// a <c>Wait</c> is parked, making a full loop-driven second-turn test awkward for this purpose) so
/// the JSON args parsing and serialized response shapes are exercised end-to-end.
/// </summary>
public class WaitToolProviderTests : IAsyncLifetime
{
    private TriggerRuntime _runtime = null!;
    private FunctionDescriptor _wait = null!;
    private FunctionDescriptor _cancelWait = null!;
    private FunctionDescriptor _listWaits = null!;

    public Task InitializeAsync()
    {
        _runtime = new TriggerRuntime(
            new TriggerOptions(),
            resolve: (_, _, _, _) => Task.CompletedTask,
            notify: (_, _, _) => Task.CompletedTask);
        _runtime.RegisterBuiltIns();
        _runtime.Register(new TriggerSourceRegistration
        {
            Kind = "manual",
            Description = "test manual trigger (notify-capable)",
            ArgsSchema = "{}",
            Capabilities = ManualTriggerSource.Caps,
            Source = new ManualTriggerSource(),
        });
        var provider = new WaitToolProvider(_runtime);

        var functions = provider.GetFunctions().ToDictionary(f => f.Contract.Name);
        _wait = functions[WaitToolProvider.WaitToolName];
        _cancelWait = functions["CancelWait"];
        _listWaits = functions["ListWaits"];
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _runtime.DisposeAsync();

    private static string WaitArgs() =>
        JsonSerializer.Serialize(new { kind = "timer", args = new { }, timeout = "10m" });

    private async Task<JsonDocument> ListAsync() =>
        JsonDocument.Parse((await _listWaits.Handler("{}", new ToolCallContext(), CancellationToken.None)).ResultText);

    private async Task<ToolHandlerResult> InvokeWaitAsync(string argsJson) =>
        await _wait.Handler(argsJson, new ToolCallContext { ToolCallId = "tc_wait" }, CancellationToken.None);

    private static string ExtractText(ToolHandlerResult result) => result.ResultText;

    [Fact]
    public async Task ListWaits_ShowsArmedWait_ThenEmpty_AfterCancelWait()
    {
        // Arm a long-timeout timer wait directly through the Wait handler; it parks (Deferred) and
        // stays pending for the rest of this test since nothing fires it.
        var waitResult = await _wait.Handler(WaitArgs(), new ToolCallContext { ToolCallId = "tc_1" }, CancellationToken.None);
        waitResult.Should().BeOfType<ToolHandlerResult.Deferred>();

        using (var listed = await ListAsync())
        {
            var waits = listed.RootElement.GetProperty("waits");
            waits.GetArrayLength().Should().Be(1);
            // WaitInfo is serialized with default System.Text.Json naming (no camelCase policy
            // applied), so its properties keep their declared PascalCase names.
            waits[0].GetProperty("WaitId").GetString().Should().Be("tc_1");
            waits[0].GetProperty("Kind").GetString().Should().Be("timer");

            listed.RootElement.GetProperty("registeredKinds")
                .EnumerateArray().Select(e => e.GetString())
                .Should().Contain("timer");
        }

        var cancelArgs = JsonSerializer.Serialize(new { id = "tc_1" });
        var cancelResult = await _cancelWait.Handler(cancelArgs, new ToolCallContext(), CancellationToken.None);
        using (var cancelled = JsonDocument.Parse(cancelResult.ResultText))
        {
            cancelled.RootElement.GetProperty("status").GetString().Should().Be("resolved");
            cancelled.RootElement.GetProperty("cancelled").GetInt32().Should().Be(1);
        }

        using var listedAfter = await ListAsync();
        listedAfter.RootElement.GetProperty("waits").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task CancelWait_ByKind_CancelsAllMatchingWaits()
    {
        await _wait.Handler(WaitArgs(), new ToolCallContext { ToolCallId = "tc_a" }, CancellationToken.None);
        await _wait.Handler(WaitArgs(), new ToolCallContext { ToolCallId = "tc_b" }, CancellationToken.None);

        var cancelArgs = JsonSerializer.Serialize(new { kind = "timer" });
        var cancelResult = await _cancelWait.Handler(cancelArgs, new ToolCallContext(), CancellationToken.None);
        using (var cancelled = JsonDocument.Parse(cancelResult.ResultText))
        {
            cancelled.RootElement.GetProperty("cancelled").GetInt32().Should().Be(2);
        }

        using var listed = await ListAsync();
        listed.RootElement.GetProperty("waits").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task CancelWait_WithNoSelector_IsNoOp()
    {
        // A totally-unfiltered CancelWait matches nothing (TriggerRuntime.Matches requires at
        // least one of id/label/kind) — it must not cancel every armed wait by accident.
        await _wait.Handler(WaitArgs(), new ToolCallContext { ToolCallId = "tc_solo" }, CancellationToken.None);

        var cancelResult = await _cancelWait.Handler("{}", new ToolCallContext(), CancellationToken.None);
        using (var cancelled = JsonDocument.Parse(cancelResult.ResultText))
        {
            cancelled.RootElement.GetProperty("cancelled").GetInt32().Should().Be(0);
        }

        using var listed = await ListAsync();
        listed.RootElement.GetProperty("waits").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task HandleWait_BlockMode_ReturnsDeferred()
    {
        // existing block behavior (regression guard): a timer block wait defers.
        var result = await InvokeWaitAsync("""{"kind":"timer","timeout":"10m"}""");
        result.Should().BeOfType<ToolHandlerResult.Deferred>();
    }

    [Fact]
    public async Task HandleWait_NotifyMode_ReturnsArmedAcknowledgment_NotDeferred()
    {
        // Register a notify-capable source in the test runtime, then:
        var result = await InvokeWaitAsync("""{"kind":"manual","timeout":"1h","mode":"notify","maxFires":2}""");
        result.Should().NotBeOfType<ToolHandlerResult.Deferred>();
        var text = ExtractText(result);
        text.Should().Contain("\"status\":\"armed\"");
        text.Should().Contain("\"mode\":\"notify\"");
    }
}
