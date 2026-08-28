using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmTestUtils.Logging;

public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Text, Exception? Error)> _entries = [];

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
        _entries.Add((logLevel, formatter(state, exception), exception));
    }

    public int WarningCount(string substring) =>
        _entries.Count(e => e.Level == LogLevel.Warning && e.Text.Contains(substring, StringComparison.Ordinal));

    /// <summary>
    ///     Number of captured entries logged at <paramref name="level"/> whose rendered
    ///     message contains <paramref name="substring"/> (ordinal comparison).
    /// </summary>
    public int CountAtLevel(LogLevel level, string substring) =>
        _entries.Count(e => e.Level == level && e.Text.Contains(substring, StringComparison.Ordinal));

    /// <summary>
    ///     Number of captured entries at <paramref name="level"/> whose logged EXCEPTION — rather than its
    ///     rendered message — carries <paramref name="substring"/> somewhere in its <c>InnerException</c> chain
    ///     (ordinal comparison). Entries logged without an exception never match.
    ///     <para>
    ///     This exists because <see cref="CountAtLevel"/> cannot see it. The default MEL formatter renders the
    ///     message template ALONE and drops the exception, so a call site that stops passing one produces a
    ///     byte-identical rendered string — and a test written against the rendered text goes on passing while
    ///     the operator loses everything the exception was carrying. Where two log lines at a site share a
    ///     template, the exception is often the only thing that distinguishes them.
    ///     </para>
    /// </summary>
    public int CountAtLevelWithExceptionText(LogLevel level, string substring) =>
        _entries.Count(e =>
            e.Level == level && Chain(e.Error).Any(message => message.Contains(substring, StringComparison.Ordinal))
        );

    /// <summary>
    ///     Rendered messages captured at <paramref name="level"/>, in the order they were logged.
    /// </summary>
    /// <remarks>
    ///     Use this when a test needs to assert that ONE line carries several facts.
    ///     <see cref="CountAtLevel"/> can only say that each substring appeared somewhere, which still passes
    ///     when the facts are scattered across unrelated lines — and a diagnostic line's value is precisely
    ///     that a single record ties them together.
    /// </remarks>
    public IReadOnlyList<string> MessagesAtLevel(LogLevel level) =>
        [.. _entries.Where(e => e.Level == level).Select(e => e.Text)];

    private static IEnumerable<string> Chain(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            yield return current.Message;
        }
    }
}
