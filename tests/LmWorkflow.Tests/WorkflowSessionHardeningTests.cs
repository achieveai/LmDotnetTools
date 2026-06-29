using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Session-level hardening: the <see cref="WorkflowSession"/> now forwards an <see cref="ILogger"/> to the
///     runtime (so the best-effort persistence Warning is no longer dead in production), the run handle exposes
///     READ-ONLY host accessors instead of the mutable runtime, and a cancelled run faults
///     <see cref="WorkflowRunHandle.Completion"/> rather than hanging.
/// </summary>
public class WorkflowSessionHardeningTests
{
    // ---- FIX: thread the ILogger through the session -------------------------------------------

    [Fact]
    public async Task StartAsync_WithThrowingStoreAndLogger_LogsPersistenceWarning_RunStillCompletes()
    {
        var store = new ThrowingStore();
        var logger = new CapturingLogger();
        var controller = ScriptedController(DriveMinimalToTerminal);

        await using (
            var handle = await WorkflowSession.StartAsync(
                objective: "drive",
                inputs: null,
                definition: WorkflowJson.Deserialize(WorkflowFixtures.MinimalValid),
                subAgentOptions: EmptyOptions(),
                controllerAgent: controller.Object,
                threadId: "wf-log-thread",
                store: store,
                instanceId: "wf-log-1",
                logger: logger
            )
        )
        {
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            handle.IsComplete.Should().BeTrue();
        }

        // Disposal drained the pending best-effort saves; the throwing store surfaced a Warning with the id.
        logger
            .Entries.Should()
            .Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("wf-log-1"));
    }

    // ---- FIX: read-only host accessors on the run handle ---------------------------------------

    [Fact]
    public async Task RunHandle_ExposesReadOnlyHostAccessors_AfterCompletion()
    {
        var controller = ScriptedController(DriveMinimalToTerminal);

        await using var handle = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: WorkflowJson.Deserialize(WorkflowFixtures.MinimalValid),
            subAgentOptions: EmptyOptions(),
            controllerAgent: controller.Object,
            threadId: "wf-accessor-thread"
        );

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        handle.IsComplete.Should().BeTrue();
        handle.CurrentNodeId.Should().Be("t");
        handle.Outputs.Should().NotBeNull();
        handle.State.Should().NotBeNull();
        handle.Notes.Should().NotBeNull();

        // The accessors hand back deep copies, so a host mutating one cannot corrupt runtime state.
        handle.State["hack"] = JsonValue.Create("x");
        handle.State.Should().NotContainKey("hack");
    }

    // ---- FIX: cooperative cancellation faults Completion ---------------------------------------

    [Fact]
    public async Task CooperativeCancellation_FaultsCompletion_AndIsNotComplete()
    {
        var controller = new Mock<IStreamingAgent>();
        controller
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                (IEnumerable<IMessage> _, GenerateReplyOptions? _, CancellationToken token) =>
                    Task.FromResult(DelayForever(token))
            );

        using var cts = new CancellationTokenSource();
        await using var handle = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: null,
            subAgentOptions: EmptyOptions(),
            controllerAgent: controller.Object,
            threadId: "wf-cancel-thread",
            ct: cts.Token
        );

        cts.Cancel();

        // A short timeout makes a regression (Completion never resolving) fail fast as a TimeoutException
        // rather than hanging CI.
        var awaitCompletion = async () => await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await awaitCompletion.Should().ThrowAsync<OperationCanceledException>();

        handle.IsComplete.Should().BeFalse();
    }

    /// <summary>Routes <c>start → terminal</c> for <see cref="WorkflowFixtures.MinimalValid"/>, then ends the run.</summary>
    private static IMessage DriveMinimalToTerminal(int turn) =>
        turn switch
        {
            1 => ToolCall(
                "SetCurrentNode",
                new JsonObject { ["completedNodeId"] = "s", ["nextNodeId"] = "t" },
                "tc_route"
            ),
            _ => new TextMessage { Text = "Workflow finished.", Role = Role.Assistant },
        };

    private static SubAgentOptions EmptyOptions() =>
        new() { Templates = new Dictionary<string, SubAgentTemplate>() };

    private static Mock<IStreamingAgent> ScriptedController(Func<int, IMessage> script)
    {
        var controller = new Mock<IStreamingAgent>();
        var turn = 0;
        controller
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() => Task.FromResult(ToAsyncEnumerable([script(++turn)])));
        return controller;
    }

    private static ToolCallMessage ToolCall(string functionName, JsonObject args, string toolCallId) =>
        new()
        {
            FunctionName = functionName,
            FunctionArgs = args.ToJsonString(),
            ToolCallId = toolCallId,
            Role = Role.Assistant,
        };

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<IMessage> DelayForever(
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        yield break;
    }

    /// <summary>An <see cref="IWorkflowStore"/> whose <see cref="SaveAsync"/> always throws (a store outage).</summary>
    private sealed class ThrowingStore : IWorkflowStore
    {
        public Task SaveAsync(
            string instanceId,
            WorkflowInstanceSnapshot snapshot,
            CancellationToken ct = default
        ) => throw new InvalidOperationException("simulated store outage");

        public Task<WorkflowInstanceSnapshot?> LoadAsync(
            string instanceId,
            CancellationToken ct = default
        ) => Task.FromResult<WorkflowInstanceSnapshot?>(null);

        public Task DeleteAsync(string instanceId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>A minimal <see cref="ILogger"/> that captures emitted entries for assertion.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
