using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>
    /// Fails a create whose response did not acknowledge <paramref name="requestedHome"/> exactly. The
    /// gateway reports the normalized, persisted home on <c>volumes.workspace.home</c>; a gateway that predates
    /// the field never sends it, so a missing or different value is not proof the request was honored (gateway
    /// Docs/tools-and-api.md, "Sandbox home directory"). The sandbox the gateway did create is deleted first,
    /// because nothing else holds its session id once this throws.
    /// </summary>
    private async Task RequireAcknowledgedHomeAsync(
        SandboxInfo info,
        string requestedHome,
        int statusCode,
        CancellationToken ct
    )
    {
        var expected = NormalizeHome(requestedHome);
        if (string.Equals(info.WorkspaceHome, expected, StringComparison.Ordinal))
        {
            return;
        }

        var acknowledged = info.WorkspaceHome is null ? "no home" : $"home '{info.WorkspaceHome}'";
        string cleanup;
        try
        {
            // Not the caller's token: once this throws nothing else holds the session id, so a cancellation here
            // would orphan the sandbox. The transport budget still bounds the call.
            await DeleteAsync(info.SessionId, CancellationToken.None).ConfigureAwait(false);
            cleanup = "It was deleted.";
        }
        catch (SandboxException ex)
        {
            cleanup =
                $"Deleting it failed ({ex.Kind}, status {ex.StatusCode?.ToString() ?? "none"}); delete it with DeleteAsync.";
        }

        throw new SandboxException(
            SandboxErrorKind.Protocol,
            $"Sandbox gateway created sandbox '{info.SessionId}' but acknowledged {acknowledged} instead of the "
                + $"requested home '{expected}' (a gateway that predates per-sandbox homes ignores the field). {cleanup}",
            statusCode
        );
    }

    /// <summary>
    /// The gateway's normalized form of a home: empty and <c>.</c> components dropped, so
    /// <c>./worktrees//issue-142/</c> becomes <c>worktrees/issue-142</c>. Anything else the gateway would
    /// normalize differently (<c>\</c>, <c>..</c>, absolute paths) it rejects with a <c>400</c> before creating.
    /// </summary>
    internal static string NormalizeHome(string home) =>
        string.Join('/', home.Split('/').Where(part => part.Length > 0 && part != "."));

    /// <summary>
    /// Maps a conformance report from a successful create, rejecting a report missing a field the gateway
    /// always sends as <see cref="SandboxErrorKind.Protocol"/> rather than defaulting it.
    /// </summary>
    private static SandboxImageReport ToImageReportOrThrow(ImageReportDto dto, string operation, int statusCode) =>
        TryToImageReport(dto)
        ?? throw new SandboxException(
            SandboxErrorKind.Protocol,
            $"Sandbox gateway returned an incomplete image report for {operation}.",
            statusCode
        );

    /// <summary>
    /// Maps a conformance report, or returns <see langword="null"/> when a required field or a warning is
    /// missing. The error path uses this directly: a malformed report on a rejection is dropped, never allowed
    /// to replace the gateway's classified error.
    /// </summary>
    private static SandboxImageReport? TryToImageReport(ImageReportDto dto)
    {
        if (
            dto
            is not {
                OsId: { } osId,
                DebianFamily: { } debianFamily,
                Arch: { } arch,
                User: { } user,
                Uid: { } uid,
                Gid: { } gid,
                UserReused: { } userReused,
                HasBash: { } hasBash,
                HasPwsh: { } hasPwsh,
                HasSystemCaBundle: { } hasSystemCaBundle,
            }
        )
        {
            return null;
        }

        var warnings = dto.Warnings ?? [];
        if (warnings.Any(warning => warning is null))
        {
            return null;
        }

        return new SandboxImageReport
        {
            OsId = osId,
            DebianFamily = debianFamily,
            Arch = arch,
            User = user,
            Uid = uid,
            Gid = gid,
            UserReused = userReused,
            HasBash = hasBash,
            HasPwsh = hasPwsh,
            HasSystemCaBundle = hasSystemCaBundle,
            Warnings = [.. warnings.Select(warning => warning!)],
        };
    }
}
