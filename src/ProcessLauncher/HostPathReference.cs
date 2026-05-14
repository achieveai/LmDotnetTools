namespace AchieveAi.LmDotnetTools.ProcessLauncher;

/// <summary>
/// Classifies a host-side path embedded in a <see cref="ProcessLaunchRequest"/>'s
/// arguments or environment so a non-local launcher (e.g., Docker) can mount/translate
/// it without re-parsing command lines.
/// </summary>
public enum HostPathKind
{
    /// <summary>An MCP config file written to disk by the provider.</summary>
    McpConfigFile,

    /// <summary>The system-prompt temp file passed via a CLI flag.</summary>
    SystemPromptFile,

    /// <summary>The process working directory.</summary>
    WorkingDirectory,

    /// <summary>A profile staging directory (e.g., redirected <c>CLAUDE_CONFIG_DIR</c>).</summary>
    StagingDirectory,

    /// <summary>Any other host path the launcher should be aware of.</summary>
    Other,
}

/// <summary>
/// Declares a host-side path that the launching provider embedded in
/// <see cref="ProcessLaunchRequest.Arguments"/> or
/// <see cref="ProcessLaunchRequest.EnvironmentOverrides"/>. A remote / container
/// launcher reads these to decide what to mount or translate.
/// </summary>
/// <param name="Path">Absolute or relative path on the host.</param>
/// <param name="Kind">What the path represents to the provider.</param>
public sealed record HostPathReference(string Path, HostPathKind Kind);
