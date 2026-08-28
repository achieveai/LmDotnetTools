namespace LmStreaming.Sample.Tests.Persistence;

/// <summary>
/// Unit tests for <see cref="GatewayWorkspaceCatalogIdentity"/> and <see cref="GatewayWorkspaceCatalogManifest"/>.
/// Tests URL canonicalization, key derivation, and manifest validation.
/// </summary>
public class GatewayWorkspaceCatalogIdentityTests
{
    private const int SchemaVersion = 1;
    private const int DerivationVersion = 1;

    /// <summary>
    /// Tests that URLs are canonicalized according to spec: lowercase domain, strip default ports,
    /// remove trailing slashes, normalize path.
    /// </summary>
    [Theory]
    [InlineData("HTTP://Example.COM:80/", "http://example.com")]
    [InlineData("https://Example.COM:443/", "https://example.com")]
    [InlineData("http://example.com:3000/", "http://example.com:3000")]
    [InlineData("https://example.com/base/", "https://example.com/base")]
    [InlineData("https://example.com/base//", "https://example.com/base")]
    public void Create_CanonicalizesExpectedUrl(string input, string expected)
    {
        var identity = GatewayWorkspaceCatalogIdentity.Create(input, "app1");

        identity.CanonicalBaseUrl.Should().Be(expected);
    }

    /// <summary>
    /// Test that different ports yield different catalog keys.
    /// </summary>
    [Fact]
    public void Create_DifferentPorts_YieldDifferentKeys()
    {
        var identity1 = GatewayWorkspaceCatalogIdentity.Create("http://example.com:3000/", "app1");
        var identity2 = GatewayWorkspaceCatalogIdentity.Create("http://example.com:3001/", "app1");

        identity1.CatalogKey.Should().NotBe(identity2.CatalogKey);
    }

    /// <summary>
    /// Test that different paths yield different catalog keys.
    /// </summary>
    [Fact]
    public void Create_DifferentPaths_YieldDifferentKeys()
    {
        var identity1 = GatewayWorkspaceCatalogIdentity.Create("https://example.com/path1/", "app1");
        var identity2 = GatewayWorkspaceCatalogIdentity.Create("https://example.com/path2/", "app1");

        identity1.CatalogKey.Should().NotBe(identity2.CatalogKey);
    }

    /// <summary>
    /// Test that different AppIds yield different catalog keys.
    /// </summary>
    [Fact]
    public void Create_DifferentAppIds_YieldDifferentKeys()
    {
        var identity1 = GatewayWorkspaceCatalogIdentity.Create("https://example.com/", "app1");
        var identity2 = GatewayWorkspaceCatalogIdentity.Create("https://example.com/", "app2");

        identity1.CatalogKey.Should().NotBe(identity2.CatalogKey);
    }

    /// <summary>
    /// Test that query string variations yield different catalog keys (query string order/content matters).
    /// </summary>
    [Fact]
    public void Create_DifferentQueries_YieldDifferentKeys()
    {
        var identity1 = GatewayWorkspaceCatalogIdentity.Create("https://example.com/?a=1&b=2", "app1");
        var identity2 = GatewayWorkspaceCatalogIdentity.Create("https://example.com/?b=2&a=1", "app1");

        // Query order should matter for strict canonicalization
        identity1.CatalogKey.Should().NotBe(identity2.CatalogKey);
    }

    /// <summary>
    /// Test that localhost and 127.0.0.1 are treated as different hosts.
    /// </summary>
    [Fact]
    public void Create_LocalhostVs127_0_0_1_YieldDifferentKeys()
    {
        var identity1 = GatewayWorkspaceCatalogIdentity.Create("http://localhost:3000/", "app1");
        var identity2 = GatewayWorkspaceCatalogIdentity.Create("http://127.0.0.1:3000/", "app1");

        identity1.CatalogKey.Should().NotBe(identity2.CatalogKey);
    }

    /// <summary>
    /// Test with hard-coded expected SHA-256 digest to verify correct null-delimited format.
    /// Material: "gateway-workspace-catalog:v1\0https://example.com\0myapp"
    /// This test catches deviations from the spec format.
    /// </summary>
    [Fact]
    public void Create_ComputesCatalogKey_WithCorrectNullDelimitedFormat()
    {
        var identity = GatewayWorkspaceCatalogIdentity.Create("https://example.com", "myapp");

        // Expected: SHA-256("gateway-workspace-catalog:v1\0https://example.com\0myapp") in lowercase hex
        // Computed via: using System.Security.Cryptography; using System.Text;
        // var material = "gateway-workspace-catalog:v1\0https://example.com\0myapp";
        // var bytes = Encoding.UTF8.GetBytes(material);
        // using var sha256 = SHA256.Create();
        // var hash = sha256.ComputeHash(bytes);
        // var key = Convert.ToHexString(hash).ToLowerInvariant();
        var expectedKey = "4617ad15ca8ca401b40f4f635ca7e94a9cf5987f4659cd3f213ad8d7b341eae1";

        identity.CatalogKey.Should().Be(expectedKey);
    }

    /// <summary>
    /// Test that relative URLs throw an exception.
    /// </summary>
    [Fact]
    public void Create_RelativeUrl_Throws()
    {
        Action act = () => GatewayWorkspaceCatalogIdentity.Create("/relative/path", "app1");

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Test that FTP URLs throw an exception.
    /// </summary>
    [Fact]
    public void Create_FtpUrl_Throws()
    {
        Action act = () => GatewayWorkspaceCatalogIdentity.Create("ftp://example.com/", "app1");

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Test that URLs with user info throw an exception.
    /// </summary>
    [Fact]
    public void Create_UrlWithUserInfo_Throws()
    {
        Action act = () => GatewayWorkspaceCatalogIdentity.Create("https://user:pass@example.com/", "app1");

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Test that URL fragments are rejected.
    /// </summary>
    [Fact]
    public void Create_UrlWithFragment_Throws()
    {
        Action act = () => GatewayWorkspaceCatalogIdentity.Create("https://example.com/path#fragment", "app1");

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Test that CatalogKey is a valid SHA-256 hex string (64 chars, lowercase).
    /// </summary>
    [Fact]
    public void Create_CatalogKey_IsSha256Hex()
    {
        var identity = GatewayWorkspaceCatalogIdentity.Create("https://example.com/", "app1");

        identity.CatalogKey.Should().HaveLength(64);
        identity.CatalogKey.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    /// <summary>
    /// Test that SchemaVersion and DerivationVersion are set to 1.
    /// </summary>
    [Fact]
    public void Create_SetsVersions()
    {
        var identity = GatewayWorkspaceCatalogIdentity.Create("https://example.com/", "app1");

        identity.SchemaVersion.Should().Be(1);
        identity.DerivationVersion.Should().Be(1);
    }

    /// <summary>
    /// Test that AppId is preserved.
    /// </summary>
    [Fact]
    public void Create_PreservesAppId()
    {
        var identity = GatewayWorkspaceCatalogIdentity.Create("https://example.com/", "myapp");

        identity.AppId.Should().Be("myapp");
    }

    /// <summary>
    /// Test that manifest validation passes when identity and manifest match.
    /// Note: CatalogKey is not part of manifest validation (directory name carries it).
    /// </summary>
    [Fact]
    public void ValidateManifest_Passes_WhenIdentitiesMatch()
    {
        var identity = GatewayWorkspaceCatalogIdentity.Create("https://example.com/", "app1");
        var manifest = new GatewayWorkspaceCatalogManifest
        {
            CanonicalBaseUrl = identity.CanonicalBaseUrl,
            AppId = identity.AppId,
            SchemaVersion = identity.SchemaVersion,
            DerivationVersion = identity.DerivationVersion,
        };

        var act = () => identity.ValidateManifest(manifest);

        act.Should().NotThrow();
    }

    /// <summary>
    /// Test that manifest validation throws when CanonicalBaseUrl mismatches.
    /// </summary>
    [Fact]
    public void ValidateManifest_Throws_WhenCanonicalBaseUrlMismatches()
    {
        var identity = GatewayWorkspaceCatalogIdentity.Create("https://example.com/", "app1");
        var manifest = new GatewayWorkspaceCatalogManifest
        {
            CanonicalBaseUrl = "https://different.com",
            AppId = identity.AppId,
            SchemaVersion = identity.SchemaVersion,
            DerivationVersion = identity.DerivationVersion,
        };

        var act = () => identity.ValidateManifest(manifest);

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Test that manifest validation throws when AppId mismatches.
    /// </summary>
    [Fact]
    public void ValidateManifest_Throws_WhenAppIdMismatches()
    {
        var identity = GatewayWorkspaceCatalogIdentity.Create("https://example.com/", "app1");
        var manifest = new GatewayWorkspaceCatalogManifest
        {
            CanonicalBaseUrl = identity.CanonicalBaseUrl,
            AppId = "wrongapp",
            SchemaVersion = identity.SchemaVersion,
            DerivationVersion = identity.DerivationVersion,
        };

        var act = () => identity.ValidateManifest(manifest);

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Test that manifest validation throws when SchemaVersion mismatches.
    /// </summary>
    [Fact]
    public void ValidateManifest_Throws_WhenSchemaVersionMismatches()
    {
        var identity = GatewayWorkspaceCatalogIdentity.Create("https://example.com/", "app1");
        var manifest = new GatewayWorkspaceCatalogManifest
        {
            CanonicalBaseUrl = identity.CanonicalBaseUrl,
            AppId = identity.AppId,
            SchemaVersion = 99,
            DerivationVersion = identity.DerivationVersion,
        };

        var act = () => identity.ValidateManifest(manifest);

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Test that manifest validation throws when DerivationVersion mismatches.
    /// </summary>
    [Fact]
    public void ValidateManifest_Throws_WhenDerivationVersionMismatches()
    {
        var identity = GatewayWorkspaceCatalogIdentity.Create("https://example.com/", "app1");
        var manifest = new GatewayWorkspaceCatalogManifest
        {
            CanonicalBaseUrl = identity.CanonicalBaseUrl,
            AppId = identity.AppId,
            SchemaVersion = identity.SchemaVersion,
            DerivationVersion = 99,
        };

        var act = () => identity.ValidateManifest(manifest);

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Test that the same URL+AppId always produces the same CatalogKey (deterministic).
    /// </summary>
    [Fact]
    public void Create_ProducesDeterministicKey()
    {
        var identity1 = GatewayWorkspaceCatalogIdentity.Create("https://example.com/path", "app1");
        var identity2 = GatewayWorkspaceCatalogIdentity.Create("https://example.com/path", "app1");

        identity1.CatalogKey.Should().Be(identity2.CatalogKey);
    }

    /// <summary>
    /// Test that empty AppId is rejected.
    /// </summary>
    [Fact]
    public void Create_EmptyAppId_Throws()
    {
        Action act = () => GatewayWorkspaceCatalogIdentity.Create("https://example.com/", "");

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Test that null AppId is rejected.
    /// </summary>
    [Fact]
    public void Create_NullAppId_Throws()
    {
        Action act = () => GatewayWorkspaceCatalogIdentity.Create("https://example.com/", null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
