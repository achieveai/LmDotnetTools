using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Focused unit tests for the pure collaborators extracted out of <see cref="WorkflowRuntime"/>:
///     <see cref="WorkflowProjectionBuilder"/> (a pure projection renderer) and
///     <see cref="SnapshotPersister"/> (the serialized best-effort save chain). The runtime's own behavior is
///     covered end-to-end by the existing suite; these prove each isolated seam directly.
/// </summary>
public class WorkflowCollaboratorTests
{
    // --- WorkflowProjectionBuilder (pure) ---------------------------------------------------------------

    [Fact]
    public void Build_MinimalHeader_EmitsCoreKeysAndOmitsOptionalSurfaces()
    {
        var result = WorkflowProjectionBuilder.Build(Inputs(currentNodeId: "start", step: 3));

        result["currentNodeId"]!.GetValue<string>().Should().Be("start");
        result["isComplete"]!.GetValue<bool>().Should().BeFalse();
        result["step"]!.GetValue<int>().Should().Be(3);
        result.Should().ContainKey("visits").And.ContainKey("tasks").And.ContainKey("nextExpectedAction");

        // No active procedural/conditional node and nothing failed/unmatched/included => no optional surfaces.
        foreach (
            var absent in new[]
            {
                "join",
                "onFailure",
                "recommendedBranch",
                "atVisitCeiling",
                "budgetExhausted",
                "taskErrors",
                "state",
                "outputs",
                "notes",
                "unmatched",
            }
        )
        {
            result.ContainsKey(absent).Should().BeFalse($"'{absent}' must be omitted");
        }
    }

    [Fact]
    public void Build_ProceduralAllJoin_AllValidated_IsSatisfiedAndOmitsOnFailure()
    {
        var node = new ProceduralNode { Id = "work", Title = "Work", Next = [] };
        var result = WorkflowProjectionBuilder.Build(
            Inputs(
                currentNodeId: "work",
                activeNode: node,
                statuses: new Dictionary<string, WorkflowTaskStatus>
                {
                    ["work:1:a"] = WorkflowTaskStatus.Validated,
                    ["work:1:b"] = WorkflowTaskStatus.Validated,
                },
                activeUnits:
                [
                    new ProjectionActiveUnit { Name = "work:1:a" },
                    new ProjectionActiveUnit { Name = "work:1:b" },
                ]
            )
        );

        var join = result["join"]!.AsObject();
        join["mode"]!.GetValue<string>().Should().Be("all");
        join["total"]!.GetValue<int>().Should().Be(2);
        join["validated"]!.GetValue<int>().Should().Be(2);
        join["satisfied"]!.GetValue<bool>().Should().BeTrue();
        result.ContainsKey("onFailure").Should().BeFalse();
    }

    [Fact]
    public void Build_ProceduralFailedUnit_SurfacesPerTaskOnFailureRoute()
    {
        var node = new ProceduralNode
        {
            Id = "work",
            Title = "Work",
            Next = [],
            OnFailure = "node-route",
        };
        var result = WorkflowProjectionBuilder.Build(
            Inputs(
                currentNodeId: "work",
                activeNode: node,
                statuses: new Dictionary<string, WorkflowTaskStatus>
                {
                    ["work:1:a"] = WorkflowTaskStatus.Failed,
                },
                activeUnits: [new ProjectionActiveUnit { Name = "work:1:a", OnFailure = "unit-route" }]
            )
        );

        // The per-task route wins over the node route.
        result["onFailure"]!.GetValue<string>().Should().Be("unit-route");
        result["join"]!["failed"]!.GetValue<int>().Should().Be(1);
        result["join"]!["satisfied"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void Build_IncludesChannelsAndDiagnostics_WhenPresentAndRequested()
    {
        var result = WorkflowProjectionBuilder.Build(
            Inputs(
                currentNodeId: "start",
                state: new JsonObject { ["k"] = "v" },
                lastErrors: new Dictionary<string, string> { ["u"] = "boom" },
                unmatched: ["x"],
                projection: "all"
            )
        );

        result.Should().ContainKey("state").And.ContainKey("outputs").And.ContainKey("notes");
        result["state"]!["k"]!.GetValue<string>().Should().Be("v");
        result["taskErrors"]!["u"]!.GetValue<string>().Should().Be("boom");
        result["unmatched"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain("x");
    }

    [Fact]
    public void Build_VisitCeiling_SurfacesWhenEnteredCountReachesMax()
    {
        var node = new ProceduralNode { Id = "work", Title = "Work", Next = [] };
        var result = WorkflowProjectionBuilder.Build(
            Inputs(
                currentNodeId: "work",
                activeNode: node,
                activeNodeMaxVisits: 2,
                activeNodeOnMaxVisits: "cap",
                visits: new Dictionary<string, int> { ["work"] = 2 }
            )
        );

        result["atVisitCeiling"]!.GetValue<bool>().Should().BeTrue();
        result["onMaxVisits"]!.GetValue<string>().Should().Be("cap");
    }

    [Fact]
    public void Build_BudgetSurface_SurfacesEscapeWhenStepReachesMaxBudget()
    {
        var definition = new WorkflowDefinition
        {
            Objective = "o",
            Nodes = [],
            MaxStepBudget = 1,
            OnBudgetExhausted = "esc",
        };
        var result = WorkflowProjectionBuilder.Build(
            Inputs(currentNodeId: "start", step: 1, definition: definition)
        );

        result["budgetExhausted"]!.GetValue<bool>().Should().BeTrue();
        result["onBudgetExhausted"]!.GetValue<string>().Should().Be("esc");
    }

    // --- SnapshotPersister (serialized best-effort saves) ----------------------------------------------

    [Fact]
    public async Task Enqueue_SerializesSaves_InCaptureOrder_WithoutOverlap()
    {
        var store = new OrderTrackingStore();
        var persister = new SnapshotPersister();

        foreach (var id in new[] { "a", "b", "c" })
        {
            persister.Enqueue(store, id, Snapshot(id), logger: null);
        }

        await persister.DrainAsync();

        store.Order.Should().Equal("a", "b", "c");
        store.MaxConcurrent.Should().Be(1);
    }

    [Fact]
    public async Task Enqueue_StoreThrows_IsBestEffort_AndChainKeepsGoing()
    {
        var store = new OrderTrackingStore { ThrowOnInstanceId = "a" };
        var persister = new SnapshotPersister();

        persister.Enqueue(store, "a", Snapshot("a"), logger: null);
        persister.Enqueue(store, "b", Snapshot("b"), logger: null);

        // A persistence fault never faults the chain, so draining never throws and the next save still runs.
        var drain = async () => await persister.DrainAsync();
        await drain.Should().NotThrowAsync();
        store.Order.Should().Equal("b");
    }

    [Fact]
    public void DrainAsync_WithNothingEnqueued_IsAlreadyCompleted()
    {
        new SnapshotPersister().DrainAsync().IsCompletedSuccessfully.Should().BeTrue();
    }

    // --- helpers ----------------------------------------------------------------------------------------

    private static WorkflowInstanceSnapshot Snapshot(string instanceId) =>
        new() { InstanceId = instanceId };

    private static ProjectionInputs Inputs(
        string? currentNodeId = null,
        bool isComplete = false,
        int step = 0,
        WorkflowDefinition? definition = null,
        WorkflowNode? activeNode = null,
        int? activeNodeMaxVisits = null,
        string? activeNodeOnMaxVisits = null,
        IReadOnlyDictionary<string, int>? visits = null,
        IReadOnlyDictionary<string, WorkflowTaskStatus>? statuses = null,
        IReadOnlyList<SpawnUnit>? nextActions = null,
        IReadOnlyList<ProjectionActiveUnit>? activeUnits = null,
        IReadOnlyDictionary<string, string>? lastErrors = null,
        IReadOnlyList<string>? unmatched = null,
        JsonObject? state = null,
        JsonObject? outputs = null,
        JsonObject? notes = null,
        string? projection = null
    ) =>
        new()
        {
            CurrentNodeId = currentNodeId,
            IsComplete = isComplete,
            Step = step,
            Definition = definition,
            ActiveNode = activeNode,
            ActiveNodeMaxVisits = activeNodeMaxVisits,
            ActiveNodeOnMaxVisits = activeNodeOnMaxVisits,
            Visits = visits ?? new Dictionary<string, int>(),
            Statuses = statuses ?? new Dictionary<string, WorkflowTaskStatus>(),
            NextActions = nextActions ?? [],
            ActiveUnits = activeUnits ?? [],
            ContextFactory = () => new BindingContext(),
            LastErrors = lastErrors ?? new Dictionary<string, string>(),
            Unmatched = unmatched ?? [],
            State = state ?? [],
            Outputs = outputs ?? [],
            Notes = notes ?? [],
            Projection = projection,
        };

    /// <summary>A store that records the order saves complete and the peak concurrency observed.</summary>
    private sealed class OrderTrackingStore : IWorkflowStore
    {
        private int _concurrent;

        public List<string> Order { get; } = [];

        public int MaxConcurrent { get; private set; }

        public string? ThrowOnInstanceId { get; init; }

        public async Task SaveAsync(
            string instanceId,
            WorkflowInstanceSnapshot snapshot,
            CancellationToken ct = default
        )
        {
            var current = Interlocked.Increment(ref _concurrent);
            MaxConcurrent = Math.Max(MaxConcurrent, current);
            try
            {
                await Task.Delay(5, ct);
                if (instanceId == ThrowOnInstanceId)
                {
                    throw new InvalidOperationException("store outage");
                }

                Order.Add(instanceId);
            }
            finally
            {
                _ = Interlocked.Decrement(ref _concurrent);
            }
        }

        public Task<WorkflowInstanceSnapshot?> LoadAsync(
            string instanceId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task DeleteAsync(string instanceId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
