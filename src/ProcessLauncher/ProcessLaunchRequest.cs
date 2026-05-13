using System.Collections.Immutable;
using System.Text;

namespace AchieveAi.LmDotnetTools.ProcessLauncher;

/// <summary>
/// Describes the process a provider wants spawned. Carried across the
/// <see cref="IProcessLauncher"/> seam so a Docker / SSH / remote launcher
/// can run the work somewhere other than the host's local OS.
/// </summary>
/// <remarks>
/// All host-side path data the launcher may need to mount or translate is
/// surfaced explicitly in <see cref="HostPaths"/> rather than left to be parsed
/// out of <see cref="Arguments"/> or <see cref="EnvironmentOverrides"/>.
/// </remarks>
public sealed record ProcessLaunchRequest
{
    /// <summary>The CLI agent kind. Determines how <see cref="DefaultProcessLauncher"/>
    /// resolves the executable (Node + cli.js probe for Claude, direct executable
    /// for Codex, Windows-shim probe for Copilot).</summary>
    public required CliAgentKind Agent { get; init; }

    /// <summary>Default executable name to use when <see cref="ExecutableOverride"/>
    /// is null. For Claude this is the cli.js entry point, for Codex/Copilot the
    /// CLI binary name.</summary>
    public required string ExecutableHint { get; init; }

    /// <summary>Caller-supplied executable path. Bypasses the default
    /// launcher's discovery logic. Maps to today's <c>CliPath</c> /
    /// <c>CodexCliPath</c> / <c>CopilotCliPath</c> option fields.</summary>
    public string? ExecutableOverride { get; init; }

    /// <summary>Path to Node.js. Only consulted when
    /// <see cref="Agent"/> is <see cref="CliAgentKind.Claude"/>; null delegates
    /// to the launcher's auto-discovery.</summary>
    public string? NodeJsPath { get; init; }

    /// <summary>CLI arguments. Pre-tokenized by the caller — the launcher does
    /// not re-quote.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Working directory for the spawned process. Surfaced separately
    /// (and tagged in <see cref="HostPaths"/>) so a Docker launcher can mount it.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment variables to apply to the spawned process. A null
    /// value clears the inherited variable (matches <see cref="System.Diagnostics.ProcessStartInfo.Environment"/>).</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentOverrides { get; init; }
        = ImmutableDictionary<string, string?>.Empty;

    /// <summary>Encoding used to read stdout. Defaults to UTF-8 with no BOM.</summary>
    public Encoding StandardOutputEncoding { get; init; } = new UTF8Encoding(false);

    /// <summary>Encoding used to read stderr. Defaults to UTF-8 with no BOM.</summary>
    public Encoding StandardErrorEncoding { get; init; } = new UTF8Encoding(false);

    /// <summary>Host paths embedded in <see cref="Arguments"/> /
    /// <see cref="EnvironmentOverrides"/> that a non-local launcher may need
    /// to translate or mount. Empty by default.</summary>
    public IReadOnlyList<HostPathReference> HostPaths { get; init; } = [];
}
