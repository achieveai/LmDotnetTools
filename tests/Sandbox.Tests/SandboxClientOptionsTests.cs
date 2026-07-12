using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

public class SandboxClientOptionsTests
{
    private static readonly string ValidSecret = Convert.ToBase64String(new byte[32]);

    private static SandboxClientOptions Build(
        string? serverAddress = "https://sandbox.example.com",
        string appId = "app-1",
        string? clientSecret = null,
        bool allowInsecure = false
    ) =>
        new(
            new Uri(serverAddress!),
            appId,
            clientSecret ?? ValidSecret,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(30),
            allowInsecure
        );

    [Fact]
    public void Constructor_WithValidArguments_PopulatesProperties()
    {
        var options = Build();

        options.ServerAddress.Should().Be(new Uri("https://sandbox.example.com"));
        options.AppId.Should().Be("app-1");
        options.ClientSecret.Should().Be(ValidSecret);
        options.ExecutionTimeout.Should().Be(TimeSpan.FromMinutes(5));
        options.TransportTimeout.Should().Be(TimeSpan.FromSeconds(30));
        options.AllowInsecureDevelopmentTransport.Should().BeFalse();
    }

    [Fact]
    public void Constructor_NullServerAddress_Throws()
    {
        var act = () =>
            new SandboxClientOptions(null!, "app-1", ValidSecret, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_RelativeServerAddress_Throws()
    {
        var act = () =>
            new SandboxClientOptions(
                new Uri("/relative", UriKind.Relative),
                "app-1",
                ValidSecret,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromSeconds(30)
            );

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("ftp://sandbox.example.com")]
    [InlineData("ws://sandbox.example.com")]
    public void Constructor_NonHttpScheme_Throws(string serverAddress)
    {
        var act = () => Build(serverAddress);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("http://127.0.0.1:3000")]
    [InlineData("http://localhost:3000")]
    [InlineData("http://[::1]:3000")]
    public void Constructor_PlainHttpOnLoopback_IsAllowedWithoutOptIn(string serverAddress)
    {
        var act = () => Build(serverAddress);

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_PlainHttpOnNonLoopback_WithoutOptIn_Throws()
    {
        var act = () => Build("http://sandbox.internal:3000");

        act.Should().Throw<ArgumentException>().WithMessage("*HTTPS*");
    }

    [Fact]
    public void Constructor_PlainHttpOnNonLoopback_WithExplicitOptIn_IsAllowed()
    {
        var act = () => Build("http://sandbox.internal:3000", allowInsecure: true);

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_HttpsOnNonLoopback_IsAlwaysAllowed()
    {
        var act = () => Build("https://sandbox.internal:3443");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Constructor_BlankAppId_Throws(string? appId)
    {
        var act = () => Build(appId: appId!);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankClientSecret_Throws(string secret)
    {
        var act = () => Build(clientSecret: secret);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullClientSecret_Throws()
    {
        var act = () =>
            new SandboxClientOptions(
                new Uri("https://sandbox.example.com"),
                "app-1",
                null!,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromSeconds(30)
            );

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_NonBase64ClientSecret_Throws()
    {
        var act = () => Build(clientSecret: "not-valid-base64!!!");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_UrlSafeBase64ClientSecret_Throws()
    {
        // URL-safe base64 substitutes '-'/'_' for '+'/'/' — Convert.FromBase64String (standard-only)
        // rejects both characters regardless of surrounding content, so a fixed literal containing
        // either is enough to exercise the rejection.
        var act = () => Build(clientSecret: "QUJDRERFRkdISUpLTE1OT1BRUlNUVVZX-_1234567890==");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_TooShortClientSecret_Throws()
    {
        var act = () => Build(clientSecret: Convert.ToBase64String(new byte[8]));

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveExecutionTimeout_Throws(int seconds)
    {
        var act = () =>
            new SandboxClientOptions(
                new Uri("https://sandbox.example.com"),
                "app-1",
                ValidSecret,
                TimeSpan.FromSeconds(seconds),
                TimeSpan.FromSeconds(30)
            );

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveTransportTimeout_Throws(int seconds)
    {
        var act = () =>
            new SandboxClientOptions(
                new Uri("https://sandbox.example.com"),
                "app-1",
                ValidSecret,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromSeconds(seconds)
            );

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToString_NeverIncludesClientSecret()
    {
        var options = Build(clientSecret: ValidSecret);

        var rendered = options.ToString();

        rendered.Should().NotContain(ValidSecret);
        rendered.Should().Contain("REDACTED");
    }

    [Fact]
    public void Constructor_BlankClientSecretException_NeverIncludesSecretValue()
    {
        const string secret = "not-valid-base64!!!";

        var act = () => Build(clientSecret: secret);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.Message.Should().NotContain(secret);
    }
}
