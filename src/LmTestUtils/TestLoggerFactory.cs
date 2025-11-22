using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmTestUtils;

/// <summary>
/// Factory for creating test logger instances
/// Provides consistent ILogger&lt;T&gt; implementations for testing
/// Shared utility for all LmDotnetTools provider testing
/// </summary>
public static class TestLoggerFactory
{
    /// <summary>
    /// Creates a test logger instance that outputs to Debug
    /// </summary>
    /// <typeparam name="T">The type the logger is for</typeparam>
    /// <returns>An ILogger&lt;T&gt; instance for testing</returns>
    public static ILogger<T> CreateLogger<T>()
    {
        return new TestLogger<T>();
    }

    /// <summary>
    /// Creates a test logger instance with custom output action
    /// </summary>
    /// <typeparam name="T">The type the logger is for</typeparam>
    /// <param name="outputAction">Custom action for log output</param>
    /// <returns>An ILogger&lt;T&gt; instance for testing</returns>
    public static ILogger<T> CreateLogger<T>(Action<string> outputAction)
    {
        return new TestLogger<T>(outputAction);
    }

    /// <summary>
    /// Creates a silent test logger that doesn't output anything
    /// Useful for performance-sensitive tests
    /// </summary>
    /// <typeparam name="T">The type the logger is for</typeparam>
    /// <returns>A silent ILogger{T} instance</returns>
    public static ILogger<T> CreateSilentLogger<T>()
    {
        return new SilentTestLogger<T>();
    }

    #region Implementation

    private class TestLogger<T> : ILogger<T>
    {
        private readonly Action<string> _outputAction;

        public TestLogger(Action<string>? outputAction = null)
        {
            _outputAction = outputAction ?? (message => Debug.WriteLine(message));
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            var message = $"[{logLevel}] {formatter(state, exception)}";
            if (exception != null)
            {
                message += $" Exception: {exception}";
            }
            _outputAction(message);
        }
    }

    private class SilentTestLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return false;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            // Silent - no output
        }
    }

    #endregion
}
