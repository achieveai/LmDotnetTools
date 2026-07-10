using CodeReviewDaemon.Sample.Configuration;

namespace CodeReviewDaemon.Sample.Tests.Configuration;

/// <summary>
/// The `--review &lt;name&gt;` operator flag (feature: config profiles) selects the hosting environment so
/// ASP.NET layers appsettings.&lt;name&gt;.json. These pin the extraction contract: the flag pair is stripped
/// from the host args, and malformed/absent usage falls back to the default environment.
/// </summary>
public sealed class ReviewProfileArgsTests
{
    [Fact]
    public void NoFlag_ReturnsNullProfileAndArgsUnchanged()
    {
        var (profile, hostArgs) = ReviewProfileArgs.Extract(["reviewbot", "init"]);

        profile.Should().BeNull();
        hostArgs.Should().Equal("reviewbot", "init");
    }

    [Fact]
    public void Flag_ExtractsProfileAndStripsThePair()
    {
        var (profile, hostArgs) = ReviewProfileArgs.Extract(["--review", "mcqdb", "--other", "x"]);

        profile.Should().Be("mcqdb");
        hostArgs.Should().Equal("--other", "x");
    }

    [Fact]
    public void TrailingFlagWithoutValue_IsIgnored()
    {
        var (profile, hostArgs) = ReviewProfileArgs.Extract(["--review"]);

        profile.Should().BeNull();
        hostArgs.Should().Equal("--review");
    }
}
