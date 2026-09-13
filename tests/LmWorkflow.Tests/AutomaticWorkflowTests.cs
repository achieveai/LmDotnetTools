using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

public class AutomaticWorkflowTests
{
    [Fact]
    public async Task InvocationDeadline_IsPinnedAcrossRestartAndFormatCorrection()
    {
        var store = new InMemoryWorkflowStore();
        var runtime = Create();
        runtime.AutomaticInvocationTimeout = TimeSpan.FromMinutes(5);
        var first = new RecordingInvoker { UnknownTask = "decide" };
        _ = await runtime.RunAutomaticAsync(store, "run", first);
        var original = first.Invocations[1].DeadlineUtc;
        original.Should().NotBeNull();
        var resumed = WorkflowRuntime.FromSnapshot((await store.LoadAsync("run"))!);
        resumed.AutomaticInvocationTimeout = TimeSpan.FromHours(1);
        var continuation = new RecordingInvoker();
        _ = await resumed.RunAutomaticAsync(store, "run", continuation);
        continuation.Reconciled.Single().DeadlineUtc.Should().Be(original);

        var correcting = Create();
        correcting.AutomaticInvocationTimeout = TimeSpan.FromMinutes(5);
        var correction = new RecordingInvoker { InvalidFirstDecision = true };
        _ = await correcting.RunAutomaticAsync(new InMemoryWorkflowStore(), "correction", correction);
        correction.Invocations[2].DeadlineUtc.Should().Be(correction.Invocations[1].DeadlineUtc);
    }

    [Fact]
    public async Task ScriptAgentScript_PassesTypedValues_AndDurablyCompletes()
    {
        var store = new InMemoryWorkflowStore();
        var runtime = Create();
        var invoker = new RecordingInvoker();

        var result = await runtime.RunAutomaticAsync(store, "run", invoker);

        result.Should().Be(WorkflowInvocationStatus.Completed);
        invoker.Invocations.Select(x => x.Task.Id).Should().Equal("prepare", "decide", "retain");
        invoker.Invocations[1].Input["Value"]!.GetValue<int>().Should().Be(7);
        invoker.Invocations[2].Input["Decision"]!.GetValue<bool>().Should().BeTrue();
        (await store.LoadAsync("run"))!.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task UnknownInvocation_ResumesByReconciliation_WithoutRepeatingScript()
    {
        var store = new InMemoryWorkflowStore();
        var first = new RecordingInvoker { UnknownTask = "decide" };
        (await Create().RunAutomaticAsync(store, "run", first)).Should().Be(WorkflowInvocationStatus.Unknown);
        var saved = (await store.LoadAsync("run"))!;
        saved.IsComplete.Should().BeFalse();

        var resumed = WorkflowRuntime.FromSnapshot(saved);
        var second = new RecordingInvoker();
        (await resumed.RunAutomaticAsync(store, "run", second)).Should().Be(WorkflowInvocationStatus.Completed);

        second.Reconciled.Should().ContainSingle();
        second.Reconciled[0].InvocationId.Should().Be(first.Invocations[1].InvocationId);
        second.Reconciled[0].SessionId.Should().Be(first.Invocations[1].SessionId);
        second.Invocations.Select(x => x.Task.Id).Should().Equal("retain");
    }

    [Fact]
    public async Task InvalidAgentJson_CorrectsSameParent_WithoutRepeatingPreviousScript()
    {
        var runtime = Create();
        var invoker = new RecordingInvoker { InvalidFirstDecision = true };

        (await runtime.RunAutomaticAsync(new InMemoryWorkflowStore(), "run", invoker))
            .Should()
            .Be(WorkflowInvocationStatus.Completed);

        invoker.Invocations.Select(x => x.Task.Id).Should().Equal("prepare", "decide", "decide", "retain");
        invoker.Invocations[2].IsCorrection.Should().BeTrue();
        invoker.Invocations[2].SessionId.Should().Be(invoker.Invocations[1].SessionId);
        invoker.Invocations[2].InvocationId.Should().NotBe(invoker.Invocations[1].InvocationId);
    }

    [Fact]
    public async Task FailedSaveAfterAction_PreventsNextInvocation_AndLeavesDurableInFlight()
    {
        var store = new FailingStore();
        var invoker = new RecordingInvoker();
        var runtime = Create();

        await runtime.Invoking(r => r.RunAutomaticAsync(store, "run", invoker)).Should().ThrowAsync<IOException>();

        invoker.Invocations.Should().ContainSingle();
        store.Last!.CurrentNodeId.Should().Be("prepare");
        store.Last.Tasks.Single().Status.Should().Be(WorkflowTaskStatus.InFlight);

        store.Fail = false;
        var recovered = WorkflowRuntime.FromSnapshot(store.Last);
        var continuation = new RecordingInvoker();
        (await recovered.RunAutomaticAsync(store, "run", continuation)).Should().Be(WorkflowInvocationStatus.Completed);
        continuation.Reconciled.Single().Task.Id.Should().Be("prepare");
        continuation.Invocations.Select(x => x.Task.Id).Should().Equal("decide", "retain");
    }

    [Fact]
    public async Task AgentPrompt_RendersAuthoredTextBindingsBeforeInvocationAndOnResume()
    {
        var store = new InMemoryWorkflowStore();
        var invoker = new RecordingInvoker { UnknownTask = "decide" };
        await Create("Value: {{state.prepare.Value}}").RunAutomaticAsync(store, "run", invoker);
        invoker.Invocations[1].Task.PromptTemplate.Should().Be("Value: 7");
        var continuation = new RecordingInvoker();
        await WorkflowRuntime
            .FromSnapshot((await store.LoadAsync("run"))!)
            .RunAutomaticAsync(store, "run", continuation);
        continuation.Reconciled.Single().Task.PromptTemplate.Should().Be("Value: 7");
    }

    [Fact]
    public async Task MissingPromptBinding_FailsBeforeAgentInvocation()
    {
        var runtime = Create("Value: {{state.prepare.Missing}}");
        var invoker = new RecordingInvoker();
        await runtime
            .Invoking(r => r.RunAutomaticAsync(new InMemoryWorkflowStore(), "run", invoker))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing*");
        invoker.Invocations.Should().ContainSingle().Which.Task.Id.Should().Be("prepare");
    }

    private static WorkflowRuntime Create(string agentPrompt = "Perform the declared work.")
    {
        var output = JsonNode.Parse(
            """{"type":"object","properties":{"Value":{"type":"integer"},"Decision":{"type":"boolean"}},"required":["Value","Decision"],"additionalProperties":false}"""
        )!;
        var tasks = new[] { "prepare", "decide", "retain" };
        var runtime = WorkflowRuntime.CreateNew();
        runtime.LoadDefinition(
            new WorkflowDefinition
            {
                Objective = "example",
                SchemaVersion = 1,
                StrictContracts = true,
                Nodes =
                [
                    new StartNode
                    {
                        Id = "start",
                        Title = "Start",
                        Next = ["prepare"],
                    },
                    .. tasks.Select(
                        (id, i) =>
                            new ProceduralNode
                            {
                                Id = id,
                                Title = id,
                                Next = [i == 2 ? "done" : tasks[i + 1]],
                                TaskList =
                                [
                                    new WorkflowTask
                                    {
                                        Id = id,
                                        Delegate = i == 1 ? DelegateKind.Agent : DelegateKind.Script,
                                        Script = i == 1 ? null : $".review/{id}.py",
                                        SubagentType = i == 1 ? "general-purpose" : null,
                                        Session = i == 1 ? "reviewer" : null,
                                        PromptTemplate = i == 1 ? agentPrompt : "Prepare or retain.",
                                        Input = i == 0 ? [] : new JsonObject { ["from"] = $"state.{tasks[i - 1]}" },
                                        OutputSchema = output.DeepClone(),
                                        MaxValidationRetries = i == 1 ? 1 : 0,
                                        Writes = new WriteSpec { To = $"state.{id}" },
                                    },
                                ],
                            }
                    ),
                    new TerminalNode { Id = "done", Title = "Done" },
                ],
            }
        );
        return runtime;
    }

    [Fact]
    public async Task ExistingParentBinding_IsUsedAndPreservedAcrossSnapshot()
    {
        var runtime = Create();
        runtime.BindAutomaticSession("reviewer", "existing-review-parent");
        var store = new InMemoryWorkflowStore();
        var invoker = new RecordingInvoker { UnknownTask = "decide" };
        _ = await runtime.RunAutomaticAsync(store, "run", invoker);
        invoker.Invocations[1].SessionId.Should().Be("existing-review-parent");

        var resumed = WorkflowRuntime.FromSnapshot((await store.LoadAsync("run"))!);
        resumed
            .Invoking(r => r.BindAutomaticSession("reviewer", "different-parent"))
            .Should()
            .Throw<InvalidOperationException>();
        var continuation = new RecordingInvoker();
        _ = await resumed.RunAutomaticAsync(store, "run", continuation);
        continuation.Reconciled.Single().SessionId.Should().Be("existing-review-parent");
        resumed.Completion.IsCompletedSuccessfully.Should().BeTrue();
    }

    private sealed class RecordingInvoker : IWorkflowTaskInvoker
    {
        public string? UnknownTask { get; init; }
        public bool InvalidFirstDecision { get; init; }
        public List<WorkflowInvocation> Invocations { get; } = [];
        public List<WorkflowInvocation> Reconciled { get; } = [];

        public Task<WorkflowInvocationResult> InvokeAsync(WorkflowInvocation invocation, CancellationToken ct = default)
        {
            Invocations.Add(invocation);
            return Task.FromResult(
                invocation.Task.Id == UnknownTask ? new WorkflowInvocationResult(WorkflowInvocationStatus.Unknown)
                : InvalidFirstDecision && invocation.Task.Id == "decide" && !invocation.IsCorrection
                    ? new WorkflowInvocationResult(WorkflowInvocationStatus.Completed, "Decision: yes")
                : Success()
            );
        }

        public Task<WorkflowInvocationResult> ReconcileAsync(
            WorkflowInvocation invocation,
            CancellationToken ct = default
        )
        {
            Reconciled.Add(invocation);
            return Task.FromResult(Success());
        }

        private static WorkflowInvocationResult Success() =>
            new(WorkflowInvocationStatus.Completed, """{"Value":7,"Decision":true}""");
    }

    private sealed class FailingStore : IWorkflowStore
    {
        public WorkflowInstanceSnapshot? Last { get; private set; }
        public bool Fail { get; set; } = true;

        public Task SaveAsync(string instanceId, WorkflowInstanceSnapshot snapshot, CancellationToken ct = default)
        {
            if (Fail && snapshot.Tasks.Any(t => t.Status == WorkflowTaskStatus.Validated))
            {
                throw new IOException("Simulated save failure after action");
            }
            Last = snapshot.DeepCopy();
            return Task.CompletedTask;
        }

        public Task<WorkflowInstanceSnapshot?> LoadAsync(string instanceId, CancellationToken ct = default) =>
            Task.FromResult(Last);

        public Task DeleteAsync(string instanceId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
