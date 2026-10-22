extern alias Reviewd;

using AchieveAi.LmDotnetTools.Misc.Storage;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class ReviewdPrTrackingTests
{
    [Theory]
    [InlineData("2026-10-04T22:00:00Z", true)]
    [InlineData("2026-10-04T21:59:59Z", false)]
    [InlineData("2026-10-05T00:00:00+02:00", true)]
    [InlineData("2026-10-07T22:00:01Z", false)]
    [InlineData("2026-10-07T21:00:00", false)]
    [InlineData("garbage", false)]
    [InlineData(null, false)]
    public void Creation_age_requires_explicit_offset_and_inclusive_rolling_boundary(string? created, bool eligible)
    {
        PrSelection
            .IsWithinCreationAge(
                System.Text.Json.Nodes.JsonValue.Create(created),
                72,
                DateTimeOffset.Parse("2026-10-07T22:00:00Z")
            )
            .Should()
            .Be(eligible);
        PrSelection
            .IsWithinCreationAge(System.Text.Json.Nodes.JsonValue.Create(72), 72, DateTimeOffset.UtcNow)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void Two_segment_repo_is_github_and_its_display_name_parses_back_to_the_same_key()
    {
        var repo = PrCommands.ParseRepo("achieveai/LmDotnetTools");

        (repo.Provider, repo.OrgOrOwner, repo.Project, repo.RepoName)
            .Should()
            .Be(("github", "achieveai", null, "LmDotnetTools"));
        PrCommands.ParseRepo(repo.DisplayName).NormalizedKey.Should().Be(repo.NormalizedKey);
        PrCommands.ParseRepo("org/project/repo").Provider.Should().Be("azure-devops");
    }

    [Theory]
    [InlineData("repo")]
    [InlineData("a/b/c/d")]
    [InlineData("owner/")]
    [InlineData("owner/re?po")]
    public void Malformed_repo_names_are_refused(string value)
    {
        FluentActions.Invoking(() => PrCommands.ParseRepo(value)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Ado_poll_snapshot_preserves_draft_and_creation_metadata_for_rolling_selection()
    {
        var now = DateTimeOffset.Parse("2026-10-13T06:00:00Z");
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-eligibility-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/repo");
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/pullrequests?",
            """
            {"count":4,"value":[
              {"pullRequestId":17,"status":"active","isDraft":false,"creationDate":"2026-10-10T08:00:00+02:00","lastMergeSourceCommit":{"commitId":"head-17"}},
              {"pullRequestId":18,"status":"active","isDraft":true,"creationDate":"2026-10-13T05:00:00Z","lastMergeSourceCommit":{"commitId":"head-18"}},
              {"pullRequestId":19,"status":"active","creationDate":"2026-10-13T05:00:00Z","lastMergeSourceCommit":{"commitId":"head-19"}},
              {"pullRequestId":20,"status":"active","isDraft":false,"creationDate":"2026-10-10T05:59:59Z","lastMergeSourceCommit":{"commitId":"head-20"}}
            ]}
            """
        );
        using var http = new HttpClient(handler);
        var provider = new AdoPrProvider(
            http,
            new FakeOAuthTokenProvider("ado"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AdoPrProvider>.Instance
        );
        try
        {
            await PrCommands.RunAsync(
                CliOptions.Parse(["pr", "check", "--repo", repo.DisplayName, "--db", database]),
                provider,
                CancellationToken.None
            );
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            var snapshot = (
                await store.GetAsync<Dictionary<string, PrCommands.PrObservation>>(
                    $"reviewd/pr-snapshot/{repo.NormalizedKey}"
                )
            )!;
            var current = System.Text.Json.JsonSerializer.SerializeToElement(snapshot["17"]);
            current.GetProperty("IsDraft").GetBoolean().Should().BeFalse();
            current
                .GetProperty("CreatedAt")
                .GetDateTimeOffset()
                .Should()
                .Be(DateTimeOffset.Parse("2026-10-10T08:00:00+02:00"));
            current.GetProperty("CreatedAt").GetString().Should().EndWith("+02:00");
            var picked = snapshot
                .Where(entry =>
                {
                    var pr = System.Text.Json.JsonSerializer.SerializeToElement(entry.Value);
                    return PrSelection.IsEligible(pr, null, false, now)
                        && PrSelection.IsWithinCreationAge(
                            System.Text.Json.Nodes.JsonNode.Parse(pr.GetProperty("CreatedAt").GetRawText()),
                            72,
                            now
                        );
                })
                .Select(entry => entry.Key);
            picked.Should().Equal("17");
            handler.CountRequests("/pushes?").Should().Be(0);
            var legacy = System.Text.Json.JsonSerializer.Deserialize<PrCommands.PrObservation>(
                """{"PrId":"21","Lifecycle":"Open","HeadSha":"head-21","Title":"legacy"}"""
            )!;
            PrSelection
                .IsEligible(System.Text.Json.JsonSerializer.SerializeToElement(legacy), null, false, now)
                .Should()
                .BeFalse();
        }
        finally
        {
            ReviewdSqlitePools.Clear(database);
            File.Delete(database);
        }
    }

    [Fact]
    public async Task Polls_preserve_bindings_detect_comment_changes_and_save_only_complete_repositories()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/repo");
        var options = CliOptions.Parse(["pr", "check", "--repo", repo.DisplayName, "--db", database]);
        var provider = new MockPrProvider(
            "ado",
            [
                new PullRequestDescriptor
                {
                    PrId = "17",
                    HeadSha = "head",
                    BaseSha = "base",
                    TriggerWatermark = "head",
                    LifecycleState = PrLifecycleState.Open,
                    Title = "PR",
                },
            ],
            new OpaqueCursor
            {
                Provider = "ado",
                Scope = "test",
                CursorVersion = 1,
                CursorPayload = "{}",
            }
        );
        var activity = new AdoPrActivityReader.Activity(
            DateTimeOffset.Parse("2026-10-01T10:00:00Z"),
            DateTimeOffset.Parse("2026-10-02T10:00:00Z")
        );
        var key = $"reviewd/pr-snapshot/{repo.NormalizedKey}";
        try
        {
            await PrThreadBindings.SetAsync(database, repo, "17", "thread", CancellationToken.None);
            await PrCommands.RunAsync(
                options,
                provider,
                CancellationToken.None,
                (_, _, _) => Task.FromResult(activity)
            );
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            var first = (await store.GetAsync<Dictionary<string, PrCommands.PrObservation>>(key))!["17"];
            await PrCommands.RunAsync(
                options,
                provider,
                CancellationToken.None,
                (_, _, _) => Task.FromResult(activity)
            );
            var unchanged = (await store.GetAsync<Dictionary<string, PrCommands.PrObservation>>(key))!["17"];
            unchanged.LastChangedAt.Should().Be(first.LastChangedAt);
            unchanged.ObservedAt.Should().BeOnOrAfter(first.ObservedAt!.Value);
            (await PrThreadBindings.GetAsync(database, repo, "17", CancellationToken.None)).Should().Be("thread");
            activity = activity with { LastCommentPostedAt = activity.LastCommentPostedAt!.Value.AddHours(1) };
            await PrCommands.RunAsync(
                options,
                provider,
                CancellationToken.None,
                (_, _, _) => Task.FromResult(activity)
            );
            var changed = (await store.GetAsync<Dictionary<string, PrCommands.PrObservation>>(key))!["17"];
            changed.LastCommentPostedAt.Should().Be(activity.LastCommentPostedAt);
            changed.LastChangedAt.Should().BeOnOrAfter(unchanged.LastChangedAt!.Value);
            var before = await ReadSharedAsync(database);
            var failure = () =>
                PrCommands.RunAsync(
                    options,
                    provider,
                    CancellationToken.None,
                    (_, _, _) => throw new HttpRequestException("unavailable")
                );
            await failure.Should().ThrowAsync<HttpRequestException>();
            (await ReadSharedAsync(database)).Should().Equal(before);
            var multi = CliOptions.Parse([
                "pr",
                "check",
                "--repos",
                "org/project/repo,org/project/broken",
                "--db",
                database,
            ]);
            (
                await PrCommands.RunAsync(
                    multi,
                    provider,
                    CancellationToken.None,
                    (target, _, _) =>
                        target.RepoName == "broken"
                            ? throw new HttpRequestException("unavailable")
                            : Task.FromResult(activity)
                )
            )
                .Should()
                .Be(1);
            (
                await store.GetAsync<Dictionary<string, PrCommands.PrObservation>>(
                    "reviewd/pr-snapshot/azure-devops/org/project/broken"
                )
            )
                .Should()
                .BeNull();
            await PrThreadBindings.SetAsync(database, repo, "17", "thread", CancellationToken.None);
            var conflict = () => PrThreadBindings.SetAsync(database, repo, "17", "other", CancellationToken.None);
            await conflict.Should().ThrowAsync<InvalidOperationException>();
            (await PrThreadBindings.GetAsync(database, repo, "17", CancellationToken.None)).Should().Be("thread");
        }
        finally
        {
            ReviewdSqlitePools.Clear(database);
            File.Delete(database);
        }
    }

    // The store connection is still open here, and on Windows File.ReadAllBytes (FileShare.Read)
    // cannot open a file SQLite holds for writing.
    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    [Fact]
    public void Picking_requires_code_changes_and_thirty_minutes_since_verified_review()
    {
        var now = DateTimeOffset.Parse("2026-10-03T12:00:00Z");
        var pr = System.Text.Json.JsonSerializer.SerializeToElement(
            new
            {
                Lifecycle = "Open",
                IsDraft = false,
                HeadSha = "new-head",
                LastCommentPostedAt = now,
            }
        );
        static System.Text.Json.JsonElement Receipt(
            string head,
            DateTimeOffset completed,
            string stage = "completed-verified"
        ) =>
            System.Text.Json.JsonSerializer.SerializeToElement(
                new
                {
                    stage,
                    reviewedHeadSha = head,
                    reviewCompletedAt = (completed - DateTimeOffset.UnixEpoch).TotalSeconds,
                }
            );

        PrSelection.IsEligible(pr, null, false, now).Should().BeTrue();
        PrSelection.IsEligible(pr, null, true, now).Should().BeFalse();
        PrSelection.IsEligible(pr, Receipt("old-head", now.AddMinutes(-30)), true, now).Should().BeTrue();
        PrSelection
            .IsEligible(pr, Receipt("old-head", now.AddMinutes(-30).AddMilliseconds(1)), true, now)
            .Should()
            .BeFalse();
        PrSelection.IsEligible(pr, Receipt("new-head", now.AddHours(-1)), true, now).Should().BeFalse();
        PrSelection
            .IsEligible(pr, Receipt("old-head", now.AddHours(-1), "review-running"), true, now)
            .Should()
            .BeFalse();
        var draft = System.Text.Json.JsonSerializer.SerializeToElement(
            new
            {
                Lifecycle = "Open",
                IsDraft = true,
                HeadSha = "new-head",
            }
        );
        var closed = System.Text.Json.JsonSerializer.SerializeToElement(
            new
            {
                Lifecycle = "Merged",
                IsDraft = false,
                HeadSha = "new-head",
            }
        );
        PrSelection.IsEligible(draft, null, false, now).Should().BeFalse();
        PrSelection.IsEligible(closed, null, false, now).Should().BeFalse();
    }

    [Fact]
    public void Picking_offers_again_a_failed_attempt_whose_slot_was_released_on_its_claim()
    {
        static System.Text.Json.JsonElement Attempt(string stage, string cleanupState, string claimToken = "claim") =>
            System.Text.Json.JsonSerializer.SerializeToElement(
                new
                {
                    stage,
                    cleanupState,
                    claimToken,
                }
            );

        PrSelection.IsSettled(Attempt("completed-verified", "released")).Should().BeTrue();
        PrSelection.IsSettled(Attempt("failed-released", "released")).Should().BeTrue();
        // Still owned, released without its claim, or an interrupted review that may have posted: not offered.
        PrSelection.IsSettled(Attempt("failed-released", "owned")).Should().BeFalse();
        PrSelection.IsSettled(Attempt("failed-released", "released", "")).Should().BeFalse();
        PrSelection.IsSettled(Attempt("review-running", "released")).Should().BeFalse();
        PrSelection.IsSettled(System.Text.Json.JsonSerializer.SerializeToElement(new { })).Should().BeFalse();
    }

    [Fact]
    public void Picking_treats_a_released_attempt_for_an_earlier_head_as_over()
    {
        static System.Text.Json.JsonElement Attempt(string cleanupState, string claimToken, string? headSha) =>
            System.Text.Json.JsonSerializer.SerializeToElement(
                new
                {
                    stage = "review-completed",
                    cleanupState,
                    claimToken,
                    headSha,
                }
            );
        var earlier = new string('d', 40);
        var live = new string('a', 40);

        PrSelection.IsReleasedForEarlierHead(Attempt("released", "claim", earlier), live).Should().BeTrue();
        // The same head may already have posted. Still owned, released without its claim, or an unknown head: not over.
        PrSelection.IsReleasedForEarlierHead(Attempt("released", "claim", live), live).Should().BeFalse();
        PrSelection
            .IsReleasedForEarlierHead(Attempt("released", "claim", live.ToUpperInvariant()), live)
            .Should()
            .BeFalse();
        PrSelection.IsReleasedForEarlierHead(Attempt("owned", "claim", earlier), live).Should().BeFalse();
        PrSelection.IsReleasedForEarlierHead(Attempt("released", "", earlier), live).Should().BeFalse();
        PrSelection.IsReleasedForEarlierHead(Attempt("released", "claim", null), live).Should().BeFalse();
        PrSelection.IsReleasedForEarlierHead(Attempt("released", "claim", earlier), null).Should().BeFalse();
    }

    [Theory]
    [InlineData('d', true)]
    [InlineData('a', false)]
    public async Task Picking_offers_a_new_head_after_its_slot_was_released_mid_review(char attemptHead, bool picked)
    {
        // The reaper released this review's slot at review-completed; the stage and thread binding stayed.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-pick-{Guid.NewGuid():N}.db");
        var selection = database + ".selection.json";
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var now = DateTimeOffset.Parse("2026-10-17T12:00:00Z");
        try
        {
            using (var connection = PrThreadBindings.Connection(database))
            {
                var store = new SqliteKvStore(connection);
                await store.SetAsync(
                    $"reviewd/pr-snapshot/{repo.NormalizedKey}",
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["17"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["PrId"] = 17,
                            ["Lifecycle"] = "Open",
                            ["IsDraft"] = false,
                            ["HeadSha"] = new string('a', 40),
                            ["CreatedAt"] = now.AddHours(-1).ToString("O"),
                        },
                    }
                );
                await store.SetAsync(
                    $"reviewd/review-attempt/{repo.NormalizedKey}/17",
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["stage"] = "review-completed",
                        ["cleanupState"] = "released",
                        ["claimToken"] = "claim",
                        ["headSha"] = new string(attemptHead, 40),
                    }
                );
            }
            await PrThreadBindings.SetAsync(database, repo, "17", "thread", CancellationToken.None);
            var options = CliOptions.Parse([
                "pr",
                "pick",
                "--repo",
                repo.DisplayName,
                "--db",
                database,
                "--max-age-hours",
                "72",
                "--selection-out",
                selection,
            ]);

            await PrSelection.RunAsync(options, CancellationToken.None, now);

            var queued = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(selection))![
                "repositories"
            ]!.AsObject();
            queued.Count.Should().Be(picked ? 1 : 0);
        }
        finally
        {
            ReviewdSqlitePools.Clear(database);
            File.Delete(database);
            File.Delete(selection);
        }
    }

    [Fact]
    public async Task Activity_uses_publication_not_edit_or_system_and_deleted_comment_times()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/pushes?", """{"count":1,"value":[{"date":"2026-10-02T10:00:00Z"}]}""")
            .OnJson(
                HttpMethod.Get,
                "/threads?",
                """{"count":2,"value":[{"comments":[{"commentType":"text","publishedDate":"2026-10-01T10:00:00Z","lastUpdatedDate":"2026-10-03T10:00:00Z"},{"commentType":"system","publishedDate":"2026-10-03T11:00:00Z"},{"commentType":1,"isDeleted":true,"publishedDate":"2026-10-03T12:00:00Z"},{"commentType":1,"publishedDate":"2026-10-01T11:00:00Z"}]},{"isDeleted":true,"comments":[{"commentType":"text","publishedDate":"2026-10-03T13:00:00Z"}]}]}"""
            )
            .OnJson(HttpMethod.Get, "/pullrequests/17?", """{"sourceRefName":"refs/heads/source"}""");
        using var http = new HttpClient(handler);
        var activity = await new AdoPrActivityReader(http, new FakeOAuthTokenProvider("ado")).ReadAsync(
            PrCommands.ParseRepo("org/project/repo"),
            "17",
            CancellationToken.None
        );
        activity.SourceUpdatedAt.Should().Be(DateTimeOffset.Parse("2026-10-02T10:00:00Z"));
        activity.LastCommentPostedAt.Should().Be(DateTimeOffset.Parse("2026-10-01T11:00:00Z"));
        handler.CountRequests("/threads?").Should().Be(1);
    }
}
