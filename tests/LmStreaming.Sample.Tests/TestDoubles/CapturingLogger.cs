using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// Minimal <see cref="ILogger{TCategoryName}"/> test double that captures emitted log
/// entries so tests can assert on level and rendered message. Thread-safe for the
/// concurrent code paths exercised by <c>MultiTurnAgentPool</c>.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public readonly record struct Entry(LogLevel Level, string Message);

    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => [.. _entries];

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        _entries.Enqueue(new Entry(logLevel, formatter(state, exception)));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose() { }
    }
}
