extern alias Reviewd;

using AchieveAi.LmDotnetTools.Misc.Storage;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class ReviewdPrTrackingTests
{
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
            var before = await File.ReadAllBytesAsync(database);
            var failure = () =>
                PrCommands.RunAsync(
                    options,
                    provider,
                    CancellationToken.None,
                    (_, _, _) => throw new HttpRequestException("unavailable")
                );
            await failure.Should().ThrowAsync<HttpRequestException>();
            (await File.ReadAllBytesAsync(database)).Should().Equal(before);
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
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
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
