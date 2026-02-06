using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Context;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;
using Xunit.Abstractions;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace AchieveAi.LmDotnetTools.LmTestUtils.Logging;

/// <summary>
///     Central configuration for structured test logging with Serilog.
///     Provides consistent logging across all test projects with test name correlation,
///     file archiving, and DuckDB-queryable JSON output.
/// </summary>
public static class TestLoggingConfiguration
{
    private static readonly object _initLock = new();
    private static bool _initialized;
    private static Serilog.Core.Logger? _sharedLogger;

    /// <summary>
    ///     Unique identifier for the current test run (timestamp-based).
    /// </summary>
    public static string CurrentRunId { get; private set; } = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");

    /// <summary>
    ///     Path to the current test log file.
    /// </summary>
    public static string LogFilePath { get; private set; } = string.Empty;

    /// <summary>
    ///     Path to the logs directory.
    /// </summary>
    public static string LogDirectory { get; private set; } = string.Empty;

    /// <summary>
    ///     Initializes the global logging configuration. Called once per test run.
    ///     Archives previous log files and cleans up old archives.
    /// </summary>
    /// <param name="archivePrevious">Whether to archive the previous log file.</param>
    /// <param name="retentionDays">Number of days to retain archived logs.</param>
    public static void InitializeOnce(bool archivePrevious = true, int retentionDays = 7)
    {
        lock (_initLock)
        {
            if (_initialized)
            {
                return;
            }

            CurrentRunId = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");

            // Find the repository root by looking for .logs directory or creating it
            LogDirectory = FindOrCreateLogDirectory();
            LogFilePath = Path.Combine(LogDirectory, "tests.jsonl");

            // Archive previous log file if it exists
            if (archivePrevious && File.Exists(LogFilePath))
            {
                ArchiveLogFile(LogFilePath);
            }

            // Clean up old archives
            CleanupOldArchives(LogDirectory, retentionDays);

            // Configure the shared Serilog logger
            _sharedLogger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithThreadId()
                .Enrich.WithMachineName()
                .Enrich.WithProperty("TestRunId", CurrentRunId)
                .WriteTo.File(
                    new CompactJsonFormatter(),
                    LogFilePath,
                    rollingInterval: RollingInterval.Infinite, // We handle archiving manually
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1))
                .CreateLogger();

            // Set as the global static logger
            Log.Logger = _sharedLogger;

            _initialized = true;

            Log.Information(
                "Test logging initialized. RunId={TestRunId}, LogFile={LogFilePath}",
                CurrentRunId,
                LogFilePath);
        }
    }

    /// <summary>
    ///     Creates an ILoggerFactory configured with the test context.
    ///     Production code using this logger factory will automatically include test correlation properties.
    /// </summary>
    /// <param name="testClass">The test class name.</param>
    /// <param name="testMethod">The test method name.</param>
    /// <param name="testOutput">Optional xUnit test output helper for console output.</param>
    /// <returns>An ILoggerFactory that includes test context in all log entries.</returns>
    public static ILoggerFactory CreateLoggerFactory(
        string testClass,
        string testMethod,
        ITestOutputHelper? testOutput = null)
    {
        // Ensure global logging is initialized
        InitializeOnce();

        // Create a logger configuration that includes xUnit output if provided
        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .Enrich.WithProperty("TestRunId", CurrentRunId)
            .Enrich.WithProperty("testClassName", testClass)
            .Enrich.WithProperty("testCaseName", testMethod)
            .WriteTo.Logger(_sharedLogger!); // Write to shared file logger

        // Add xUnit output sink if provided
        if (testOutput != null)
        {
            _ = loggerConfig.WriteTo.TestOutput(
                testOutput,
                outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] [{testClassName}.{testCaseName}] {SourceContext}{NewLine}    {Message:lj}{NewLine}{Exception}");
        }

        var logger = loggerConfig.CreateLogger();
        return new SerilogLoggerFactory(logger, dispose: true);
    }

    /// <summary>
    ///     Creates a scoped logger for a specific test. The scope ensures all logs
    ///     (including from production code) include the test class and method.
    /// </summary>
    /// <param name="testClass">The test class name.</param>
    /// <param name="testMethod">The test method name.</param>
    /// <returns>A disposable scope that enriches all logs with test context.</returns>
    public static IDisposable BeginTestScope(string testClass, string testMethod)
    {
        // Ensure global logging is initialized
        InitializeOnce();

        // Push properties that will be included in all logs within this scope
        return new CompositeDisposable(
            LogContext.PushProperty("testClassName", testClass),
            LogContext.PushProperty("testCaseName", testMethod));
    }

    /// <summary>
    ///     Gets an ILogger for production code to use during tests.
    ///     When called within a test scope, logs will include test correlation properties.
    /// </summary>
    /// <typeparam name="T">The type to create a logger for.</typeparam>
    /// <returns>An ILogger instance.</returns>
    public static ILogger<T> CreateLogger<T>()
    {
        InitializeOnce();
        return new SerilogLoggerFactory(_sharedLogger!, dispose: false).CreateLogger<T>();
    }

    /// <summary>
    ///     Gets an ILogger for production code to use during tests.
    /// </summary>
    /// <param name="categoryName">The category name for the logger.</param>
    /// <returns>An ILogger instance.</returns>
    public static ILogger CreateLogger(string categoryName)
    {
        InitializeOnce();
        return new SerilogLoggerFactory(_sharedLogger!, dispose: false).CreateLogger(categoryName);
    }

    /// <summary>
    ///     Flushes all pending log entries to disk and closes the logger.
    ///     Called at the end of a test run.
    /// </summary>
    public static void Shutdown()
    {
        Log.CloseAndFlush();
        _initialized = false;
        _sharedLogger = null;
    }

    private static string FindOrCreateLogDirectory()
    {
        // Try to find the repository root by looking for known files
        var currentDir = AppContext.BaseDirectory;
        var searchDir = currentDir;

        // Walk up the directory tree looking for .logs or solution root markers
        for (var i = 0; i < 10; i++)
        {
            var logsDir = Path.Combine(searchDir, ".logs", "tests");
            var solutionFile = Path.Combine(searchDir, "LmDotnetTools.sln");

            if (File.Exists(solutionFile))
            {
                // Found solution root, use .logs/tests
                logsDir = Path.Combine(searchDir, ".logs", "tests");
                _ = Directory.CreateDirectory(logsDir);
                return logsDir;
            }

            var parentDir = Directory.GetParent(searchDir);
            if (parentDir == null)
            {
                break;
            }

            searchDir = parentDir.FullName;
        }

        // Fallback: create in AppContext.BaseDirectory
        var fallbackDir = Path.Combine(currentDir, ".logs", "tests");
        _ = Directory.CreateDirectory(fallbackDir);
        return fallbackDir;
    }

    private static void ArchiveLogFile(string logFilePath)
    {
        try
        {
            var fileInfo = new FileInfo(logFilePath);
            var timestamp = fileInfo.LastWriteTimeUtc.ToString("yyyy-MM-dd_HH-mm-ss");
            var archivePath = Path.Combine(
                Path.GetDirectoryName(logFilePath)!,
                $"tests-{timestamp}.jsonl.gz");

            // Compress the log file
            using (var originalStream = File.OpenRead(logFilePath))
            using (var archiveStream = File.Create(archivePath))
            using (var gzipStream = new GZipStream(archiveStream, CompressionLevel.Optimal))
            {
                originalStream.CopyTo(gzipStream);
            }

            // Delete the original
            File.Delete(logFilePath);
        }
        catch (Exception ex)
        {
            // Log to console if archiving fails - don't block tests
            Console.WriteLine($"[TestLogging] Warning: Failed to archive log file: {ex.Message}");
        }
    }

    private static void CleanupOldArchives(string logDirectory, int retentionDays)
    {
        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
            var archiveFiles = Directory.GetFiles(logDirectory, "tests-*.jsonl.gz");

            foreach (var archiveFile in archiveFiles)
            {
                var fileInfo = new FileInfo(archiveFile);
                if (fileInfo.CreationTimeUtc < cutoffDate)
                {
                    File.Delete(archiveFile);
                }
            }
        }
        catch (Exception ex)
        {
            // Don't block tests if cleanup fails
            Console.WriteLine($"[TestLogging] Warning: Failed to cleanup old archives: {ex.Message}");
        }
    }

    /// <summary>
    ///     Helper class to combine multiple IDisposable instances.
    /// </summary>
    private sealed class CompositeDisposable : IDisposable
    {
        private readonly IDisposable[] _disposables;

        public CompositeDisposable(params IDisposable[] disposables)
        {
            _disposables = disposables;
        }

        public void Dispose()
        {
            // Dispose in reverse order
            for (var i = _disposables.Length - 1; i >= 0; i--)
            {
                _disposables[i].Dispose();
            }
        }
    }
}
