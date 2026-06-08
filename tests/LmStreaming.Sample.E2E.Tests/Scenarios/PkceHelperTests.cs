using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using LmStreaming.Sample.Services.Auth;
using Xunit.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Unit tests for <see cref="PkceHelper"/>. Pins the wire-shape contract every consumer relies on:
/// 32-byte (256-bit) entropy verifiers, RFC 7636 §A base64url encoding (no padding, '+' → '-', '/' → '_'),
/// and the canonical S256 challenge transform.
/// </summary>
public sealed class PkceHelperTests : LoggingTestBase
{
    public PkceHelperTests(ITestOutputHelper output)
        : base(output)
    {
    }

    [Fact]
    public void Code_verifier_is_base64url_with_256_bits_of_entropy()
    {
        LogTestStart();
        var verifier = PkceHelper.CreateCodeVerifier();
        Logger.LogInformation("Verifier length {Length}", verifier.Length);

        // base64url(32 bytes) = 43 chars (no padding).
        verifier.Length.Should().Be(43);
        verifier.Should().NotContain("=").And.NotContain("+").And.NotContain("/");
        verifier.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        LogTestEnd();
    }

    [Fact]
    public void State_is_base64url_with_256_bits_of_entropy()
    {
        LogTestStart();
        var state = PkceHelper.CreateState();
        Logger.LogInformation("State length {Length}", state.Length);

        state.Length.Should().Be(43);
        state.Should().NotContain("=").And.NotContain("+").And.NotContain("/");
        state.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        LogTestEnd();
    }

    [Fact]
    public void Two_calls_produce_distinct_verifiers()
    {
        LogTestStart();
        var first = PkceHelper.CreateCodeVerifier();
        var second = PkceHelper.CreateCodeVerifier();

        // Birthday-paradox collision is vanishingly small with 256 bits — distinct is the only safe expectation.
        first.Should().NotBe(second);
        LogTestEnd();
    }

    [Fact]
    public void Code_challenge_matches_RFC_7636_Appendix_B_S256_vector()
    {
        LogTestStart();
        // RFC 7636 Appendix B normative test vector.
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string expectedChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        var challenge = PkceHelper.CreateCodeChallenge(verifier);
        Logger.LogInformation("Computed S256 challenge length {Length}", challenge.Length);

        challenge.Should().Be(expectedChallenge);
        LogTestEnd();
    }

    [Fact]
    public void Base64Url_strips_padding_and_remaps_unsafe_chars()
    {
        LogTestStart();
        // Bytes whose Base64 representation contains all three problematic characters (+, /, =).
        var bytes = new byte[] { 0xFB, 0xFF, 0xBF };
        var standard = Convert.ToBase64String(bytes); // "+/+/"... padding-less here (3 bytes), but '+' and '/' present.

        Logger.LogInformation("Standard base64 of fixture: {Standard}", standard);
        standard.Should().Contain("+").And.Contain("/");

        var url = PkceHelper.Base64Url(bytes);
        Logger.LogInformation("base64url of fixture: {Url}", url);

        url.Should().NotContain("=").And.NotContain("+").And.NotContain("/");
        url.Should().Be(standard.Replace('+', '-').Replace('/', '_').TrimEnd('='));
        LogTestEnd();
    }

    [Fact]
    public void Code_challenge_equals_manual_S256_transform()
    {
        LogTestStart();
        var verifier = PkceHelper.CreateCodeVerifier();
        var expected = PkceHelper.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        PkceHelper.CreateCodeChallenge(verifier).Should().Be(expected);
        LogTestEnd();
    }
}
