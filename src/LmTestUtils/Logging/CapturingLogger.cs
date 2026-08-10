using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmTestUtils.Logging;

public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Text)> _entries = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Add((logLevel, formatter(state, exception)));
    }

    public int WarningCount(string substring)
        => _entries.Count(e => e.Level == LogLevel.Warning
            && e.Text.Contains(substring, StringComparison.Ordinal));

    /// <summary>
    ///     Number of captured entries logged at <paramref name="level"/> whose rendered
    ///     message contains <paramref name="substring"/> (ordinal comparison).
    /// </summary>
    public int CountAtLevel(LogLevel level, string substring)
        => _entries.Count(e => e.Level == level
            && e.Text.Contains(substring, StringComparison.Ordinal));

    /// <summary>
    ///     Rendered messages captured at <paramref name="level"/>, in the order they were logged.
    /// </summary>
    /// <remarks>
    ///     Use this when a test needs to assert that ONE line carries several facts.
    ///     <see cref="CountAtLevel"/> can only say that each substring appeared somewhere, which still passes
    ///     when the facts are scattered across unrelated lines — and a diagnostic line's value is precisely
    ///     that a single record ties them together.
    /// </remarks>
    public IReadOnlyList<string> MessagesAtLevel(LogLevel level)
        => [.. _entries.Where(e => e.Level == level).Select(e => e.Text)];
}
