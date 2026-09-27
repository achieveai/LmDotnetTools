using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Task #81, Command A — <c>--list-candidate-prs</c>. Read-only, bounded, and over ENABLED repos only:
/// it must never create a <c>review_run</c>, touch the poll cursor, allocate a workflow slot, or invoke an
/// agent. <see cref="ListCandidatePrsCommand.RunAsync"/> deliberately takes no <c>ReviewStore</c> or
/// <c>PrOrchestrator</c> at all — a compile-time proof it cannot reach either — so these tests only need to
/// pin the bound, the field mapping, and that the cursor is never persisted (the provider is only ever
/// asked with a null cursor and its returned cursor is discarded).
/// </summary>
public sealed class ListCandidatePrsCommandTests
{
    private static CodeReviewDaemonOptions Options(params string[] enabledRepos) =>
        new() { EnabledRepos = enabledRepos };

    private static CodeReviewDaemonOptions Options(int maxPrAgeDays, params string[] enabledRepos) =>
        new() { EnabledRepos = enabledRepos, MaxPrAgeDays = maxPrAgeDays };

    private static PullRequestDescriptor Descriptor(string prId, string title = "Title", string author = "alice") =>
        new()
        {
            PrId = prId,
            HeadSha = $"head-{prId}",
            BaseSha = $"base-{prId}",
            TriggerWatermark = $"watermark-{prId}",
            LifecycleState = PrLifecycleState.Open,
            Author = author,
            Title = title,
        };

    private static OpaqueCursor Cursor(string provider, string scope) =>
        new()
        {
            Provider = provider,
            Scope = scope,
            CursorVersion = PrPollingService.CursorVersion,
            CursorPayload = "{}",
        };

    [Fact]
    public async Task Maps_every_field_the_operator_needs_to_approve_a_candidate()
    {
        var provider = new MockPrProvider(
            "github",
            [Descriptor("7", title: "Fix the thing", author: "alice")],
            Cursor("github", "acme/widgets:open-prs")
        );
        var options = Options("acme/widgets");

        var candidates = await ListCandidatePrsCommand.RunAsync(
            options,
            [provider],
            NullLogger.Instance,
            CancellationToken.None
        );

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.Provider.Should().Be("github");
        candidate.RepoKey.Should().Be("acme/widgets");
        candidate.PrId.Should().Be("7");
        candidate.Title.Should().Be("Fix the thing");
        candidate.Author.Should().Be("alice");
        candidate.LifecycleState.Should().Be(PrLifecycleState.Open);
        candidate.HeadSha.Should().Be("head-7");
        candidate.BaseSha.Should().Be("base-7");
        candidate.TriggerWatermark.Should().Be("watermark-7");
    }

    [Fact]
    public async Task Never_asks_the_provider_for_anything_but_a_fresh_resync()
    {
        // Cursor = null unconditionally is what proves this read never advances or trusts a persisted
        // cursor — there is no ReviewStore in scope to have read one from in the first place.
        var provider = new MockPrProvider("github", [Descriptor("7")], Cursor("github", "acme/widgets:open-prs"));

        _ = await ListCandidatePrsCommand.RunAsync(
            Options("acme/widgets"),
            [provider],
            NullLogger.Instance,
            CancellationToken.None
        );

        provider.LastRequestedCursor.Should().BeNull();
    }

    [Fact]
    public async Task Is_bounded_to_MaxCandidates_across_all_enabled_repos()
    {
        var manyPrs = Enumerable
            .Range(1, ListCandidatePrsCommand.MaxCandidates + 5)
            .Select(i => Descriptor(i.ToString()))
            .ToList();
        var provider = new MockPrProvider("github", manyPrs, Cursor("github", "acme/widgets:open-prs"));

        var candidates = await ListCandidatePrsCommand.RunAsync(
            Options("acme/widgets"),
            [provider],
            NullLogger.Instance,
            CancellationToken.None
        );

        candidates.Should().HaveCount(ListCandidatePrsCommand.MaxCandidates);
    }

    [Fact]
    public async Task Caps_across_multiple_repos_rather_than_per_repo()
    {
        // One IPrProvider instance serves both targets (both are "github" repos, matched by provider name
        // only) and hands back a full-to-the-cap page every time it is asked — so the running total must
        // stop at MaxCandidates after the first target, and the second target must contribute nothing more,
        // proving the bound is global rather than reset per repo.
        var fullPage = Enumerable
            .Range(1, ListCandidatePrsCommand.MaxCandidates)
            .Select(i => Descriptor($"pr{i}"))
            .ToList();
        var provider = new MockPrProvider("github", fullPage, Cursor("github", "acme/one:open-prs"));

        var candidates = await ListCandidatePrsCommand.RunAsync(
            Options("acme/one", "acme/two"),
            [provider],
            NullLogger.Instance,
            CancellationToken.None
        );

        candidates.Should().HaveCount(ListCandidatePrsCommand.MaxCandidates);
        provider.CallCount.Should().Be(1, "the cap was already reached after the first target's page");
    }

    [Fact]
    public async Task Skips_a_target_whose_provider_is_not_registered_rather_than_throwing()
    {
        var options = Options("acme/widgets");
        // No IPrProvider registered at all.
        var candidates = await ListCandidatePrsCommand.RunAsync(
            options,
            [],
            NullLogger.Instance,
            CancellationToken.None
        );

        candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Passes_the_same_recency_cutoff_to_the_provider_that_polling_would_compute()
    {
        // Second security-review round, item 2: the proposal must honor MaxPrAgeDays with the SAME cutoff
        // semantics as PrPollingService, not propose PRs the poller would have skipped.
        var now = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var provider = new MockPrProvider(
            "github",
            [Descriptor("7") with { UpdatedAt = now }],
            Cursor("github", "acme/widgets:open-prs")
        );
        var options = Options(7, "acme/widgets");

        _ = await ListCandidatePrsCommand.RunAsync(
            options,
            [provider],
            NullLogger.Instance,
            CancellationToken.None,
            clock
        );

        provider.LastRecencyCutoff.Should().Be(now - TimeSpan.FromDays(7));
    }

    [Fact]
    public async Task Excludes_a_pr_outside_the_recency_window_the_same_way_polling_would()
    {
        var now = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var freshPr = Descriptor("fresh") with { UpdatedAt = now };
        var stalePr = Descriptor("stale") with { UpdatedAt = now - TimeSpan.FromDays(30) };
        var provider = new MockPrProvider("github", [freshPr, stalePr], Cursor("github", "acme/widgets:open-prs"));
        var options = Options(7, "acme/widgets");

        var candidates = await ListCandidatePrsCommand.RunAsync(
            options,
            [provider],
            NullLogger.Instance,
            CancellationToken.None,
            clock
        );

        candidates.Should().ContainSingle(c => c.PrId == "fresh");
    }

    [Fact]
    public async Task Applies_no_cutoff_at_all_when_MaxPrAgeDays_is_unset()
    {
        // Options(...) defaults MaxPrAgeDays to 0 — every existing test above relies on this to keep
        // passing unchanged with no timeProvider argument at all.
        var oldPr = Descriptor("old") with
        {
            UpdatedAt = DateTimeOffset.UtcNow.AddYears(-1),
        };
        var provider = new MockPrProvider("github", [oldPr], Cursor("github", "acme/widgets:open-prs"));

        var candidates = await ListCandidatePrsCommand.RunAsync(
            Options("acme/widgets"),
            [provider],
            NullLogger.Instance,
            CancellationToken.None
        );

        candidates.Should().ContainSingle();
        provider.LastRecencyCutoff.Should().BeNull();
    }
}
