using CodeReviewDaemon.Sample.Configuration;

namespace CodeReviewDaemon.Sample.Tests.Configuration;

/// <summary>
/// The operator command-line flags parsed before the host builder runs. <c>--review &lt;name&gt;</c>
/// (feature: config profiles) selects the hosting environment so ASP.NET layers appsettings.&lt;name&gt;.json;
/// <c>--days &lt;N&gt;</c> / <c>--max-pr-age-days &lt;N&gt;</c> overrides the PR recency bound. These pin the
/// extraction contract: recognized flag pairs are stripped from the host args, and malformed/absent usage
/// falls back to the default (no profile / no override) with the tokens left untouched.
/// </summary>
public sealed class ReviewProfileArgsTests
{
    [Fact]
    public void NoFlag_ReturnsNullProfileAndArgsUnchanged()
    {
        var (profile, maxPrAgeDays, hostArgs) = ReviewProfileArgs.Extract(["reviewbot", "init"]);

        profile.Should().BeNull();
        maxPrAgeDays.Should().BeNull();
        hostArgs.Should().Equal("reviewbot", "init");
    }

    [Fact]
    public void Flag_ExtractsProfileAndStripsThePair()
    {
        var (profile, maxPrAgeDays, hostArgs) = ReviewProfileArgs.Extract(["--review", "mcqdb", "--other", "x"]);

        profile.Should().Be("mcqdb");
        maxPrAgeDays.Should().BeNull();
        hostArgs.Should().Equal("--other", "x");
    }

    [Fact]
    public void TrailingFlagWithoutValue_IsIgnored()
    {
        var (profile, maxPrAgeDays, hostArgs) = ReviewProfileArgs.Extract(["--review"]);

        profile.Should().BeNull();
        maxPrAgeDays.Should().BeNull();
        hostArgs.Should().Equal("--review");
    }

    [Fact]
    public void DaysFlag_ExtractsTheOverrideAndStripsThePair()
    {
        var (profile, maxPrAgeDays, hostArgs) = ReviewProfileArgs.Extract(["--review", "mcqdb", "--days", "3"]);

        profile.Should().Be("mcqdb");
        maxPrAgeDays.Should().Be(3);
        hostArgs.Should().BeEmpty("both flag pairs are stripped");
    }

    [Fact]
    public void MaxPrAgeDaysAlias_IsAccepted()
    {
        var (_, maxPrAgeDays, hostArgs) = ReviewProfileArgs.Extract(["--max-pr-age-days", "14", "keep"]);

        maxPrAgeDays.Should().Be(14);
        hostArgs.Should().Equal("keep");
    }

    [Fact]
    public void NonIntegerDaysValue_IsIgnoredAndTokensLeftUntouched()
    {
        var (_, maxPrAgeDays, hostArgs) = ReviewProfileArgs.Extract(["--days", "soon"]);

        maxPrAgeDays.Should().BeNull("a non-integer value is not a valid recency bound");
        hostArgs.Should().Equal("--days", "soon");
    }

    [Fact]
    public void TrailingDaysFlagWithoutValue_IsIgnored()
    {
        var (_, maxPrAgeDays, hostArgs) = ReviewProfileArgs.Extract(["--days"]);

        maxPrAgeDays.Should().BeNull();
        hostArgs.Should().Equal("--days");
    }
}
