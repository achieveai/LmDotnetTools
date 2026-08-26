using System.Security.Cryptography;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmSampleShared.Release;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class ReleaseIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"release-identity-{Guid.NewGuid():N}");

    [Fact]
    public void Development_mode_does_not_require_a_manifest()
    {
        var identity = ReleaseManifestVerifier.LoadAndVerify("daemon", _root, developmentMode: true);
        identity.IsDevelopment.Should().BeTrue();
    }

    [Fact]
    public void Ambient_environment_cannot_enable_development_identity()
    {
        var originalAspNet = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var originalDotNet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var originalBypass = Environment.GetEnvironmentVariable("CODEREVIEW_DEVELOPMENT_IDENTITY");
        AppContext.TryGetSwitch(
            "LmDotnetTools.ReleaseIdentity.TestHostDevelopmentIdentity",
            out var originalTestHostSwitch
        );
        AppContext.SetSwitch("LmDotnetTools.ReleaseIdentity.TestHostDevelopmentIdentity", false);
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
            Environment.SetEnvironmentVariable("CODEREVIEW_DEVELOPMENT_IDENTITY", "1");

            ReleaseManifestVerifier
                .IsDevelopmentIdentityAllowed(_root, ["--development-identity"])
                .Should()
                .BeFalse("the executable is not under build output");
            ReleaseManifestVerifier
                .IsDevelopmentIdentityAllowed(Path.Combine(_root, "bin", "Debug", "net8.0"), [])
                .Should()
                .BeFalse("the explicit command-line flag is mandatory");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", originalAspNet);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", originalDotNet);
            Environment.SetEnvironmentVariable("CODEREVIEW_DEVELOPMENT_IDENTITY", originalBypass);
            AppContext.SetSwitch("LmDotnetTools.ReleaseIdentity.TestHostDevelopmentIdentity", originalTestHostSwitch);
        }
    }

    [Fact]
    public void Explicit_development_identity_is_allowed_only_from_build_output()
    {
        ReleaseManifestVerifier
            .IsDevelopmentIdentityAllowed(Path.Combine(_root, "bin", "Debug", "net8.0"), ["--development-identity"])
            .Should()
            .BeTrue();
    }

    [Fact]
    public void Production_mode_refuses_a_missing_manifest()
    {
        var act = () => ReleaseManifestVerifier.LoadAndVerify("daemon", _root, developmentMode: false);
        act.Should().Throw<InvalidOperationException>().WithMessage("*requires an adjacent manifest.json*");
    }

    [Fact]
    public void Production_mode_refuses_an_artifact_hash_mismatch()
    {
        Directory.CreateDirectory(Path.Combine(_root, "daemon"));
        var artifact = Path.Combine(_root, "daemon", "app");
        File.WriteAllText(artifact, "actual");
        var manifest = new ReleaseManifest(
            ReleaseManifestVerifier.CurrentManifestFormatVersion,
            new ReleaseIdentity("r1", "source", "commit", false, 1, 0, 10, ReleaseCapabilities.Required),
            [
                new ReleaseArtifact(
                    "daemon/app",
                    Convert.ToHexString(SHA256.HashData("expected"u8)).ToLowerInvariant(),
                    "daemon"
                ),
            ],
            DateTimeOffset.UtcNow,
            "v1"
        );
        var manifestPath = Path.Combine(_root, "manifest.json");
        var json = JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );
        using (var document = JsonDocument.Parse(json))
        {
            var root = document.RootElement;
            var normalized = new
            {
                formatVersion = root.GetProperty("formatVersion").GetInt32(),
                identity = new
                {
                    releaseId = root.GetProperty("identity").GetProperty("releaseId").GetString(),
                    sourceContentSha256 = root.GetProperty("identity").GetProperty("sourceContentSha256").GetString(),
                    baseCommit = root.GetProperty("identity").GetProperty("baseCommit").GetString(),
                    isDirty = root.GetProperty("identity").GetProperty("isDirty").GetBoolean(),
                    hostApiContractVersion = root.GetProperty("identity")
                        .GetProperty("hostApiContractVersion")
                        .GetInt32(),
                    databaseSchemaMinimum = root.GetProperty("identity")
                        .GetProperty("databaseSchemaMinimum")
                        .GetInt32(),
                    databaseSchemaMaximum = root.GetProperty("identity")
                        .GetProperty("databaseSchemaMaximum")
                        .GetInt32(),
                    capabilities = ReleaseCapabilities.Required,
                },
                artifacts = manifest.Artifacts.Select(a => new
                {
                    path = a.Path,
                    sha256 = a.Sha256,
                    component = a.Component,
                }),
                verifiedAtUtc = manifest.VerifiedAtUtc,
                verificationPolicy = manifest.VerificationPolicy,
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(normalized));
        }

        var act = () => ReleaseManifestVerifier.LoadAndVerify("daemon", _root, false, manifestPath);
        act.Should().Throw<InvalidOperationException>().WithMessage("*hash mismatch*");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
