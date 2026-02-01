using System.Runtime.CompilerServices;
using Serilog;

namespace AchieveAi.LmDotnetTools.LmTestUtils.Logging;

/// <summary>
///     Module initializer that sets up test logging when the assembly is loaded.
///     This runs before any tests execute, ensuring logging is configured.
/// </summary>
public static class TestLoggingModuleInitializer
{
    /// <summary>
    ///     Indicates whether the module has been initialized.
    /// </summary>
    public static bool IsInitialized { get; private set; }

    /// <summary>
    ///     Initializes test logging infrastructure when the module loads.
    ///     This method runs automatically before any code in the assembly executes.
    /// </summary>
    [ModuleInitializer]
    public static void Initialize()
    {
        if (IsInitialized)
        {
            return;
        }

        // Initialize logging with archiving enabled and 7-day retention
        TestLoggingConfiguration.InitializeOnce(archivePrevious: true, retentionDays: 7);

        Log.Information(
            "=== Test Logging Module Initialized === RunId: {RunId}, LogPath: {LogPath}",
            TestLoggingConfiguration.CurrentRunId,
            TestLoggingConfiguration.LogFilePath);

        IsInitialized = true;

        // Register for process exit to flush logs
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Log.Information("=== Test Logging Module Shutting Down === RunId: {RunId}",
                TestLoggingConfiguration.CurrentRunId);
            TestLoggingConfiguration.Shutdown();
        };
    }
}

/// <summary>
///     Optional class-level fixture for tests that need explicit logging lifecycle control.
///     Use this when you need to ensure logging is initialized before specific tests.
/// </summary>
/// <remarks>
///     <para>
///         Usage with xUnit IClassFixture:
///     </para>
///     <code>
/// public class MyTests : IClassFixture&lt;TestLoggingFixture&gt;
/// {
///     public MyTests(TestLoggingFixture fixture, ITestOutputHelper output)
///     {
///         // Logging is guaranteed to be initialized
///     }
/// }
/// </code>
/// </remarks>
public sealed class TestLoggingFixture : IDisposable
{
    /// <summary>
    ///     Ensures logging is initialized.
    /// </summary>
    public TestLoggingFixture()
    {
        // Module initializer should have already run, but ensure it's initialized
        if (!TestLoggingModuleInitializer.IsInitialized)
        {
            TestLoggingModuleInitializer.Initialize();
        }

        Log.Debug("TestLoggingFixture created");
    }

    /// <summary>
    ///     Cleanup is handled by module shutdown, nothing to do here.
    /// </summary>
    public void Dispose()
    {
        Log.Debug("TestLoggingFixture disposed");
    }
}

