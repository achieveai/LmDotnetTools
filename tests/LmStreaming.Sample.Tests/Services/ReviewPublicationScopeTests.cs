using System.Collections.Immutable;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The scope is the only grant of the typed publication surface, so every way it can be incomplete has
/// to resolve to "no scope" rather than to a half-addressed one.
/// </summary>
public sealed class ReviewPublicationScopeTests
{
    private static ReviewPublicationScopeRequest Valid() =>
        new()
        {
            RoundId = 7,
            Provider = "github",
            RepoId = 1234,
            PrId = "118",
            ExpectedHeadSha = "abc123",
            LivePostingAuthorized = true,
        };

    [Fact]
    public void TryCreate_AcceptsAndTrimsACompleteScope()
    {
        var scope = ReviewPublicationScope.TryCreate(Valid() with { PrId = "  118  " });

        scope.Should().NotBeNull();
        scope!.RoundId.Should().Be(7);
        scope.RepoId.Should().Be(1234);
        scope.PrId.Should().Be("118");
        scope.LivePostingAuthorized.Should().BeTrue();
    }

    /// <summary>
    /// The collect-only default. An omitted flag must not read as authorization to write to the PR.
    /// </summary>
    [Fact]
    public void TryCreate_DefaultsLivePostingToNotAuthorized()
    {
        var request = new ReviewPublicationScopeRequest
        {
            RoundId = 7,
            Provider = "github",
            RepoId = 1234,
            PrId = "118",
            ExpectedHeadSha = "abc123",
        };

        ReviewPublicationScope.TryCreate(request)!.LivePostingAuthorized.Should().BeFalse();
    }

    [Fact]
    public void TryCreate_RejectsANullRequest() => ReviewPublicationScope.TryCreate(null).Should().BeNull();

    [Theory]
    [InlineData("Provider")]
    [InlineData("PrId")]
    [InlineData("ExpectedHeadSha")]
    public void TryCreate_RejectsABlankRequiredField(string field)
    {
        var request = field switch
        {
            "Provider" => Valid() with { Provider = null },
            "PrId" => Valid() with { PrId = "   " },
            _ => Valid() with { ExpectedHeadSha = "" },
        };

        ReviewPublicationScope.TryCreate(request).Should().BeNull();
    }

    /// <summary>
    /// The daemon's own <c>ValidateScope</c> rejects <c>RoundId &lt;= 0</c> as <c>invalid_scope</c>, so a
    /// surface built on one would be refused on every call. Fail at provision instead.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TryCreate_RejectsANonPositiveRoundId(long roundId) =>
        ReviewPublicationScope.TryCreate(Valid() with { RoundId = roundId }).Should().BeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TryCreate_RejectsANonPositiveRepoId(long repoId) =>
        ReviewPublicationScope.TryCreate(Valid() with { RepoId = repoId }).Should().BeNull();

    [Fact]
    public void PersistedValue_RoundTrips()
    {
        var scope = ReviewPublicationScope.TryCreate(Valid())!;

        ReviewPublicationScope.Parse(scope.ToPropertyValue()).Should().Be(scope);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"RoundId\":7}")]
    public void Parse_FailsClosedOnAnUnusablePersistedValue(string persisted) =>
        ReviewPublicationScope.Parse(persisted).Should().BeNull();

    /// <summary>
    /// The production store round-trips the property bag through JSON, so a value written as a string
    /// comes back as a <see cref="JsonElement"/>. A reader that only handles the in-memory form returns
    /// null for every scope that has actually been persisted — see <c>ThreadPropertyValue</c>.
    /// </summary>
    [Fact]
    public async Task ReadAsync_ReadsTheScopeBackAfterAJsonRoundTrip()
    {
        var scope = ReviewPublicationScope.TryCreate(Valid())!;
        var stored = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(scope.ToPropertyValue()));
        var store = StoreWith(ReviewPublicationScope.PropertyKey, stored);

        (await ReviewPublicationScope.ReadAsync(store, "thread-1")).Should().Be(scope);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNullWhenTheThreadCarriesNoScope()
    {
        var store = StoreWith("sample.somethingElse", "value");

        (await ReviewPublicationScope.ReadAsync(store, "thread-1")).Should().BeNull();
    }

    [Fact]
    public async Task ReadAsync_ReturnsNullForAnUnknownThread()
    {
        var store = new Mock<IConversationStore>();
        _ = store
            .Setup(s => s.LoadMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ThreadMetadata?)null);

        (await ReviewPublicationScope.ReadAsync(store.Object, "missing")).Should().BeNull();
    }

    private static IConversationStore StoreWith(string key, object value)
    {
        var store = new Mock<IConversationStore>();
        _ = store
            .Setup(s => s.LoadMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new ThreadMetadata
                {
                    ThreadId = "thread-1",
                    LastUpdated = 0,
                    Properties = ImmutableDictionary<string, object>.Empty.Add(key, value),
                }
            );
        return store.Object;
    }
}
