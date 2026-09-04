using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Workspace.ReviewBot;

/// <summary>
/// Shared clone-or-reuse logic for the ReviewBot checkout (PR #121 H3). Both the one-time
/// <c>reviewbot init</c> subcommand and the runtime daemon (before its first retention push) must
/// ensure the configured ReviewBot remote is checked out at the working directory; this is the single
/// implementation of that step so the two paths cannot drift. It is a pure orchestration helper over
/// <see cref="GitRunner"/>, so it is verifiable against a fake command runner. On a failed clone it
/// returns a classified <see cref="CloneFailureDiagnosis"/> (not-found / permission / bad-credential /
/// transient) rather than a generic failure.
/// </summary>
internal static class ReviewBotCheckout
{
    /// <summary>
    /// Clones <paramref name="url"/> into <paramref name="workdir"/>, or reuses an existing checkout.
    /// Returns <c>null</c> on success, or a classified <see cref="CloneFailureDiagnosis"/> on a failed clone.
    /// </summary>
    public static async Task<CloneFailureDiagnosis?> EnsureCheckoutAsync(
        GitRunner git,
        string url,
        string workdir,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(workdir);
        ArgumentNullException.ThrowIfNull(logger);

        var probe = await git.RunAsync(["rev-parse", "--is-inside-work-tree"], workdir, cancellationToken)
            .ConfigureAwait(false);
        if (probe.Succeeded)
        {
            var origin = await git.RunAsync(["remote", "get-url", "origin"], workdir, cancellationToken)
                .ConfigureAwait(false);
            if (!origin.Succeeded || string.IsNullOrWhiteSpace(origin.Stdout))
            {
                logger.LogError(
                    "Refusing to reuse review-store checkout at {Workdir}: its origin remote could not be read.",
                    workdir
                );
                return new CloneFailureDiagnosis(
                    CloneFailureKind.OriginMismatch,
                    CloneFailureClassifier.UnknownExitCode,
                    "Existing review-store checkout origin could not be verified."
                );
            }

            if (!RemoteMatches(url, origin.Stdout))
            {
                logger.LogError(
                    "Refusing to reuse review-store checkout at {Workdir}: its origin does not match the configured store.",
                    workdir
                );
                return new CloneFailureDiagnosis(
                    CloneFailureKind.OriginMismatch,
                    CloneFailureClassifier.UnknownExitCode,
                    "Existing review-store checkout origin does not match the configured store."
                );
            }

            logger.LogInformation("Reusing existing ReviewBot checkout at {Workdir}.", workdir);
            return null;
        }

        var clone = await git.RunAsync(["clone", url, workdir], workingDirectory: null, cancellationToken)
            .ConfigureAwait(false);
        if (clone.Succeeded)
        {
            return null;
        }

        // The clone of a pre-created remote failed — classify the cause so the caller gets an actionable
        // reason and a distinct exit code rather than a generic failure.
        return CloneFailureClassifier.Classify(clone.ExitCode, clone.Stderr);
    }

    private static bool RemoteMatches(string expected, string actual)
    {
        var expectedIdentity = TryCanonicalizeRemote(expected);
        var actualIdentity = TryCanonicalizeRemote(actual);
        return expectedIdentity is not null
            && actualIdentity is not null
            && string.Equals(expectedIdentity, actualIdentity, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryCanonicalizeRemote(string value)
    {
        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}:{uri.Port}{path}";
    }
}
