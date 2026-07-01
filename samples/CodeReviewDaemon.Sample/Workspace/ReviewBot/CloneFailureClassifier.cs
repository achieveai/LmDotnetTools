namespace CodeReviewDaemon.Sample.Workspace.ReviewBot;

/// <summary>The classified cause of a failed ReviewBot clone (Thread #3).</summary>
internal enum CloneFailureKind
{
    /// <summary>The remote does not exist. <c>reviewbot init</c> requires a pre-created remote.</summary>
    RepositoryNotFound,

    /// <summary>The bot identity authenticated but lacks access to the repo.</summary>
    PermissionDenied,

    /// <summary>The credential was rejected (missing, expired, or wrong token).</summary>
    BadCredential,

    /// <summary>A network/DNS/gateway-reachability failure — likely transient, worth a retry.</summary>
    TransientGateway,

    /// <summary>An unrecognized failure; the raw git stderr is surfaced verbatim.</summary>
    Unknown,
}

/// <summary>A classified clone failure: its cause, the process exit code to return, and a precise message.</summary>
internal sealed record CloneFailureDiagnosis(CloneFailureKind Kind, int ExitCode, string Message);

/// <summary>
/// Maps a failed <c>git clone</c> (exit code + stderr) to a precise <see cref="CloneFailureDiagnosis"/>
/// (Thread #3). This narrows the <c>reviewbot init</c> contract: the ReviewBot remote must be
/// <b>pre-created</b> out-of-band (the command never creates a provider repo); when the clone of that
/// remote fails, the operator gets an actionable reason — repository-not-found, permission-denied,
/// bad-credential, or transient-gateway — each with its own exit code, rather than a generic failure.
/// It is a pure function of its inputs, so the (otherwise gateway-bound) <c>reviewbot init</c> path is
/// unit-testable.
/// </summary>
internal static class CloneFailureClassifier
{
    /// <summary>Exit code for a missing remote (the remote must be pre-created out-of-band).</summary>
    public const int NotFoundExitCode = 66; // EX_NOINPUT

    /// <summary>Exit code for an authenticated identity lacking repo access.</summary>
    public const int PermissionExitCode = 77; // EX_NOPERM

    /// <summary>Exit code for a rejected credential.</summary>
    public const int BadCredentialExitCode = 67; // EX_NOUSER

    /// <summary>Exit code for a transient network/gateway failure (retryable).</summary>
    public const int TransientExitCode = 75; // EX_TEMPFAIL

    /// <summary>Exit code for an unrecognized clone failure.</summary>
    public const int UnknownExitCode = 70; // EX_SOFTWARE

    /// <summary>Classifies a failed clone from git's <paramref name="exitCode"/> and <paramref name="stderr"/>.</summary>
    public static CloneFailureDiagnosis Classify(int exitCode, string stderr)
    {
        var text = stderr ?? string.Empty;
        var lower = text.ToLowerInvariant();

        // Order matters: an auth failure can also mention "could not read" — check the specific signals first.
        if (lower.Contains("repository not found") || lower.Contains("not found")
            || lower.Contains("does not exist"))
        {
            return new CloneFailureDiagnosis(
                CloneFailureKind.RepositoryNotFound,
                NotFoundExitCode,
                "ReviewBot remote not found. `reviewbot init` requires the ReviewBot repository to be "
                + "pre-created out-of-band (it does not create provider repos); create the empty remote, "
                + $"grant the bot identity access, then re-run. git said:\n{Trim(text)}");
        }

        if (lower.Contains("authentication failed") || lower.Contains("invalid username or password")
            || lower.Contains("could not read username") || lower.Contains("terminal prompts disabled"))
        {
            return new CloneFailureDiagnosis(
                CloneFailureKind.BadCredential,
                BadCredentialExitCode,
                "ReviewBot clone was rejected by authentication. Sign in once with the `auth` subcommand "
                + $"(or refresh the bot credential) and re-run. git said:\n{Trim(text)}");
        }

        if ((lower.Contains("permission") && lower.Contains("denied"))
            || lower.Contains("403")
            || lower.Contains("write access to repository not granted"))
        {
            return new CloneFailureDiagnosis(
                CloneFailureKind.PermissionDenied,
                PermissionExitCode,
                "ReviewBot remote exists but the bot identity lacks access. Grant the bot read access to "
                + $"the ReviewBot repository and re-run. git said:\n{Trim(text)}");
        }

        if (lower.Contains("could not resolve host") || lower.Contains("failed to connect")
            || lower.Contains("connection timed out") || lower.Contains("connection refused")
            || lower.Contains("temporary failure") || lower.Contains("502") || lower.Contains("503")
            || lower.Contains("504"))
        {
            return new CloneFailureDiagnosis(
                CloneFailureKind.TransientGateway,
                TransientExitCode,
                "ReviewBot clone failed to reach the remote (network/gateway). This is likely transient — "
                + $"verify the sandbox gateway and remote host, then retry. git said:\n{Trim(text)}");
        }

        return new CloneFailureDiagnosis(
            CloneFailureKind.Unknown,
            UnknownExitCode,
            $"ReviewBot clone failed (git exit {exitCode}):\n{Trim(text)}");
    }

    private static string Trim(string stderr) => stderr.Trim();
}
