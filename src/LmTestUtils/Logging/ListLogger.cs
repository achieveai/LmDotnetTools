using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmTestUtils.Logging;

/// <summary>
///     Minimal in-memory <see cref="ILogger"/> that captures emitted entries (level + formatted
///     message) so tests can assert on what was logged (e.g. that a retry warning was emitted).
///     Shared across provider test projects to avoid duplicate definitions.
/// </summary>
public sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    ) => Entries.Add((logLevel, formatter(state, exception)));
}
