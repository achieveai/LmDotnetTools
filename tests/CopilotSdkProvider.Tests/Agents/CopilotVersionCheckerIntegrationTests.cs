using System.Runtime.InteropServices;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Tests.Agents;

/// <summary>
/// Integration test — invokes the real Copilot CLI. Skipped unless the env var
/// <c>COPILOT_INTEGRATION_CLI_PATH</c> points to a real CLI install (or the
/// extensionless npm shim we want to validate).
/// </summary>
public class CopilotVersionCheckerIntegrationTests
{
    [Fact]
    public async Task EnsureCopilotCliVersion_ExtensionlessNpmShim_OnWindows_Succeeds()
    {
        var cliPath = Environment.GetEnvironmentVariable("COPILOT_INTEGRATION_CLI_PATH");
        if (string.IsNullOrWhiteSpace(cliPath))
        {
            return; // Skipped when no integration path configured.
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // Only relevant on Windows.
        }

        var version = await CopilotVersionChecker.EnsureCopilotCliVersionAsync(
            cliPath, minVersion: "0.0.410", timeout: TimeSpan.FromSeconds(30), ct: CancellationToken.None);

        version.Should().NotBeNullOrWhiteSpace();
    }
}
