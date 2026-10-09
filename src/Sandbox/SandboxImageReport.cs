namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// The gateway's conformance report on a sandbox image (ADR 0039): what the injected runtime found in
/// the image, plus the non-fatal <see cref="Warnings"/> (for example a missing <c>pwsh</c>, so the
/// PowerShell tool will fail). Reported on <see cref="SandboxInfo.ImageReport"/> after a create, and on
/// <see cref="SandboxException.ImageReport"/> when a <c>422 image_unsupported</c> probe rejected the image.
/// </summary>
public sealed record SandboxImageReport
{
    /// <summary>The image's <c>/etc/os-release</c> <c>ID</c> (for example <c>debian</c>, <c>ubuntu</c>).</summary>
    public required string OsId { get; init; }

    /// <summary>Whether the image is Debian-family, a hard requirement of the injected runtime.</summary>
    public required bool DebianFamily { get; init; }

    /// <summary>The image's CPU architecture as the runtime saw it (for example <c>x86_64</c>).</summary>
    public required string Arch { get; init; }

    /// <summary>The user sandbox commands run as.</summary>
    public required string User { get; init; }

    /// <summary>That user's uid.</summary>
    public required long Uid { get; init; }

    /// <summary>That user's gid.</summary>
    public required long Gid { get; init; }

    /// <summary>Whether the runtime reused a user the image already had at the sandbox uid.</summary>
    public required bool UserReused { get; init; }

    /// <summary>Whether the image has <c>bash</c>.</summary>
    public required bool HasBash { get; init; }

    /// <summary>Whether the image has <c>pwsh</c>.</summary>
    public required bool HasPwsh { get; init; }

    /// <summary>Whether the image has a system CA bundle (<c>ca-certificates</c>).</summary>
    public required bool HasSystemCaBundle { get; init; }

    /// <summary>Non-fatal gaps the gateway found, as gateway-authored text. Empty when there are none.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}
