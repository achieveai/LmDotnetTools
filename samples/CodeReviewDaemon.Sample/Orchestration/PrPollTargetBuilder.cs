using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Turns the operator allow-list (<see cref="CodeReviewDaemonOptions.EnabledRepos"/>) into the
/// <see cref="PrPollTarget"/> set the <see cref="PrPollingService"/> watches — the single consumer of
/// that config. An <c>owner/repo</c> (2 segments) entry is a GitHub repo; an <c>org/project/repo</c>
/// (3 segments) entry is an ADO repo. ADO targets are emitted only when <c>EnableAdoProvider</c> is set
/// (otherwise no <c>ado</c> provider is registered to serve them). The poll <see cref="PrPollTarget.Mode"/>
/// follows <c>EnableCommentPosting</c> so the safe default stays collect-only. Malformed entries are
/// logged and skipped rather than failing daemon boot.
/// </summary>
internal static class PrPollTargetBuilder
{
    /// <summary>
    /// Refuses daemon startup, naming the offending entry, when any <see cref="CodeReviewDaemonOptions.EnabledRepos"/>
    /// entry is malformed. <see cref="Build"/> does NOT encode anything — it splits an entry and stores the raw
    /// segments on <see cref="RepoIdentity"/>; encoding happens downstream, in
    /// <c>GitRemoteUrl.RepoPathFor</c> (the clone URL + the submodule allow rule, issue #478/#485) and in
    /// <c>DaemonReviewStageExecutor.BuildPromptVariables</c> (<c>Uri.EscapeDataString</c> for the REST URLs the
    /// agent runs through <c>curl</c>). Encoding downstream is not validation either way: a value with an empty
    /// segment or an embedded <c>/ ? # %</c> still yields a syntactically valid URL that silently polls the
    /// wrong repo (or nothing). Config is operator-controlled, so this is a loud-at-load gap, not a security
    /// hole — this method makes it loud at the same point every other daemon option is validated, rather than
    /// logged-and-skipped after the daemon is already up.
    /// <para>
    /// An entry must split into exactly 2 (<c>owner/repo</c>) or 3 (<c>org/project/repo</c>) segments, each
    /// non-empty and free of <c>? # %</c>. A <c>/</c> inside a name cannot survive the split, so it surfaces
    /// here as an empty segment or a wrong segment count. Spaces are allowed — a legitimate Azure DevOps org or
    /// project name may contain them, and every downstream consumer escapes them (<c>GitRemoteUrl.RepoPathFor</c>
    /// percent-encodes each segment; <c>System.Uri</c> escapes a space in the <c>AdoPrProvider</c> REST path).
    /// </para>
    /// <para>
    /// Every diagnostic names the offending element by its configuration INDEX and quotes its raw value, so an
    /// operator can go straight to <c>CodeReviewDaemon:EnabledRepos:{i}</c>. The quotes matter for the blank
    /// case: a whitespace-only entry is invisible unrendered, and an unquoted message would just show a gap.
    /// </para>
    /// </summary>
    public static void ValidateEnabledRepos(CodeReviewDaemonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        for (var i = 0; i < options.EnabledRepos.Count; i++)
        {
            var entry = options.EnabledRepos[i];
            if (string.IsNullOrWhiteSpace(entry))
            {
                throw new InvalidOperationException(
                    $"CodeReviewDaemon:EnabledRepos[{i}] is blank or whitespace-only (raw value: {Render(entry)}); "
                    + "expected 'owner/repo' or 'org/project/repo'.");
            }

            // Split WITHOUT RemoveEmptyEntries so an embedded '/' (an empty segment) is caught here rather than
            // being silently collapsed by Build's lenient split and re-pointed at a different repo.
            var segments = entry.Split('/');

            if (segments.Length is not (2 or 3))
            {
                throw new InvalidOperationException(
                    $"CodeReviewDaemon:EnabledRepos[{i}] '{entry}' has {segments.Length} segment(s); expected "
                    + "'owner/repo' (2) or 'org/project/repo' (3).");
            }

            for (var s = 0; s < segments.Length; s++)
            {
                var segment = segments[s];
                if (string.IsNullOrWhiteSpace(segment))
                {
                    throw new InvalidOperationException(
                        $"CodeReviewDaemon:EnabledRepos[{i}] '{entry}' has a blank segment at position {s} "
                        + $"(raw value: {Render(segment)}); every owner/org/project/repo name must be non-empty.");
                }

                var bad = segment.IndexOfAny(['?', '#', '%']);
                if (bad >= 0)
                {
                    throw new InvalidOperationException(
                        $"CodeReviewDaemon:EnabledRepos[{i}] '{entry}' has a segment containing '{segment[bad]}'; "
                        + "owner/org/project/repo names may not contain '? # %' (a '/' is the segment separator).");
                }
            }
        }
    }

    /// <summary>
    /// Renders a rejected value so it is still legible when it is empty or pure whitespace: quoted, so the
    /// operator sees the extent of a run of spaces, and an explicit <c>&lt;null&gt;</c> rather than empty
    /// quotes when the configured element is absent entirely.
    /// </summary>
    private static string Render(string? value) => value is null ? "<null>" : $"'{value}'";

    public static IReadOnlyList<PrPollTarget> Build(CodeReviewDaemonOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var mode = options.EnableCommentPosting ? "post" : "collect-only";
        var targets = new List<PrPollTarget>();

        foreach (var entry in options.EnabledRepos)
        {
            var segments = (entry ?? string.Empty)
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var target = segments.Length switch
            {
                2 => GitHubTarget(segments, mode, options.ReviewModelId, options.MaxPrAgeDays),
                3 => AdoTarget(segments, mode, options.ReviewModelId, options.MaxPrAgeDays, options.EnableAdoProvider, logger),
                _ => null,
            };

            if (target is not null)
            {
                targets.Add(target);
            }
            else if (segments.Length is not (2 or 3))
            {
                logger.LogWarning(
                    "Ignoring malformed EnabledRepos entry '{Entry}': expected 'owner/repo' or 'org/project/repo'.",
                    entry);
            }
        }

        return targets;
    }

    private static PrPollTarget GitHubTarget(string[] segments, string mode, string? modelId, int maxPrAgeDays) =>
        new()
        {
            Provider = "github",
            Repo = new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = segments[0],
                RepoName = segments[1],
            },
            Scope = $"{segments[0]}/{segments[1]}:open-prs",
            Mode = mode,
            ModelId = modelId,
            MaxPrAgeDays = maxPrAgeDays,
        };

    private static PrPollTarget? AdoTarget(string[] segments, string mode, string? modelId, int maxPrAgeDays, bool enableAdoProvider, ILogger logger)
    {
        if (!enableAdoProvider)
        {
            logger.LogWarning(
                "Skipping ADO repo '{Repo}' because EnableAdoProvider is off; no 'ado' provider is registered.",
                string.Join('/', segments));
            return null;
        }

        return new PrPollTarget
        {
            Provider = "ado",
            Repo = new RepoIdentity
            {
                Provider = "azure-devops",
                OrgOrOwner = segments[0],
                Project = segments[1],
                RepoName = segments[2],
            },
            Scope = $"{segments[0]}/{segments[1]}/{segments[2]}:active-prs",
            Mode = mode,
            ModelId = modelId,
            MaxPrAgeDays = maxPrAgeDays,
        };
    }
}
