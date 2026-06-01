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
}
