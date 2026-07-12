using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// F4: every manifest field is REQUIRED at JSON deserialization. A wire manifest read back through the
/// gateway that OMITS or NULLS any required field (version, digest, exit, each stream record, its length,
/// its hash, its inline slot, the lease/created timestamps) must surface as
/// <see cref="SandboxErrorKind.Protocol"/> carrying the operation id — never a silent, successful default
/// (a zero exit, an empty digest, a zero length, an unintended chunked read). These drive the real SDK
/// boundary (<see cref="SandboxClient.ExecuteAsync"/>) with a hand-crafted manifest so the actual
/// deserialize → validate → map pipeline is exercised, not a stand-in.
/// </summary>
public class CommandManifestWireTests
{
    private const string Session = "sess-1";
    private static readonly string S_emptySha = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();

    /// <summary>A fully-populated, materialization-valid manifest DOM whose digest matches the fixed command's canonical digest and whose empty streams carry the true empty-input SHA-256.</summary>
    private static JsonObject ValidManifest()
    {
        var command = FixedCommand();
        return new JsonObject
        {
            ["v"] = CommandManifest.CurrentVersion,
            ["digest"] = CommandTestSupport.Digest(Session, command),
            ["exit"] = 0,
            ["stdout"] = new JsonObject
            {
                ["len"] = 0,
                ["sha256"] = S_emptySha,
                ["inline"] = "",
            },
            ["stderr"] = new JsonObject
            {
                ["len"] = 0,
                ["sha256"] = S_emptySha,
                ["inline"] = "",
            },
            ["lease"] = 1,
            ["created"] = 1,
        };
    }

    private static SandboxCommand FixedCommand() => new(["x"], operationId: "op-1");

    /// <summary>Every field whose OMISSION from the wire manifest must be rejected as Protocol.</summary>
    public static IEnumerable<object[]> RequiredFieldPaths() =>
        new[]
        {
            "v",
            "digest",
            "exit",
            "stdout",
            "stderr",
            "lease",
            "created",
            "stdout.len",
            "stdout.sha256",
            "stdout.inline",
            "stderr.len",
            "stderr.sha256",
            "stderr.inline",
        }.Select(path => new object[] { path });

    /// <summary>Every field whose PRESENT-BUT-NULL value must be rejected as Protocol. <c>inline</c> is excluded — a null inline is the legitimate "read this stream in chunks" signal.</summary>
    public static IEnumerable<object[]> NonNullFieldPaths() =>
        new[]
        {
            "v",
            "digest",
            "exit",
            "stdout",
            "stderr",
            "lease",
            "created",
            "stdout.len",
            "stdout.sha256",
            "stderr.len",
            "stderr.sha256",
        }.Select(path => new object[] { path });

    [Fact]
    public async Task ExecuteAsync_FullyPopulatedManifest_IsAccepted_ProvingTheBaselineIsValid()
    {
        // The baseline must succeed, so an omission/null theory failure is attributable to the defect,
        // not to a broken baseline. Empty streams with the true empty SHA-256 materialize to empty output.
        var exception = await ExecuteWithManifestAsync(ValidManifest());

        exception.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ManifestWithNullInline_IsAccepted_NullInlineMeansChunkedNotAViolation()
    {
        var manifest = ValidManifest();
        manifest["stdout"]!["inline"] = null;
        manifest["stderr"]!["inline"] = null;

        // A null inline is required to be PRESENT but is a valid value: the SDK reads the (zero-length)
        // stream back in chunks and it verifies, so this must NOT be a Protocol failure.
        var exception = await ExecuteWithManifestAsync(manifest);

        exception.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(RequiredFieldPaths))]
    public async Task ExecuteAsync_ManifestMissingRequiredField_MapsToProtocol_WithOperationId(string path)
    {
        var exception = await ExecuteWithManifestAsync(Without(ValidManifest(), path));

        AssertProtocol(exception, $"the manifest omitting '{path}'");
    }

    [Theory]
    [MemberData(nameof(NonNullFieldPaths))]
    public async Task ExecuteAsync_ManifestWithNullRequiredField_MapsToProtocol_WithOperationId(string path)
    {
        var exception = await ExecuteWithManifestAsync(SetNull(ValidManifest(), path));

        AssertProtocol(exception, $"the manifest nulling '{path}'");
    }

    private static void AssertProtocol(Exception? exception, string because)
    {
        exception.Should().BeOfType<SandboxException>(because);
        var sandboxException = (SandboxException)exception!;
        sandboxException.Kind.Should().Be(SandboxErrorKind.Protocol, because);
        sandboxException.OperationId.Should().Be("op-1", because);
    }

    private static async Task<Exception?> ExecuteWithManifestAsync(JsonObject manifest)
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.SeedRawManifestJson(op, manifest.ToJsonString());

        return await Record.ExceptionAsync(() => client.ExecuteAsync(Session, FixedCommand()));
    }

    /// <summary>Returns a clone of <paramref name="manifest"/> with the dot-delimited field removed.</summary>
    private static JsonObject Without(JsonObject manifest, string path)
    {
        var clone = Clone(manifest);
        var (parent, leaf) = Navigate(clone, path);
        parent.Remove(leaf);
        return clone;
    }

    /// <summary>Returns a clone of <paramref name="manifest"/> with the dot-delimited field set to JSON null.</summary>
    private static JsonObject SetNull(JsonObject manifest, string path)
    {
        var clone = Clone(manifest);
        var (parent, leaf) = Navigate(clone, path);
        parent[leaf] = null;
        return clone;
    }

    private static (JsonObject Parent, string Leaf) Navigate(JsonObject root, string path)
    {
        var parts = path.Split('.');
        var parent = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            parent = (JsonObject)parent[parts[i]]!;
        }

        return (parent, parts[^1]);
    }

    private static JsonObject Clone(JsonObject manifest) => (JsonObject)JsonNode.Parse(manifest.ToJsonString())!;
}
