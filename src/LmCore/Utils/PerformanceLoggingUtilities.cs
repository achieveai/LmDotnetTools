using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Utilities for measuring and logging performance metrics in the LmDotnetTools library.
/// </summary>
public static class PerformanceLoggingUtilities
{
    #region Operation Duration Utilities

    /// <summary>
    /// Measures the duration of an operation and logs the result.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="logger">The logger instance.</param>
    /// <param name="operationName">The name of the operation being measured.</param>
    /// <param name="operation">The operation to measure.</param>
    /// <param name="logLevel">The log level to use (default: Debug).</param>
    /// <returns>The result of the operation.</returns>
    public static T MeasureAndLogOperation<T>(
        ILogger logger,
        string operationName,
        Func<T> operation,
        LogLevel logLevel = LogLevel.Debug
    )
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = operation();
            stopwatch.Stop();

            logger.Log(
                logLevel,
                LogEventIds.OperationDurationMetrics,
                "Operation completed: Name={OperationName}, Duration={Duration}ms",
                operationName,
                stopwatch.ElapsedMilliseconds
            );

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(
                ex,
                "Operation failed: Name={OperationName}, Duration={Duration}ms",
                operationName,
                stopwatch.ElapsedMilliseconds
            );
            throw;
        }
    }

    /// <summary>
    /// Measures the duration of an async operation and logs the result.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="logger">The logger instance.</param>
    /// <param name="operationName">The name of the operation being measured.</param>
    /// <param name="operation">The async operation to measure.</param>
    /// <param name="logLevel">The log level to use (default: Debug).</param>
    /// <returns>The result of the operation.</returns>
    public static async Task<T> MeasureAndLogOperationAsync<T>(
        ILogger logger,
        string operationName,
        Func<Task<T>> operation,
        LogLevel logLevel = LogLevel.Debug
    )
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await operation();
            stopwatch.Stop();

            logger.Log(
                logLevel,
                LogEventIds.OperationDurationMetrics,
                "Async operation completed: Name={OperationName}, Duration={Duration}ms",
                operationName,
                stopwatch.ElapsedMilliseconds
            );

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(
                ex,
                "Async operation failed: Name={OperationName}, Duration={Duration}ms",
                operationName,
                stopwatch.ElapsedMilliseconds
            );
            throw;
        }
    }

    /// <summary>
    /// Measures the duration of an async operation without return value and logs the result.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="operationName">The name of the operation being measured.</param>
    /// <param name="operation">The async operation to measure.</param>
    /// <param name="logLevel">The log level to use (default: Debug).</param>
    public static async Task MeasureAndLogOperationAsync(
        ILogger logger,
        string operationName,
        Func<Task> operation,
        LogLevel logLevel = LogLevel.Debug
    )
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await operation();
            stopwatch.Stop();

            logger.Log(
                logLevel,
                LogEventIds.OperationDurationMetrics,
                "Async operation completed: Name={OperationName}, Duration={Duration}ms",
                operationName,
                stopwatch.ElapsedMilliseconds
            );
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(
                ex,
                "Async operation failed: Name={OperationName}, Duration={Duration}ms",
                operationName,
                stopwatch.ElapsedMilliseconds
            );
            throw;
        }
    }

    /// <summary>
    /// Creates a disposable timer that logs the duration when disposed.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="operationName">The name of the operation being measured.</param>
    /// <param name="logLevel">The log level to use (default: Debug).</param>
    /// <returns>A disposable timer.</returns>
    public static IDisposable CreateOperationTimer(
        ILogger logger,
        string operationName,
        LogLevel logLevel = LogLevel.Debug
    )
    {
        return new OperationTimer(logger, operationName, logLevel);
    }

    #endregion

    #region Token Rate Calculation Utilities

    /// <summary>
    /// Calculates and logs tokens per second metrics.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="totalTokens">The total number of tokens processed.</param>
    /// <param name="durationMs">The duration in milliseconds.</param>
    /// <param name="operationName">The name of the operation (optional).</param>
    /// <returns>The calculated tokens per second rate.</returns>
    public static double CalculateAndLogTokensPerSecond(
        ILogger logger,
        int totalTokens,
        long durationMs,
        string? operationName = null
    )
    {
        if (durationMs <= 0)
        {
            logger.LogWarning(
                "Invalid duration for tokens per second calculation: {Duration}ms",
                durationMs
            );
            return 0;
        }

        var tokensPerSecond = (totalTokens * 1000.0) / durationMs;

        logger.LogDebug(
            LogEventIds.TokensPerSecondMetrics,
            "Tokens per second calculated: Operation={Operation}, TotalTokens={TotalTokens}, Duration={Duration}ms, TokensPerSecond={TokensPerSecond:F2}",
            operationName ?? "Unknown",
            totalTokens,
            durationMs,
            tokensPerSecond
        );

        return tokensPerSecond;
    }

    /// <summary>
    /// Calculates tokens per second without logging.
    /// </summary>
    /// <param name="totalTokens">The total number of tokens processed.</param>
    /// <param name="durationMs">The duration in milliseconds.</param>
    /// <returns>The calculated tokens per second rate.</returns>
    public static double CalculateTokensPerSecond(int totalTokens, long durationMs)
    {
        return durationMs <= 0 ? 0 : (totalTokens * 1000.0) / durationMs;
    }

    /// <summary>
    /// Logs comprehensive token metrics including rates and efficiency.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="promptTokens">The number of prompt tokens.</param>
    /// <param name="completionTokens">The number of completion tokens.</param>
    /// <param name="totalDurationMs">The total operation duration in milliseconds.</param>
    /// <param name="timeToFirstTokenMs">The time to first token in milliseconds (optional).</param>
    /// <param name="operationName">The name of the operation (optional).</param>
    public static void LogComprehensiveTokenMetrics(
        ILogger logger,
        int? promptTokens,
        int? completionTokens,
        long totalDurationMs,
        long? timeToFirstTokenMs = null,
        string? operationName = null
    )
    {
        var totalTokens = (promptTokens ?? 0) + (completionTokens ?? 0);
        var tokensPerSecond =
            totalTokens > 0 ? CalculateTokensPerSecond(totalTokens, totalDurationMs) : 0;

        logger.LogDebug(
            LogEventIds.TokenMetrics,
            "Comprehensive token metrics: Operation={Operation}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, TotalTokens={TotalTokens}, Duration={Duration}ms, TimeToFirstToken={TimeToFirstToken}ms, TokensPerSecond={TokensPerSecond:F2}",
            operationName ?? "Unknown",
            promptTokens,
            completionTokens,
            totalTokens,
            totalDurationMs,
            timeToFirstTokenMs,
            tokensPerSecond
        );
    }

    #endregion

    #region Memory Usage Utilities

    /// <summary>
    /// Measures and logs current memory usage for a component.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="componentName">The name of the component.</param>
    /// <param name="operation">The operation that triggered the measurement.</param>
    /// <returns>The current memory usage in bytes.</returns>
    public static long MeasureAndLogMemoryUsage(
        ILogger logger,
        string componentName,
        string operation
    )
    {
        var memoryUsage = GC.GetTotalMemory(false);

        logger.LogDebug(
            LogEventIds.MemoryMetrics,
            "Memory usage measured: Component={Component}, Operation={Operation}, MemoryUsage={MemoryUsage} bytes ({MemoryUsageMB:F2} MB)",
            componentName,
            operation,
            memoryUsage,
            memoryUsage / (1024.0 * 1024.0)
        );

        return memoryUsage;
    }

    /// <summary>
    /// Measures memory usage before and after an operation and logs the difference.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="logger">The logger instance.</param>
    /// <param name="componentName">The name of the component.</param>
    /// <param name="operationName">The name of the operation.</param>
    /// <param name="operation">The operation to measure.</param>
    /// <param name="forceGC">Whether to force garbage collection before measurement (default: false).</param>
    /// <returns>The result of the operation.</returns>
    public static T MeasureMemoryImpact<T>(
        ILogger logger,
        string componentName,
        string operationName,
        Func<T> operation,
        bool forceGC = false
    )
    {
        if (forceGC)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        var memoryBefore = GC.GetTotalMemory(false);
        var result = operation();
        var memoryAfter = GC.GetTotalMemory(false);
        var memoryDelta = memoryAfter - memoryBefore;

        logger.LogDebug(
            LogEventIds.MemoryMetrics,
            "Memory impact measured: Component={Component}, Operation={Operation}, MemoryBefore={MemoryBefore} bytes, MemoryAfter={MemoryAfter} bytes, MemoryDelta={MemoryDelta} bytes ({MemoryDeltaMB:F2} MB)",
            componentName,
            operationName,
            memoryBefore,
            memoryAfter,
            memoryDelta,
            memoryDelta / (1024.0 * 1024.0)
        );

        return result;
    }

    #endregion

    #region Cache Statistics Utilities

    /// <summary>
    /// Logs cache hit statistics.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="cacheType">The type of cache.</param>
    /// <param name="key">The cache key.</param>
    /// <param name="retrievalTimeMs">The time taken to retrieve from cache in milliseconds.</param>
    public static void LogCacheHit(
        ILogger logger,
        string cacheType,
        string key,
        long? retrievalTimeMs = null
    )
    {
        logger.LogDebug(
            LogEventIds.CacheMetrics,
            "Cache hit: Type={CacheType}, Key={Key}, RetrievalTime={RetrievalTime}ms",
            cacheType,
            key,
            retrievalTimeMs
        );
    }

    /// <summary>
    /// Logs cache miss statistics.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="cacheType">The type of cache.</param>
    /// <param name="key">The cache key.</param>
    /// <param name="reason">The reason for the cache miss (optional).</param>
    public static void LogCacheMiss(
        ILogger logger,
        string cacheType,
        string key,
        string? reason = null
    )
    {
        logger.LogDebug(
            LogEventIds.CacheMetrics,
            "Cache miss: Type={CacheType}, Key={Key}, Reason={Reason}",
            cacheType,
            key,
            reason ?? "Not found"
        );
    }

    /// <summary>
    /// Logs cache set operations.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="cacheType">The type of cache.</param>
    /// <param name="key">The cache key.</param>
    /// <param name="valueSize">The size of the cached value in bytes (optional).</param>
    /// <param name="setTimeMs">The time taken to set the cache value in milliseconds (optional).</param>
    public static void LogCacheSet(
        ILogger logger,
        string cacheType,
        string key,
        long? valueSize = null,
        long? setTimeMs = null
    )
    {
        logger.LogDebug(
            LogEventIds.CacheMetrics,
            "Cache set: Type={CacheType}, Key={Key}, ValueSize={ValueSize} bytes, SetTime={SetTime}ms",
            cacheType,
            key,
            valueSize,
            setTimeMs
        );
    }

    /// <summary>
    /// Logs comprehensive cache statistics.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="cacheType">The type of cache.</param>
    /// <param name="totalRequests">The total number of cache requests.</param>
    /// <param name="hits">The number of cache hits.</param>
    /// <param name="misses">The number of cache misses.</param>
    /// <param name="averageRetrievalTimeMs">The average retrieval time in milliseconds.</param>
    public static void LogCacheStatistics(
        ILogger logger,
        string cacheType,
        int totalRequests,
        int hits,
        int misses,
        double? averageRetrievalTimeMs = null
    )
    {
        var hitRate = totalRequests > 0 ? (hits * 100.0) / totalRequests : 0;

        logger.LogInformation(
            LogEventIds.CacheMetrics,
            "Cache statistics: Type={CacheType}, TotalRequests={TotalRequests}, Hits={Hits}, Misses={Misses}, HitRate={HitRate:F1}%, AverageRetrievalTime={AverageRetrievalTime:F2}ms",
            cacheType,
            totalRequests,
            hits,
            misses,
            hitRate,
            averageRetrievalTimeMs
        );
    }

    #endregion

    #region Streaming Performance Utilities

    /// <summary>
    /// Tracks streaming performance metrics and logs them.
    /// </summary>
    public class StreamingMetricsTracker : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _operationName;
        private readonly Stopwatch _totalStopwatch;
        private readonly Stopwatch _firstTokenStopwatch;
        private int _chunkCount;
        private int _totalTokens;
        private bool _firstTokenReceived;
        private bool _disposed;

        public StreamingMetricsTracker(ILogger logger, string operationName)
        {
            _logger = logger;
            _operationName = operationName;
            _totalStopwatch = Stopwatch.StartNew();
            _firstTokenStopwatch = Stopwatch.StartNew();
        }

        /// <summary>
        /// Records a chunk received during streaming.
        /// </summary>
        /// <param name="tokenCount">The number of tokens in this chunk.</param>
        public void RecordChunk(int tokenCount = 0)
        {
            _chunkCount++;
            _totalTokens += tokenCount;

            if (!_firstTokenReceived && tokenCount > 0)
            {
                _firstTokenReceived = true;
                _firstTokenStopwatch.Stop();
            }
        }

        /// <summary>
        /// Records the first token received.
        /// </summary>
        public void RecordFirstToken()
        {
            if (!_firstTokenReceived)
            {
                _firstTokenReceived = true;
                _firstTokenStopwatch.Stop();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _totalStopwatch.Stop();

            var timeToFirstToken = _firstTokenReceived
                ? _firstTokenStopwatch.ElapsedMilliseconds
                : (long?)null;
            var tokensPerSecond =
                _totalTokens > 0 && _totalStopwatch.ElapsedMilliseconds > 0
                    ? CalculateTokensPerSecond(_totalTokens, _totalStopwatch.ElapsedMilliseconds)
                    : (double?)null;

            _logger.LogDebug(
                LogEventIds.StreamingMetrics,
                "Streaming completed: Operation={Operation}, TotalChunks={TotalChunks}, TotalTokens={TotalTokens}, TotalDuration={TotalDuration}ms, TimeToFirstToken={TimeToFirstToken}ms, TokensPerSecond={TokensPerSecond:F2}",
                _operationName,
                _chunkCount,
                _totalTokens,
                _totalStopwatch.ElapsedMilliseconds,
                timeToFirstToken,
                tokensPerSecond
            );

            _disposed = true;
        }
    }

    /// <summary>
    /// Creates a streaming metrics tracker.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="operationName">The name of the streaming operation.</param>
    /// <returns>A disposable streaming metrics tracker.</returns>
    public static StreamingMetricsTracker CreateStreamingTracker(
        ILogger logger,
        string operationName
    )
    {
        return new StreamingMetricsTracker(logger, operationName);
    }

    #endregion

    #region Serialization Performance Utilities

    /// <summary>
    /// Measures and logs serialization performance.
    /// </summary>
    /// <typeparam name="T">The type being serialized.</typeparam>
    /// <param name="logger">The logger instance.</param>
    /// <param name="objectType">The type name of the object being serialized.</param>
    /// <param name="serializer">The serialization function.</param>
    /// <param name="obj">The object to serialize.</param>
    /// <returns>The serialized result.</returns>
    public static string MeasureAndLogSerialization<T>(
        ILogger logger,
        string objectType,
        Func<T, string> serializer,
        T obj
    )
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = serializer(obj);
            stopwatch.Stop();

            logger.LogDebug(
                LogEventIds.SerializationMetrics,
                "Serialization completed: Type={ObjectType}, Duration={Duration}ms, ResultSize={ResultSize} chars",
                objectType,
                stopwatch.ElapsedMilliseconds,
                result.Length
            );

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(
                ex,
                "Serialization failed: Type={ObjectType}, Duration={Duration}ms",
                objectType,
                stopwatch.ElapsedMilliseconds
            );
            throw;
        }
    }

    /// <summary>
    /// Measures and logs deserialization performance.
    /// </summary>
    /// <typeparam name="T">The type being deserialized.</typeparam>
    /// <param name="logger">The logger instance.</param>
    /// <param name="objectType">The type name of the object being deserialized.</param>
    /// <param name="deserializer">The deserialization function.</param>
    /// <param name="data">The data to deserialize.</param>
    /// <returns>The deserialized object.</returns>
    public static T MeasureAndLogDeserialization<T>(
        ILogger logger,
        string objectType,
        Func<string, T> deserializer,
        string data
    )
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = deserializer(data);
            stopwatch.Stop();

            logger.LogDebug(
                LogEventIds.SerializationMetrics,
                "Deserialization completed: Type={ObjectType}, Duration={Duration}ms, InputSize={InputSize} chars",
                objectType,
                stopwatch.ElapsedMilliseconds,
                data.Length
            );

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(
                ex,
                "Deserialization failed: Type={ObjectType}, Duration={Duration}ms, InputSize={InputSize} chars",
                objectType,
                stopwatch.ElapsedMilliseconds,
                data.Length
            );
            throw;
        }
    }

    #endregion

    #region Private Helper Classes

    private class OperationTimer : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _operationName;
        private readonly LogLevel _logLevel;
        private readonly Stopwatch _stopwatch;
        private bool _disposed;

        public OperationTimer(ILogger logger, string operationName, LogLevel logLevel)
        {
            _logger = logger;
            _operationName = operationName;
            _logLevel = logLevel;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _stopwatch.Stop();
            _logger.Log(
                _logLevel,
                LogEventIds.OperationDurationMetrics,
                "Operation timer completed: Name={OperationName}, Duration={Duration}ms",
                _operationName,
                _stopwatch.ElapsedMilliseconds
            );

            _disposed = true;
        }
    }

    #endregion
}
