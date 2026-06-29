using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Proves the persistence hardening: snapshot saves are SERIALIZED per instance so they
///     apply in capture order (a slow, stale save can never overtake a newer one), a store outage is
///     swallowed-but-logged so the run continues, and <see cref="WorkflowRuntime.DrainPersistAsync"/> flushes
///     every pending save.
/// </summary>
public class WorkflowPersistenceOrderingTests
{
    private const string InstanceId = "wf-persist-order";

    [Fact]
    public async Task Saves_AreSerialized_InCaptureOrder_AndNeverOverlap()
    {
        var store = new GatedOrderRecordingStore();
        var runtime = new WorkflowRuntime();
        runtime.AttachStore(store, InstanceId);

        // Queue five state mutations; each captures a snapshot whose state.seq carries the capture order. The
        // first save blocks on the store gate, so a NON-serialized (fire-and-forget) implementation would let
        // saves 2..5 overtake it — the serialized chain keeps them queued behind it.
        for (var seq = 1; seq <= 5; seq++)
        {
            runtime.SetState("state.seq", JsonValue.Create(seq), "set");
        }

        store.ReleaseGate();
        await runtime.DrainPersistAsync();

        store.CompletedSeqs.Should().Equal(1, 2, 3, 4, 5);
        store.OverlapDetected.Should().BeFalse();
    }

    [Fact]
    public async Task StoreOutage_IsSwallowedAndLogged_RunContinues_AndDrainCompletes()
    {
        var logger = new CapturingLogger();
        var store = new GatedOrderRecordingStore(throwOnSeq: 2);
        var runtime = new WorkflowRuntime(schemaValidator: null, logger: logger);
        runtime.AttachStore(store, InstanceId);

        for (var seq = 1; seq <= 4; seq++)
        {
            runtime.SetState("state.seq", JsonValue.Create(seq), "set");
        }

        store.ReleaseGate();

        // Best-effort: the one failed save must not fault the chain, so DrainPersistAsync still completes.
        await runtime.DrainPersistAsync();

        store.ThrowFired.Should().BeTrue();
        // The outage (seq 2) was swallowed; every later save still applied, in order.
        store.CompletedSeqs.Should().Equal(1, 3, 4);
        logger
            .Entries.Should()
            .Contain(e =>
                e.Level == LogLevel.Warning
                && e.Message.Contains(InstanceId)
                && e.Exception is InvalidOperationException
            );
    }

    /// <summary>
    ///     An <see cref="IWorkflowStore"/> that records the order saves COMPLETE (by their <c>state.seq</c>),
    ///     flags any overlapping save, gates the first save open to widen a reordering window, and can throw
    ///     once for a specific sequence to simulate a transient store outage.
    /// </summary>
    private sealed class GatedOrderRecordingStore : IWorkflowStore
    {
        private readonly object _lock = new();
        private readonly List<int> _completedSeqs = [];
        private readonly TaskCompletionSource _gate = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly int? _throwOnSeq;
        private int _concurrent;

        public GatedOrderRecordingStore(int? throwOnSeq = null) => _throwOnSeq = throwOnSeq;

        public bool OverlapDetected { get; private set; }

        public bool ThrowFired { get; private set; }

        public IReadOnlyList<int> CompletedSeqs
        {
            get
            {
                lock (_lock)
                {
                    return [.. _completedSeqs];
                }
            }
        }

        public void ReleaseGate() => _gate.TrySetResult();

        public async Task SaveAsync(
            string instanceId,
            WorkflowInstanceSnapshot snapshot,
            CancellationToken ct = default
        )
        {
            var seq = snapshot.State["seq"]?.GetValue<int>() ?? -1;

            if (Interlocked.Increment(ref _concurrent) > 1)
            {
                OverlapDetected = true;
            }

            try
            {
                if (seq == 1)
                {
                    await _gate.Task.ConfigureAwait(false);
                }

                if (_throwOnSeq == seq)
                {
                    ThrowFired = true;
                    throw new InvalidOperationException($"simulated store outage on seq {seq}");
                }

                lock (_lock)
                {
                    _completedSeqs.Add(seq);
                }
            }
            finally
            {
                _ = Interlocked.Decrement(ref _concurrent);
            }
        }

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
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
