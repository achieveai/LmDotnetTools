using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Unit tests for <c>MultiTurnAgentBase.ResolveBatchParent</c>, the helper that
/// surfaces explicit caller fork intent from a <see cref="QueuedInput"/> batch.
/// </summary>
public class ResolveBatchParentTests
{
    [Fact]
    public void AllNullParents_ReturnsNoFork()
    {
        var harness = new ForkResolveProbe("t");
        var batch = new List<QueuedInput>
        {
            MakeQueued("a", parent: null),
            MakeQueued("b", parent: null),
        };

        var (parent, isFork) = harness.Resolve(batch);

        parent.Should().BeNull();
        isFork.Should().BeFalse();
    }

    [Fact]
    public void EmptyParentString_TreatedAsNull()
    {
        var harness = new ForkResolveProbe("t");
        var batch = new List<QueuedInput>
        {
            MakeQueued("a", parent: string.Empty),
        };

        var (parent, isFork) = harness.Resolve(batch);

        parent.Should().BeNull();
        isFork.Should().BeFalse();
    }

    [Fact]
    public void SingleNonNullParent_ReturnsExplicitFork()
    {
        var harness = new ForkResolveProbe("t");
        var batch = new List<QueuedInput>
        {
            MakeQueued("a", parent: "run-parent-1"),
        };

        var (parent, isFork) = harness.Resolve(batch);

        parent.Should().Be("run-parent-1");
        isFork.Should().BeTrue();
    }

    [Fact]
    public void MixedNullAndNonNull_FirstNonNullWins()
    {
        var harness = new ForkResolveProbe("t");
        var batch = new List<QueuedInput>
        {
            MakeQueued("a", parent: null),
            MakeQueued("b", parent: "run-parent-1"),
            MakeQueued("c", parent: null),
        };

        var (parent, isFork) = harness.Resolve(batch);

        parent.Should().Be("run-parent-1");
        isFork.Should().BeTrue();
    }

    [Fact]
    public void MixedDistinctParents_FirstWins_AndWarningLogged()
    {
        var logger = new CapturingLogger();
        var harness = new ForkResolveProbe("t", logger);

        var batch = new List<QueuedInput>
        {
            MakeQueued("a", parent: "run-parent-1"),
            MakeQueued("b", parent: "run-parent-2"),
            MakeQueued("c", parent: "run-parent-3"),
        };

        var (parent, isFork) = harness.Resolve(batch);

        parent.Should().Be("run-parent-1");
        isFork.Should().BeTrue();
        logger.Warnings.Should().ContainSingle().Which
            .Should().Contain("Mixed ParentRunId values")
            .And.Contain("run-parent-1");
    }

    [Fact]
    public void RepeatedSameParent_NoWarning()
    {
        var logger = new CapturingLogger();
        var harness = new ForkResolveProbe("t", logger);

        var batch = new List<QueuedInput>
        {
            MakeQueued("a", parent: "run-parent-1"),
            MakeQueued("b", parent: "run-parent-1"),
        };

        var (parent, isFork) = harness.Resolve(batch);

        parent.Should().Be("run-parent-1");
        isFork.Should().BeTrue();
        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void EmptyBatch_ReturnsNoFork()
    {
        var harness = new ForkResolveProbe("t");
        var (parent, isFork) = harness.Resolve([]);

        parent.Should().BeNull();
        isFork.Should().BeFalse();
    }

    private static QueuedInput MakeQueued(string receiptId, string? parent)
        => new(
            new UserInput(
                [new TextMessage { Text = "x", Role = Role.User }],
                InputId: receiptId,
                ParentRunId: parent),
            receiptId,
            DateTimeOffset.UtcNow);

    /// <summary>
    /// Test-only subclass that exposes <c>ResolveBatchParent</c> via a public method.
    /// </summary>
    private sealed class ForkResolveProbe : MultiTurnAgentBase
    {
        public ForkResolveProbe(string threadId, ILogger? logger = null)
            : base(threadId, logger: logger)
        {
        }

        public (string? ParentRunId, bool IsExplicitFork) Resolve(IReadOnlyList<QueuedInput> inputs)
            => ResolveBatchParent(inputs);

        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
