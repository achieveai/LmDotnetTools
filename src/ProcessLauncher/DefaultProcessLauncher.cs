using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AchieveAi.LmDotnetTools.ProcessLauncher;

/// <summary>
/// Default <see cref="IProcessLauncher"/> implementation. Spawns the requested
/// CLI directly via <see cref="Process.Start(ProcessStartInfo)"/>. Handles
/// agent-specific discovery (Node + cli.js for Claude, Windows shebang shim
/// probing for Copilot) so providers do not have to.
/// </summary>
public sealed class DefaultProcessLauncher : IProcessLauncher
{
    /// <summary>Shared singleton; the launcher carries no per-call state.</summary>
    public static IProcessLauncher Instance { get; } = new DefaultProcessLauncher();

    public IProcessHandle Launch(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var (fileName, prependArgs) = ResolveExecutable(request);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            StandardOutputEncoding = request.StandardOutputEncoding,
            StandardErrorEncoding = request.StandardErrorEncoding,
        };

        // Build the argument list: any agent-specific prefix (e.g., the cli.js
        // path for Claude) followed by the caller's tokenized arguments.
        foreach (var prepend in prependArgs)
        {
            psi.ArgumentList.Add(prepend);
        }

        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in request.EnvironmentOverrides)
        {
            psi.Environment[key] = value;
        }

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new ProcessLauncherException(
                    $"Process.Start returned null when launching '{fileName}'.");
        }
        catch (ProcessLauncherException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProcessLauncherException(
                $"Failed to start '{fileName}'. Ensure the executable is installed and accessible.",
                ex);
        }

        return new SystemProcessHandle(process);
    }

    private static (string FileName, IReadOnlyList<string> PrependArgs) ResolveExecutable(ProcessLaunchRequest request)
    {
        return request.Agent switch
        {
            CliAgentKind.Claude => ResolveClaude(request),
            CliAgentKind.Codex => ResolveCodex(request),
            CliAgentKind.Copilot => ResolveCopilot(request),
            _ => throw new ProcessLauncherException(
                $"Unsupported {nameof(CliAgentKind)}: {request.Agent}."),
        };
    }

    private static (string, IReadOnlyList<string>) ResolveClaude(ProcessLaunchRequest request)
    {
        var node = request.NodeJsPath ?? FindNodeJs();
        var cliPath = request.ExecutableOverride ?? FindClaudeAgentSdkCli(request.ExecutableHint);
        return (node, new[] { cliPath });
    }

    private static (string, IReadOnlyList<string>) ResolveCodex(ProcessLaunchRequest request)
    {
        var path = request.ExecutableOverride ?? request.ExecutableHint;
        return (path, Array.Empty<string>());
    }

    private static (string, IReadOnlyList<string>) ResolveCopilot(ProcessLaunchRequest request)
    {
        var configured = request.ExecutableOverride ?? request.ExecutableHint;
        return (ResolveCopilotCliPath(configured), Array.Empty<string>());
    }

    /// <summary>
    /// Returns the Node.js executable name. The OS resolves it via PATH; if
    /// Node.js is missing, <see cref="Process.Start(ProcessStartInfo)"/>
    /// raises a descriptive error.
    /// </summary>
    private static string FindNodeJs() => "node";

    private static string FindClaudeAgentSdkCli(string hint)
    {
        if (!string.IsNullOrEmpty(hint) && File.Exists(hint))
        {
            return hint;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var npmGlobalPath = Path.Combine(
                appData,
                "npm",
                "node_modules",
                "@anthropic-ai",
                "claude-agent-sdk",
                "cli.js");
            if (File.Exists(npmGlobalPath))
            {
                return npmGlobalPath;
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var systemPath = Path.Combine(
                programFiles,
                "nodejs",
                "node_modules",
                "@anthropic-ai",
                "claude-agent-sdk",
                "cli.js");
            if (File.Exists(systemPath))
            {
                return systemPath;
            }
        }
        else
        {
            const string globalPath = "/usr/local/lib/node_modules/@anthropic-ai/claude-agent-sdk/cli.js";
            if (File.Exists(globalPath))
            {
                return globalPath;
            }
        }

        throw new FileNotFoundException(
            "claude-agent-sdk CLI not found. Please install: npm install -g @anthropic-ai/claude-agent-sdk");
    }

    private static readonly string[] WindowsCopilotExtensions = [".cmd", ".exe", ".ps1", ".bat"];

    /// <summary>
    /// Windows-only probe for npm shebang shims (e.g., <c>node_modules/.bin/copilot</c>)
    /// that have no extension. Returns the resolved wrapper path, or the original
    /// input when no probe matches.
    /// </summary>
    private static string ResolveCopilotCliPath(string cliPath)
    {
        if (string.IsNullOrWhiteSpace(cliPath))
        {
            return cliPath;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return cliPath;
        }

        var hasPathSeparator = cliPath.Contains('/', StringComparison.Ordinal)
            || cliPath.Contains('\\', StringComparison.Ordinal);
        if (!hasPathSeparator)
        {
            return cliPath;
        }

        if (!string.IsNullOrEmpty(Path.GetExtension(cliPath)) && File.Exists(cliPath))
        {
            return cliPath;
        }

        foreach (var ext in WindowsCopilotExtensions)
        {
            var candidate = cliPath + ext;
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return cliPath;
    }
}
