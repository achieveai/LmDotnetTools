using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Integration.Tests;

/// <summary>
/// The fake provider's <c>github.com</c> leaf must fit inside the job's CA validity. The contract job creates the CA
/// seconds before the tests run, so a leaf backdated a minute started before its issuer and
/// <see cref="CertificateRequest.Create(X509Certificate2, DateTimeOffset, DateTimeOffset, byte[])"/> threw (CI run
/// 45969468811, net8.0). Needs no gateway.
/// </summary>
public sealed class FakePrivateProviderCertificateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lmdt-ca-" + Guid.NewGuid().ToString("N")[..12]);

    public FakePrivateProviderCertificateTests() => _ = Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Leaf_IssuedRightAfterTheCaWasCreated_StaysInsideTheCaValidity()
    {
        var now = DateTimeOffset.UtcNow;
        var (certPath, keyPath) = WriteCa(notBefore: now, notAfter: now.AddMinutes(30));

        using var leaf = FakePrivateProvider.IssueGitHubCertificate(certPath, keyPath, now);
        using var ca = X509Certificate2.CreateFromPemFile(certPath, keyPath);

        leaf.NotBefore.Should().BeOnOrAfter(ca.NotBefore, "a leaf may not start before its issuer");
        leaf.NotAfter.Should().BeOnOrBefore(ca.NotAfter, "a leaf may not outlive its issuer");
        leaf.NotBefore.Should().BeOnOrBefore(now.UtcDateTime.ToLocalTime());
        leaf.HasPrivateKey.Should().BeTrue("Kestrel serves TLS with it");
        leaf.Issuer.Should().Be(ca.Subject);
    }

    private (string CertPath, string KeyPath) WriteCa(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=lmdt test CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true)
        );
        using var ca = request.CreateSelfSigned(notBefore, notAfter);
        var certPath = Path.Combine(_dir, "ca.crt");
        var keyPath = Path.Combine(_dir, "ca.key");
        File.WriteAllText(certPath, ca.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        return (certPath, keyPath);
    }
}
