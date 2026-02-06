using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.LmTestUtils.Logging;

/// <summary>
///     Base class for tests that need structured logging with automatic test name correlation.
///     All logs (including from production code) will include TestClass and TestMethod properties.
/// </summary>
/// <remarks>
///     <para>
///         <b>Usage:</b>
///     </para>
///     <code>
/// public class MyTests : LoggingTestBase
/// {
///     public MyTests(ITestOutputHelper output) : base(output) { }
///
///     [Fact]
///     public void MyTest()
///     {
///         Logger.LogInformation("Test starting with value {Value}", 42);
///         // Production code logged via LoggerFactory will also include TestClass/TestMethod
///         var service = new MyService(LoggerFactory.CreateLogger&lt;MyService&gt;());
///         service.DoWork();
///     }
/// }
/// </code>
/// </remarks>
public abstract class LoggingTestBase : IDisposable
{
    private readonly IDisposable _logScope;
    private bool _disposed;

    /// <summary>
    ///     Logger for the test class. Includes test context properties.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    ///     Logger factory for creating loggers for production code.
    ///     Loggers created from this factory will include test context properties.
    /// </summary>
    protected ILoggerFactory LoggerFactory { get; }

    /// <summary>
    ///     The xUnit test output helper for writing to test console.
    /// </summary>
    protected ITestOutputHelper Output { get; }

    /// <summary>
    ///     The name of the current test class.
    /// </summary>
    protected string TestClassName { get; }

    /// <summary>
    ///     The name of the current test method (set when a test method is invoked).
    /// </summary>
    protected string TestMethodName { get; private set; }

    /// <summary>
    ///     Initializes the logging infrastructure for the test.
    /// </summary>
    /// <param name="output">The xUnit test output helper.</param>
    /// <param name="testMethodName">
    ///     Optional: The test method name. If not provided, attempts to detect from stack trace.
    /// </param>
    protected LoggingTestBase(ITestOutputHelper output, [CallerMemberName] string? testMethodName = null)
    {
        Output = output;
        TestClassName = GetType().Name;
        TestMethodName = testMethodName ?? DetectTestMethodName() ?? "Unknown";

        // Begin a logging scope that includes test context
        _logScope = TestLoggingConfiguration.BeginTestScope(TestClassName, TestMethodName);

        // Create a logger factory that includes test context in all loggers
        LoggerFactory = TestLoggingConfiguration.CreateLoggerFactory(TestClassName, TestMethodName, output);

        // Create a logger for this test class
        Logger = LoggerFactory.CreateLogger(GetType());

        Logger.LogDebug("Test initialized: {testClassName}.{testCaseName}", TestClassName, TestMethodName);
    }

    /// <summary>
    ///     Sets the current test method name. Call this at the start of each test method
    ///     if the automatic detection doesn't work correctly.
    /// </summary>
    /// <param name="methodName">The test method name.</param>
    protected void SetTestMethod(string methodName)
    {
        TestMethodName = methodName;
        // Update the log context with the new method name
        _ = LogContext.PushProperty("testCaseName", methodName);
    }

    /// <summary>
    ///     Logs a message indicating the test is starting. Call at the beginning of test methods.
    /// </summary>
    /// <param name="additionalContext">Optional additional context to log.</param>
    /// <param name="methodName">The test method name (auto-detected via CallerMemberName).</param>
    protected void LogTestStart(
        object? additionalContext = null,
        [CallerMemberName] string? methodName = null)
    {
        if (methodName != null && methodName != TestMethodName)
        {
            SetTestMethod(methodName);
        }

        if (additionalContext != null)
        {
            Logger.LogInformation(
                "▶ Test starting: {testClassName}.{testCaseName} with context {@Context}",
                TestClassName,
                methodName,
                additionalContext);
        }
        else
        {
            Logger.LogInformation("▶ Test starting: {testClassName}.{testCaseName}", TestClassName, methodName);
        }
    }

    /// <summary>
    ///     Logs a message indicating the test completed. Call at the end of test methods.
    /// </summary>
    /// <param name="methodName">The test method name (auto-detected via CallerMemberName).</param>
    protected void LogTestEnd([CallerMemberName] string? methodName = null)
    {
        Logger.LogInformation("✓ Test completed: {testClassName}.{testCaseName}", TestClassName, methodName);
    }

    /// <summary>
    ///     Logs structured data for debugging. Use this to log important variables
    ///     that help understand control flow during test failures.
    /// </summary>
    /// <param name="name">A descriptive name for the data.</param>
    /// <param name="data">The data to log (will be destructured).</param>
    protected void LogData(string name, object? data)
    {
        Logger.LogDebug("{DataName}: {@Data}", name, data);
    }

    /// <summary>
    ///     Logs a trace message for detailed control flow tracking.
    /// </summary>
    /// <param name="message">The message template.</param>
    /// <param name="args">Message template arguments.</param>
    protected void Trace(string message, params object[] args)
    {
        Logger.LogTrace(message, args);
    }

    private static string? DetectTestMethodName()
    {
        // Walk the stack to find a method with [Fact] or [Theory] attribute
        var stackTrace = new System.Diagnostics.StackTrace();
        foreach (var frame in stackTrace.GetFrames())
        {
            var method = frame.GetMethod();
            if (method == null)
            {
                continue;
            }

            // Check for xUnit test attributes
            var attributes = method.GetCustomAttributes(false);
            foreach (var attr in attributes)
            {
                var attrTypeName = attr.GetType().Name;
                if (attrTypeName is "FactAttribute" or "TheoryAttribute")
                {
                    return method.Name;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Disposes the logging scope.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Disposes managed resources.
    /// </summary>
    /// <param name="disposing">True if called from Dispose(), false if from finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            Logger.LogDebug("Test disposing: {testClassName}.{testCaseName}", TestClassName, TestMethodName);
            _logScope.Dispose();
            LoggerFactory.Dispose();
        }

        _disposed = true;
    }
}
