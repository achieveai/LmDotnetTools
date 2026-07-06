namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins <see cref="SandboxCredential"/>'s validation contract (issue #153 M1): standard base64
/// only (URL-safe rejected), a minimum decoded length, and a REDACTED exception message that never
/// echoes the key itself — only the app id and the decoded byte length.
/// </summary>
public class SandboxCredentialTests
{
    private const string AppId = "auth-header-test-app";

    [Fact]
    public void ValidateKeyOrThrow_ValidBase64AtMinimumLength_DoesNotThrow()
    {
        var key = Convert.ToBase64String(new byte[32]);

        var act = () => SandboxCredential.ValidateKeyOrThrow(AppId, key);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateKeyOrThrow_ValidBase64AboveMinimumLength_DoesNotThrow()
    {
        var key = Convert.ToBase64String(new byte[64]);

        var act = () => SandboxCredential.ValidateKeyOrThrow(AppId, key);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateKeyOrThrow_TooShort_ThrowsWithAppIdAndByteLength_NeverTheKey()
    {
        var key = Convert.ToBase64String(new byte[16]); // decodes to 16 bytes, below the 32-byte floor

        var act = () => SandboxCredential.ValidateKeyOrThrow(AppId, key);

        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Contain(AppId);
        ex.Message.Should().Contain("16");
        ex.Message.Should().NotContain(key);
    }

    [Fact]
    public void ValidateKeyOrThrow_MalformedBase64_ThrowsWithoutEchoingTheKey()
    {
        const string malformed = "not-valid-base64!!!";

        var act = () => SandboxCredential.ValidateKeyOrThrow(AppId, malformed);

        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Contain(AppId);
        ex.Message.Should().NotContain(malformed);
    }

    [Fact]
    public void ValidateKeyOrThrow_UrlSafeBase64Alphabet_Throws()
    {
        // Standard base64 never contains '_'; Convert.FromBase64String rejects it as an invalid
        // character rather than silently accepting the URL-safe ('-'/'_') alphabet.
        var standard = Convert.ToBase64String(Enumerable.Repeat((byte)0xFF, 33).ToArray());
        var urlSafe = standard.Replace('/', '_');
        urlSafe
            .Should()
            .NotBe(
                standard,
                "the transform must actually introduce a URL-safe character for this test to be meaningful"
            );

        var act = () => SandboxCredential.ValidateKeyOrThrow(AppId, urlSafe);

        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().NotContain(urlSafe);
    }

    [Fact]
    public void ValidateKeyOrThrow_BlankKey_ThrowsWithAppIdOnly()
    {
        var act1 = () => SandboxCredential.ValidateKeyOrThrow(AppId, "");
        var act2 = () => SandboxCredential.ValidateKeyOrThrow(AppId, "   ");

        act1.Should().Throw<ArgumentException>().Which.Message.Should().Contain(AppId);
        act2.Should().Throw<ArgumentException>().Which.Message.Should().Contain(AppId);
    }

    [Fact]
    public void FromOptions_AppKeyUnset_ReturnsNull()
    {
        var options = new SandboxGatewayOptions { AppId = AppId, AppKey = null };

        SandboxCredential.FromOptions(options).Should().BeNull();
    }

    [Fact]
    public void FromOptions_AppKeyBlank_ReturnsNull()
    {
        var options = new SandboxGatewayOptions { AppId = AppId, AppKey = "   " };

        SandboxCredential.FromOptions(options).Should().BeNull();
    }

    [Fact]
    public void FromOptions_ValidAppKey_ReturnsCredentialMatchingOptions()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var options = new SandboxGatewayOptions { AppId = AppId, AppKey = key };

        var credential = SandboxCredential.FromOptions(options);

        credential.Should().NotBeNull();
        credential!.Value.AppId.Should().Be(AppId);
        credential.Value.AppKey.Should().Be(key);
    }

    [Fact]
    public void FromOptions_InvalidAppKey_Throws()
    {
        var options = new SandboxGatewayOptions { AppId = AppId, AppKey = "AAAA" }; // valid base64, only 3 bytes

        var act = () => SandboxCredential.FromOptions(options);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromOptions_NullOptions_Throws()
    {
        var act = () => SandboxCredential.FromOptions(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
