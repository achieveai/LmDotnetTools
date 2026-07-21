using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// The sweep watch-set reconciler: it must surface orphaned <c>review/*</c> branches (whose review row is
/// missing from the DB) as pollable PRs resolved back to a configured repo identity, while never dropping or
/// duplicating the DB-derived set, and skipping any branch it cannot resolve.
/// </summary>
public sealed class OrphanBranchReconcilerTests
{
    private static readonly RepoIdentity Widgets = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "widgets",
    };

    private static readonly PrPollTarget WidgetsTarget = new()
    {
        Provider = "github",
        Repo = Widgets,
        Scope = "acme/widgets:open-prs",
    };

    [Fact]
    public void Adds_an_orphaned_branch_that_matches_a_configured_repo()
    {
        var result = OrphanBranchReconciler.Reconcile(
            fromDb: [],
            remoteReviewBranches: ["review/widgets-42"],
            configuredTargets: [WidgetsTarget],
            NullLogger.Instance);

        var pr = result.Should().ContainSingle().Subject;
        pr.Provider.Should().Be("github");
        pr.PrId.Should().Be("42");
        pr.Branch.Should().Be("review/widgets-42");
        pr.Repo.RepoName.Should().Be("widgets", "the identity is recovered from the configured target, not the branch name");
    }

    [Fact]
    public void Keeps_the_db_set_and_does_not_duplicate_a_branch_already_covered()
    {
        ReviewedPr[] fromDb = [new(Widgets, "github", "42", "review/widgets-42")];

        var result = OrphanBranchReconciler.Reconcile(
            fromDb,
            remoteReviewBranches: ["review/widgets-42"],
            configuredTargets: [WidgetsTarget],
            NullLogger.Instance);

        result.Should().ContainSingle().Which.Branch.Should().Be("review/widgets-42");
    }

    [Fact]
    public void Skips_a_branch_whose_slug_matches_no_configured_repo()
    {
        var result = OrphanBranchReconciler.Reconcile(
            fromDb: [],
            remoteReviewBranches: ["review/unknown-7"],
            configuredTargets: [WidgetsTarget],
            NullLogger.Instance);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Resolves_a_legacy_nested_branch_via_the_owner_repo_slug()
    {
        // Left over from before the {repo}-{pr} rename: review/{provider}/{owner-repo}/{pr}.
        var result = OrphanBranchReconciler.Reconcile(
            fromDb: [],
            remoteReviewBranches: ["review/github/acme-widgets/42"],
            configuredTargets: [WidgetsTarget],
            NullLogger.Instance);

        var pr = result.Should().ContainSingle().Subject;
        pr.Provider.Should().Be("github");
        pr.PrId.Should().Be("42");
        pr.Branch.Should().Be("review/github/acme-widgets/42", "the sweep must act on the actual legacy branch name");
        pr.Repo.RepoName.Should().Be("widgets");
    }

    [Fact]
    public void Skips_a_legacy_branch_whose_slug_matches_no_configured_repo()
    {
        var result = OrphanBranchReconciler.Reconcile(
            fromDb: [],
            remoteReviewBranches: ["review/github/other-owner-other-repo/9"],
            configuredTargets: [WidgetsTarget],
            NullLogger.Instance);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Warns_once_per_unmatched_branch_across_sweeps_when_a_dedupe_set_is_supplied()
    {
        // A stray branch that maps to no configured repo is skipped every sweep. Passing a persistent dedupe
        // set must collapse the "matches no configured repo" warning to ONE per branch per process instead of
        // one per sweep (a single stray otherwise emitted 163 warnings in one mcqdb run).
        var warned = new HashSet<string>(StringComparer.Ordinal);
        var logger = new CountingLogger();

        for (var sweep = 0; sweep < 5; sweep++)
        {
            var result = OrphanBranchReconciler.Reconcile(
                fromDb: [],
                remoteReviewBranches: ["review/lmdotnettools-178"],
                configuredTargets: [WidgetsTarget],
                logger,
                warned);
            result.Should().BeEmpty("the branch still matches no configured repo, so it is never added to the watch-set");
        }

        logger.WarningCount.Should().Be(1, "the unresolvable branch is a steady-state condition, warned once per process");
        warned.Should().Contain("review/lmdotnettools-178");
    }

    /// <summary>Minimal <see cref="ILogger"/> that only counts warning-level records, so a test can assert the
    /// stray-branch skip is logged once across sweeps rather than on every one.</summary>
    private sealed class CountingLogger : ILogger
    {
        public int WarningCount { get; private set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                WarningCount++;
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
