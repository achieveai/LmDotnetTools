using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Utility class for measuring and logging operation performance.
/// </summary>
public sealed class PerformanceLogger : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _operationName;
    private readonly Stopwatch _stopwatch;
    private readonly LogLevel _logLevel;
    private readonly EventId _eventId;
    private readonly Dictionary<string, object?> _properties;
    private bool _disposed;

    private PerformanceLogger(ILogger logger, string operationName, LogLevel logLevel, EventId eventId)
    {
        _logger = logger;
        _operationName = operationName;
        _logLevel = logLevel;
        _eventId = eventId;
        _properties = [];
        _stopwatch = Stopwatch.StartNew();
    }

    /// <summary>
    /// Creates a new performance logger for measuring operation duration.
    /// </summary>
    /// <param name="logger">The logger to use for output</param>
    /// <param name="operationName">Name of the operation being measured</param>
    /// <param name="logLevel">Log level to use (default: Debug)</param>
    /// <param name="eventId">Event ID to use (default: PerformanceMetrics)</param>
    /// <returns>A disposable performance logger</returns>
    public static PerformanceLogger Start(
        ILogger logger,
        string operationName,
        LogLevel logLevel = LogLevel.Debug,
        EventId? eventId = null
    )
    {
        return new PerformanceLogger(logger, operationName, logLevel, eventId ?? LogEventIds.PerformanceMetrics);
    }

    /// <summary>
    /// Adds a property to be included in the performance log.
    /// </summary>
    /// <param name="key">Property key</param>
    /// <param name="value">Property value</param>
    /// <returns>This instance for method chaining</returns>
    public PerformanceLogger WithProperty(string key, object? value)
    {
        _properties[key] = value;
        return this;
    }

    /// <summary>
    /// Adds token count information to the performance log.
    /// </summary>
    /// <param name="tokenCount">Number of tokens processed</param>
    /// <returns>This instance for method chaining</returns>
    public PerformanceLogger WithTokens(int tokenCount)
    {
        _properties["Tokens"] = tokenCount;
        return this;
    }

    /// <summary>
    /// Adds cost information to the performance log.
    /// </summary>
    /// <param name="cost">Cost of the operation</param>
    /// <returns>This instance for method chaining</returns>
    public PerformanceLogger WithCost(decimal cost)
    {
        _properties["Cost"] = cost;
        return this;
    }

    /// <summary>
    /// Adds completion ID to the performance log.
    /// </summary>
    /// <param name="completionId">Completion ID</param>
    /// <returns>This instance for method chaining</returns>
    public PerformanceLogger WithCompletionId(string? completionId)
    {
        _properties["CompletionId"] = completionId;
        return this;
    }

    /// <summary>
    /// Adds model information to the performance log.
    /// </summary>
    /// <param name="modelId">Model ID</param>
    /// <returns>This instance for method chaining</returns>
    public PerformanceLogger WithModel(string modelId)
    {
        _properties["Model"] = modelId;
        return this;
    }

    /// <summary>
    /// Gets the current elapsed time in milliseconds.
    /// </summary>
    public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;

    /// <summary>
    /// Stops the timer and logs the performance metrics.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _stopwatch.Stop();

        if (_logger.IsEnabled(_logLevel))
        {
            var duration = _stopwatch.ElapsedMilliseconds;

            // Build the log message with all properties
            var messageBuilder = new List<string> { "Operation={Operation}", "Duration={Duration}ms" };
            var values = new List<object?> { _operationName, duration };

            foreach (var (key, value) in _properties)
            {
                messageBuilder.Add($"{key}={{{key}}}");
                values.Add(value);
            }

            var message = $"Performance metrics: {string.Join(", ", messageBuilder)}";

            _logger.Log(_logLevel, _eventId, message, [.. values]);
        }

        _disposed = true;
    }
}

/// <summary>
/// Utility class for calculating and logging tokens per second metrics.
/// </summary>
public static class TokenMetrics
{
    /// <summary>
    /// Calculates tokens per second based on token count and duration.
    /// </summary>
    /// <param name="tokenCount">Number of tokens processed</param>
    /// <param name="durationMs">Duration in milliseconds</param>
    /// <returns>Tokens per second</returns>
    public static double CalculateTokensPerSecond(int tokenCount, long durationMs)
    {
        return durationMs <= 0 ? 0 : (double)tokenCount / (durationMs / 1000.0);
    }

    /// <summary>
    /// Logs tokens per second metrics.
    /// </summary>
    /// <param name="logger">Logger to use</param>
    /// <param name="tokenCount">Number of tokens processed</param>
    /// <param name="durationMs">Duration in milliseconds</param>
    /// <param name="completionId">Optional completion ID</param>
    public static void LogTokensPerSecond(ILogger logger, int tokenCount, long durationMs, string? completionId = null)
    {
        var tokensPerSecond = CalculateTokensPerSecond(tokenCount, durationMs);

        logger.LogDebug(
            LogEventIds.TokensPerSecond,
            "Tokens per second: CompletionId={CompletionId}, Tokens={Tokens}, Duration={Duration}ms, TokensPerSecond={TokensPerSecond:F2}",
            completionId,
            tokenCount,
            durationMs,
            tokensPerSecond
        );
    }
}

/// <summary>
/// Utility class for measuring time to first token in streaming scenarios.
/// </summary>
public sealed class TimeToFirstTokenLogger : IDisposable
{
    private readonly ILogger _logger;
    private readonly Stopwatch _stopwatch;
    private readonly string? _completionId;
    private bool _disposed;

    /// <summary>
    /// Creates a new time to first token logger.
    /// </summary>
    /// <param name="logger">Logger to use</param>
    /// <param name="completionId">Optional completion ID</param>
    public TimeToFirstTokenLogger(ILogger logger, string? completionId = null)
    {
        _logger = logger;
        _completionId = completionId;
        _stopwatch = Stopwatch.StartNew();
    }

    /// <summary>
    /// Records that the first token has been received and logs the time.
    /// </summary>
    public void RecordFirstToken()
    {
        if (FirstTokenReceived)
        {
            return;
        }

        FirstTokenReceived = true;
        var timeToFirstToken = _stopwatch.ElapsedMilliseconds;

        _logger.LogDebug(
            LogEventIds.TimeToFirstToken,
            "Time to first token: CompletionId={CompletionId}, TimeToFirstToken={TimeToFirstToken}ms",
            _completionId,
            timeToFirstToken
        );
    }

    /// <summary>
    /// Gets the elapsed time since creation.
    /// </summary>
    public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;

    /// <summary>
    /// Gets whether the first token has been received.
    /// </summary>
    public bool FirstTokenReceived { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _stopwatch.Stop();
        _disposed = true;
    }
}
