using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Capabilities;

/// <summary>A log row: its level, message template and structured properties.</summary>
internal sealed record LogRow(LogLevel Level, string Template, IReadOnlyDictionary<string, object?> Props);

/// <summary>Captures every row with its template and named properties, so tests assert on the structured fields.</summary>
internal sealed class RecordingLogger : ILogger
{
    private const string OriginalFormat = "{OriginalFormat}";

    private readonly ConcurrentQueue<LogRow> _rows = new();

    public IReadOnlyList<LogRow> Rows => [.. _rows];

    /// <summary>Rows whose template starts with <paramref name="prefix"/>.</summary>
    public IReadOnlyList<LogRow> Starting(string prefix) =>
        [.. _rows.Where(r => r.Template.StartsWith(prefix, StringComparison.Ordinal))];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        var props = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
        var template =
            props.FirstOrDefault(p => p.Key == OriginalFormat).Value as string ?? formatter(state, exception);
        _rows.Enqueue(
            new LogRow(
                logLevel,
                template,
                props.Where(p => p.Key != OriginalFormat).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            )
        );
    }
}
