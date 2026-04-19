using System.Runtime.InteropServices;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;

/// <summary>
/// Resolves the Copilot CLI executable path across platforms.
/// On Windows, if the configured path is an absolute/relative path to an extensionless
/// file (e.g., an npm shebang shim like <c>node_modules/.bin/copilot</c>), probes for
/// sibling <c>.cmd</c>/<c>.exe</c>/<c>.ps1</c>/<c>.bat</c> wrappers and returns the first
/// that exists. Bare names ("copilot") are returned unchanged so PATH+PATHEXT lookup runs.
/// </summary>
internal static class CopilotCliPathResolver
{
    private static readonly string[] WindowsExtensions = [".cmd", ".exe", ".ps1", ".bat"];

    public static string Resolve(string cliPath)
    {
        if (string.IsNullOrWhiteSpace(cliPath))
        {
            return cliPath;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return cliPath;
        }

        // Bare names (no directory separator) resolve through PATH+PATHEXT — leave alone.
        var hasPathSeparator = cliPath.Contains('/', StringComparison.Ordinal)
            || cliPath.Contains('\\', StringComparison.Ordinal);
        if (!hasPathSeparator)
        {
            return cliPath;
        }

        // If the configured path already has an extension and exists, use it.
        if (!string.IsNullOrEmpty(Path.GetExtension(cliPath)) && File.Exists(cliPath))
        {
            return cliPath;
        }

        // Extensionless path OR path with unknown extension that doesn't exist —
        // probe the Windows wrapper variants.
        foreach (var ext in WindowsExtensions)
        {
            var candidate = cliPath + ext;
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Nothing matched — return original so Process.Start raises a descriptive error.
        return cliPath;
    }
}
