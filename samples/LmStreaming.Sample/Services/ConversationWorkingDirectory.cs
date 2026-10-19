using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.Sandbox;

namespace LmStreaming.Sample.Services;

/// <summary>Conversation-local defaults, never a shared-session cwd or a filesystem jail.</summary>
internal sealed class ConversationWorkingDirectory(string relativeDirectory, string absoluteDirectory)
{
    public const string PropertyKey = "sample.workingDirectoryRelPath";

    public static string Validate(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        // Reuse the SDK's POSIX workspace-relative validation, independent of the host OS.
        _ = new SandboxCommand(["pwd"], value);
        var normalized = string.Join('/', value.Split('/').Where(segment => segment.Length > 0 && segment != "."));
        return normalized.Length == 0 ? "." : normalized;
    }

    public static async Task<string?> ReadAsync(IConversationStore store, string threadId)
    {
        var metadata = await store.LoadMetadataAsync(threadId);
        if (metadata?.Properties is not { } properties || !properties.TryGetValue(PropertyKey, out var raw))
        {
            return null;
        }
        return Validate(
            ThreadPropertyValue.AsStringPreservingEmpty(raw)
                ?? throw new InvalidOperationException("Stored conversation working directory is not a string.")
        );
    }

    public static void EnsureSupported(string providerId, bool needsSandbox)
    {
        if (
            !needsSandbox
            || providerId.ToLowerInvariant()
                is "copilot"
                    or "codex"
                    or "claude"
                    or "copilot-mock"
                    or "codex-mock"
                    or "claude-mock"
        )
        {
            throw new ArgumentException(
                "Explicit working directory requires a sandbox mode and a middleware provider; direct-MCP/CLI providers cannot enforce it."
            );
        }
    }

    public static async Task<ConversationWorkingDirectory> ResolveAsync(
        SandboxSessionRegistry registry,
        SandboxSession session,
        string directory,
        CancellationToken ct = default
    )
    {
        // Executing pwd in this cwd verifies existence/containment remotely and discovers the actual
        // mounted path (Docker and local backends have different absolute paths). No host file access.
        var result = await registry.ExecuteWorkspaceCommandAsync(
            session.SessionId,
            new SandboxCommand(["pwd"], directory),
            ct
        );
        var absolute = result.StandardOutput.TrimEnd('\r', '\n');
        if (result.ExitCode != 0 || !absolute.StartsWith('/') || absolute.Contains('\n') || absolute.Contains('\r'))
        {
            throw new InvalidOperationException(
                $"Remote working directory '{directory}' could not be resolved (exit {result.ExitCode})."
            );
        }
        return new ConversationWorkingDirectory(directory, absolute);
    }

    /// <summary>
    /// The sandbox command for one Bash call. The timeout travels with the command: the gateway stops
    /// the process when it elapses, and the SDK waits for that same deadline rather than its 5-minute
    /// default, so a longer request is honoured and a shorter one does not leave the process running.
    /// </summary>
    internal static SandboxCommand BashCommand(string command, string cwd, int timeoutSeconds) =>
        new(["bash", "-c", command], cwd) { ExecutionTimeout = TimeSpan.FromSeconds(timeoutSeconds) };

    public ToolHandler Wrap(
        FunctionContract contract,
        ToolHandler handler,
        SandboxSessionRegistry registry,
        string sessionId
    )
    {
        var pathParameter = contract.Name switch
        {
            "Read" or "Write" or "Edit" => "file_path",
            "Glob" or "Grep" => "path",
            _ => null,
        };
        if (pathParameter is not null && !(contract.Parameters ?? []).Any(p => p.Name == pathParameter))
        {
            throw new InvalidOperationException($"Sandbox tool '{contract.Name}' has an unsupported path schema.");
        }
        if (contract.Name == "Bash" && !(contract.Parameters ?? []).Any(p => p.Name == "command"))
        {
            throw new InvalidOperationException("Sandbox Bash has an unsupported command schema.");
        }

        return async (argsJson, context, ct) =>
        {
            var args =
                JsonNode.Parse(argsJson) as JsonObject
                ?? throw new ArgumentException("Sandbox tool arguments must be an object.");
            if (contract.Name == "Bash")
            {
                if (args["run_in_background"]?.GetValue<bool>() == true)
                {
                    return ToolHandlerResult.FromError(
                        "Background Bash is unsupported with an explicit conversation cwd.",
                        "working_directory_unsupported"
                    );
                }
                var command =
                    args["command"]?.GetValue<string>()
                    ?? throw new ArgumentException("Bash requires a command string.");
                var workdir = args["workdir"]?.GetValue<string>();
                var cwd = string.IsNullOrEmpty(workdir)
                    ? relativeDirectory
                    : Validate(relativeDirectory + "/" + Validate(workdir));
                var timeout = args["timeout"]?.GetValue<int>() ?? 120;
                if (timeout <= 0 || timeout > 3600)
                {
                    return ToolHandlerResult.FromError("Bash timeout must be between 1 and 3600 seconds.");
                }
                var result = await registry.ExecuteWorkspaceCommandAsync(
                    sessionId,
                    BashCommand(command, cwd, timeout),
                    ct
                );
                return new ToolHandlerResult.Resolved(
                    new ToolHandlerResultPayload(
                        System.Text.Json.JsonSerializer.Serialize(
                            new
                            {
                                exitCode = result.ExitCode,
                                stdout = result.StandardOutput,
                                stderr = result.StandardError,
                            }
                        ),
                        IsError: result.ExitCode != 0
                    )
                );
            }
            if (pathParameter is not null)
            {
                var path = args[pathParameter]?.GetValue<string>();
                if (path is null && contract.Name is "Read" or "Write" or "Edit")
                {
                    throw new ArgumentException($"{contract.Name} requires {pathParameter}.");
                }
                path ??= ".";
                // Absolute paths retain ordinary gateway behavior. Relative paths default to this
                // conversation's directory, including searches with an omitted path. This is not a jail.
                if (!path.StartsWith('/'))
                {
                    if (path.Contains('\\') || path.Contains('\0') || (path.Length >= 2 && path[1] == ':'))
                    {
                        return ToolHandlerResult.FromError("Unsupported non-POSIX file path with explicit cwd.");
                    }
                    args[pathParameter] = absoluteDirectory.TrimEnd('/') + "/" + path;
                }
                return await handler(args.ToJsonString(), context, ct);
            }
            if (contract.Name is "Skill" or "math_eval" or "TaskOutput" or "KillShell")
            {
                return await handler(argsJson, context, ct);
            }
            // Includes PowerShell (session-persistent cwd) and unknown/plugin filesystem tools.
            // Do not silently execute any tool whose cwd/path contract we cannot enforce.
            return ToolHandlerResult.FromError(
                $"Sandbox tool '{contract.Name}' is unsupported with an explicit conversation cwd.",
                "working_directory_unsupported"
            );
        };
    }
}
