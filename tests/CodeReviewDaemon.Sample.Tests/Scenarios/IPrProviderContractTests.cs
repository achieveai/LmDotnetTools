using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Pins <see cref="IPrProvider.GetPrStateAsync"/> as part of the provider seam (not a GitHub-only member):
/// the PR-lifecycle sweep resolves each reviewed PR's terminal state through the interface, so any fake or
/// real provider must expose it. Driving the shared <see cref="MockPrProvider"/> double purely through the
/// <see cref="IPrProvider"/> reference proves the member is callable off the abstraction.
/// </summary>
public sealed class IPrProviderContractTests
{
    private static readonly RepoIdentity Repo = new()
    {
        Provider = "github",
        OrgOrOwner = "acme",
        RepoName = "widgets",
    };

    private static IPrProvider Provider(PrLifecycle state) =>
        new MockPrProvider("github", [], Cursor()) { PrState = state };

    [Fact]
    public async Task GetPrState_is_callable_through_the_interface()
    {
        (await Provider(PrLifecycle.Open).GetPrStateAsync(Repo, "42", CancellationToken.None))
            .Should().Be(PrLifecycle.Open);
        (await Provider(PrLifecycle.Merged).GetPrStateAsync(Repo, "42", CancellationToken.None))
            .Should().Be(PrLifecycle.Merged);
        (await Provider(PrLifecycle.Abandoned).GetPrStateAsync(Repo, "42", CancellationToken.None))
            .Should().Be(PrLifecycle.Abandoned);
    }

    private static OpaqueCursor Cursor() => new()
    {
        Provider = "github",
        Scope = "acme/widgets:open-prs",
        CursorVersion = PrPollingService.CursorVersion,
        CursorPayload = "{}",
        HighWaterMark = null,
    };
}
