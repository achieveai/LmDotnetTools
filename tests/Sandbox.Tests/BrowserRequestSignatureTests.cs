using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// The pinned-egress signature (gateway <c>browser/egress.rs</c> <c>signature()</c>, ADR 0038), checked
/// against the gateway's own pinned test vector.
/// </summary>
public sealed class BrowserRequestSignatureTests
{
    // egress.rs signature_is_stable_hmac_sha256, shared with the harness verifier's test.
    private const string GatewayVector = "t=1,v1=719f54c2b79941d2039be12e5b40e53685e1c90fab7bf01cf24346e59543458d";
    private static readonly DateTimeOffset At1 = DateTimeOffset.FromUnixTimeSeconds(1);

    [Fact]
    public void Sign_MatchesTheGatewaysPinnedVector()
    {
        BrowserRequestSignature.Sign("k", 1, "GET", "https://a/", "app", "b", "i").Should().Be(GatewayVector);
    }

    [Theory]
    [InlineData(-300, BrowserSignatureVerdict.Valid)]
    [InlineData(300, BrowserSignatureVerdict.Valid)]
    [InlineData(-301, BrowserSignatureVerdict.Stale)]
    [InlineData(301, BrowserSignatureVerdict.Stale)]
    public void Verify_AcceptsAtMostFiveMinutesOfSkewEitherWay(int nowOffsetSeconds, BrowserSignatureVerdict expected)
    {
        BrowserRequestSignature
            .Verify("k", GatewayVector, "GET", "https://a/", "app", "b", "i", At1.AddSeconds(nowOffsetSeconds))
            .Should()
            .Be(expected);
    }

    [Theory]
    [InlineData("POST", "https://a/", "app", "b", "i")]
    [InlineData("GET", "https://a/x", "app", "b", "i")]
    [InlineData("GET", "https://a/", "other-app", "b", "i")]
    [InlineData("GET", "https://a/", "app", "other-browser", "i")]
    [InlineData("GET", "https://a/", "app", "b", "other-instance")]
    public void Verify_EverySignedFieldIsCovered(
        string method,
        string originalUrl,
        string app,
        string browser,
        string instance
    )
    {
        BrowserRequestSignature
            .Verify("k", GatewayVector, method, originalUrl, app, browser, instance, At1)
            .Should()
            .Be(BrowserSignatureVerdict.Mismatch);
    }

    [Fact]
    public void Verify_AnotherSecretIsAMismatch()
    {
        BrowserRequestSignature
            .Verify("not-k", GatewayVector, "GET", "https://a/", "app", "b", "i", At1)
            .Should()
            .Be(BrowserSignatureVerdict.Mismatch);
    }

    [Theory]
    [InlineData(null, BrowserSignatureVerdict.Missing)]
    [InlineData("", BrowserSignatureVerdict.Missing)]
    [InlineData("t=1", BrowserSignatureVerdict.Malformed)]
    [InlineData("t=1,v1=719f", BrowserSignatureVerdict.Malformed)]
    [InlineData(
        "t=-1,v1=719f54c2b79941d2039be12e5b40e53685e1c90fab7bf01cf24346e59543458d",
        BrowserSignatureVerdict.Malformed
    )]
    [InlineData(
        "t=1,v1=zz9f54c2b79941d2039be12e5b40e53685e1c90fab7bf01cf24346e59543458d",
        BrowserSignatureVerdict.Malformed
    )]
    [InlineData(
        "v1=719f54c2b79941d2039be12e5b40e53685e1c90fab7bf01cf24346e59543458d,t=1",
        BrowserSignatureVerdict.Malformed
    )]
    [InlineData(
        "t=1,v1=719f54c2b79941d2039be12e5b40e53685e1c90fab7bf01cf24346e59543458d,x=1",
        BrowserSignatureVerdict.Malformed
    )]
    public void Verify_RejectsMissingAndMalformedValues(string? signature, BrowserSignatureVerdict expected)
    {
        BrowserRequestSignature.Verify("k", signature, "GET", "https://a/", "app", "b", "i", At1).Should().Be(expected);
    }
}
