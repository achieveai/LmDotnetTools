using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// F9: every structural manifest defect must map to <see cref="SandboxErrorKind.Protocol"/> before the
/// SDK materializes any stream — never surface as a raw exception. These exercise
/// <see cref="CommandManifestValidator"/> directly across version, digest, stream nullability, length,
/// per-stream digest, and timestamp defects.
/// </summary>
public class CommandManifestValidatorTests
{
    private const string OperationId = "op-1";

    private static CommandManifest Valid() =>
        new()
        {
            Version = CommandManifest.CurrentVersion,
            Digest = new string('a', 64),
            Generation = new string('e', 32),
            ExitCode = 0,
            Stdout = new CommandStreamManifest
            {
                Length = 0,
                Sha256 = new string('b', 64),
                Inline = "",
            },
            Stderr = new CommandStreamManifest
            {
                Length = 0,
                Sha256 = new string('c', 64),
                Inline = "",
            },
            LeaseUnixSeconds = 100,
            CreatedUnixSeconds = 50,
        };

    [Fact]
    public void Validate_WellFormedManifest_DoesNotThrow()
    {
        var act = () => CommandManifestValidator.Validate(Valid(), OperationId);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_EveryStructuralDefect_ThrowsProtocol_WithOperationId_NeverARawException()
    {
        var malformed = new (string Case, CommandManifest Manifest)[]
        {
            ("unsupported version (2)", Valid() with { Version = 2 }),
            ("unsupported version (0)", Valid() with { Version = 0 }),
            ("digest too short", Valid() with { Digest = new string('a', 63) }),
            ("digest uppercase hex", Valid() with { Digest = new string('A', 64) }),
            ("digest non-hex", Valid() with { Digest = "not-hex-value" }),
            ("digest empty", Valid() with { Digest = "" }),
            ("generation too short", Valid() with { Generation = new string('e', 31) }),
            ("generation wrong length (64)", Valid() with { Generation = new string('e', 64) }),
            ("generation uppercase hex", Valid() with { Generation = new string('E', 32) }),
            ("generation non-hex", Valid() with { Generation = "not-hex-generation-value-32chars" }),
            ("generation empty", Valid() with { Generation = "" }),
            ("null stdout record", Valid() with { Stdout = null! }),
            ("null stderr record", Valid() with { Stderr = null! }),
            (
                "negative stdout length",
                Valid() with
                {
                    Stdout = new CommandStreamManifest { Length = -1, Sha256 = new string('b', 64) },
                }
            ),
            (
                "malformed stderr digest",
                Valid() with
                {
                    Stderr = new CommandStreamManifest { Length = 0, Sha256 = "short" },
                }
            ),
            ("negative lease", Valid() with { LeaseUnixSeconds = -1 }),
            ("negative created", Valid() with { CreatedUnixSeconds = -1 }),
        };

        foreach (var (description, manifest) in malformed)
        {
            var act = () => CommandManifestValidator.Validate(manifest, OperationId);

            var thrown = act.Should()
                .Throw<SandboxException>($"the manifest defect '{description}' must be rejected")
                .Which;
            thrown.Kind.Should().Be(SandboxErrorKind.Protocol, "defect: {0}", description);
            thrown.OperationId.Should().Be(OperationId, "defect: {0}", description);
        }
    }
}
