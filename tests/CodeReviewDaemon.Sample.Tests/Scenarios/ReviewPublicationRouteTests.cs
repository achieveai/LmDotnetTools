using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class ReviewPublicationRouteTests
{
    private const string Secret = "publication-bridge-secret";
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Disabled_publication_route_returns_not_found()
    {
        using var factory = new DaemonWebAppFactory(reviewBridgeSecret: Secret);
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, 1, "CreateRootSummary", new { });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-secret")]
    public async Task Enabled_route_requires_the_bridge_secret(string? presentedSecret)
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/review-publication/rounds/1/actions/CreateRootSummary"
        )
        {
            Content = JsonContent.Create(new { }),
        };
        if (presentedSecret is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Review-Bridge-Auth", presentedSecret);
        }

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_operation_is_rejected_without_exposing_an_extra_route()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, 1, "RawProviderRest", new { });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("unsupported_operation");
    }

    [Fact]
    public async Task Collect_only_root_returns_a_typed_outcome_and_no_provider_body()
    {
        using var factory = NewFactory();
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var roundId = SeedRunningRound(store);
        using var client = factory.CreateClient();
        var body = new
        {
            scope = new
            {
                roundId,
                actionId = "root-1",
                provider = "github",
                repoId = store.GetEngagementRound(roundId)!.PrEngagementId is var engagementId
                    ? store.GetEngagement(engagementId)!.RepoId
                    : 0,
                prId = "118",
                expectedHeadSha = "head-1",
                sources = new[] { new { sourceRecordId = "source-1", contentSha256 = Sha256("evidence"u8) } },
                livePostingAuthorized = false,
            },
            body = "## Review\nSupported finding.",
        };

        using var response = await SendAsync(client, roundId, "CreateRootSummary", body);
        var outcome = await response.Content.ReadFromJsonAsync<PublicationOutcome>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        outcome.Should().NotBeNull();
        outcome!.Status.Should().Be(ReviewActionStatus.CollectedOnly);
        outcome.ActionId.Should().Be("root-1");
        outcome.RejectionCode.Should().BeNull();
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Exception");
    }

    [Fact]
    public async Task Mismatched_route_round_and_body_round_returns_typed_conflict()
    {
        using var factory = NewFactory();
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var roundId = SeedRunningRound(store);
        var engagement = store.GetEngagement(store.GetEngagementRound(roundId)!.PrEngagementId)!;
        using var client = factory.CreateClient();
        var body = new RootSummaryRequest(
            new PublicationScope(
                roundId,
                "root-1",
                "github",
                engagement.RepoId,
                "118",
                "head-1",
                [new AuditSourceReference("source-1", Sha256("evidence"u8))]
            ),
            "body"
        );

        using var response = await SendAsync(client, roundId + 1, "CreateRootSummary", body);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("round_id_mismatch");
    }

    private static DaemonWebAppFactory NewFactory() =>
        new(enableTypedReviewPublication: true, reviewBridgeSecret: Secret);

    private static async Task<HttpResponseMessage> SendAsync<T>(
        HttpClient client,
        long roundId,
        string operation,
        T body
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/review-publication/rounds/{roundId}/actions/{operation}"
        )
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("X-Review-Bridge-Auth", Secret);
        return await client.SendAsync(request);
    }

    private static long SeedRunningRound(ReviewStore store)
    {
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
                RepoStableId = "R_node_123",
            }
        );
        var engagement = store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                "github",
                "118",
                PrLifecycleState.Open,
                "head-1",
                "base-1",
                null,
                new ProviderActivityWatermark("github", Now, "comment-1"),
                null,
                null,
                null,
                null,
                null,
                null,
                Now
            )
        );
        var round = store.TryAdmitRound(
            new EngagementRound(
                0,
                engagement.Id,
                EngagementRoundIntent.CodeReview,
                EngagementRoundStatus.Pending,
                "head-1",
                "base-1",
                null,
                engagement.LatestActivity,
                0,
                null,
                0,
                null,
                null,
                null,
                null,
                null
            )
        )!;
        store.TryTransitionEngagementRound(round.Id, EngagementRoundStatus.Pending, EngagementRoundStatus.Running, Now);
        _ = store.StoreAuditRecord(
            new ModelTurnAuditRecord(
                "source-1",
                new MultiTurnAuditScope(engagement.Id.ToString(), round.Id.ToString()),
                "thread-1",
                "run-1",
                "generation-1",
                null,
                1,
                MultiTurnAuditRecordTypes.ModelResponse,
                "assistant",
                "claude-opus-5",
                "anthropic",
                "evidence"u8.ToArray(),
                Sha256("evidence"u8),
                "evidence"u8.Length,
                AuditCaptureOutcome.Complete,
                null,
                Now
            )
        );
        return round.Id;
    }

    private static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
