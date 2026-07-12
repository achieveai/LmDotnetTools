using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// F1: the manifest sentinel line the SDK reads back through the gateway's truncating <c>exec</c> must
/// stay provably under the pinned 20&#160;KB/500-line limit — even when BOTH streams sit at the inline
/// threshold and the manifest is base64-wrapped (nested encoding) — and exact output must still be
/// recovered by switching larger streams to chunk artifacts. Every test here drives a
/// <see cref="FakeSandboxGateway"/> that faithfully truncates its output (never "unlimited"), so an
/// over-limit line would genuinely corrupt and fail rather than pass by accident.
/// </summary>
public class CommandManifestTransportTests
{
    private const string Session = "sess-1";

    [Fact]
    public void WorstCaseManifestLine_BothStreamsInlinedAtThreshold_StaysUnderBudget_AsASingleLine()
    {
        var line = ManifestLineForInlineSizes(
            CommandArtifactLayout.InlineThresholdBytes,
            CommandArtifactLayout.InlineThresholdBytes
        );

        // The proven bound: both streams inlined at threshold, nested base64, still under budget...
        Encoding.UTF8.GetByteCount(line).Should().BeLessThanOrEqualTo(CommandArtifactLayout.ManifestLineByteBudget);
        // ...and the budget sits safely below the gateway's actual truncation cap.
        CommandArtifactLayout
            .ManifestLineByteBudget.Should()
            .BeLessThan(CommandArtifactLayout.GatewayOutputByteLimit);
        // A single newline-free line, so the 500-line cap can never apply to it.
        line.Should().NotContain("\n");
        line.Should().StartWith(CommandSentinel.Marker);
        // It therefore passes through the real gateway's truncation completely intact.
        GatewayTruncation.Apply(line).Should().Be(line);
    }

    [Fact]
    public void PreFixEightKibThreshold_WouldHaveOverflowed_TheCurrentThresholdDoesNot()
    {
        var preFixLine = ManifestLineForInlineSizes(8 * 1024, 8 * 1024);
        var currentLine = ManifestLineForInlineSizes(
            CommandArtifactLayout.InlineThresholdBytes,
            CommandArtifactLayout.InlineThresholdBytes
        );

        // Regression guard: the pre-fix 8 KiB per-stream inline threshold produced a nested manifest
        // line the gateway would truncate (and truncation corrupts it), which is the F1 defect...
        Encoding
            .UTF8.GetByteCount(preFixLine)
            .Should()
            .BeGreaterThan(CommandArtifactLayout.GatewayOutputByteLimit);
        GatewayTruncation.Apply(preFixLine).Should().NotBe(preFixLine);

        // ...whereas the current threshold keeps the worst case whole.
        Encoding
            .UTF8.GetByteCount(currentLine)
            .Should()
            .BeLessThan(CommandArtifactLayout.GatewayOutputByteLimit);
    }

    [Theory]
    [MemberData(nameof(StreamSizeCombinations))]
    public async Task ExecuteAsync_StreamSizeBoundaryCombinations_ReassembleExactBytes_AndNeverOverflowTheGateway(
        int stdoutSize,
        int stderrSize
    )
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        var stdout = CommandTestSupport.PrintablePattern(stdoutSize, seed: 1);
        var stderr = CommandTestSupport.PrintablePattern(stderrSize, seed: 2);
        fake.Program(op, exitCode: 42, stdout, stderr);

        var result = await client.ExecuteAsync(Session, new SandboxCommand(["x"], operationId: "op-1"));

        result.ExitCode.Should().Be(42);
        Encoding.UTF8.GetBytes(result.StandardOutput).Should().Equal(stdout);
        Encoding.UTF8.GetBytes(result.StandardError).Should().Equal(stderr);
        // The whole point of F1: no wire line the gateway returned ever exceeded its truncation cap.
        fake.MaxObservedResponseBytes.Should().BeLessThanOrEqualTo(CommandArtifactLayout.GatewayOutputByteLimit);
    }

    public static IEnumerable<object[]> StreamSizeCombinations()
    {
        var threshold = CommandArtifactLayout.InlineThresholdBytes;
        int[] sizes = [0, 1, threshold - 1, threshold, threshold + 1, 2 * threshold, 30_000];
        foreach (var stdoutSize in sizes)
        {
            foreach (var stderrSize in sizes)
            {
                yield return [stdoutSize, stderrSize];
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_LostResponse_BothStreamsAtInlineThreshold_RecoversExactBytesThroughTruncatingGateway()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake, transportTimeout: TimeSpan.FromMilliseconds(300));
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        // Exactly at the inline threshold => both streams inline => the largest possible manifest line,
        // which is precisely what the recovery PROBE must be able to return through the truncating exec.
        var stdout = CommandTestSupport.PrintablePattern(CommandArtifactLayout.InlineThresholdBytes, seed: 1);
        var stderr = CommandTestSupport.PrintablePattern(CommandArtifactLayout.InlineThresholdBytes, seed: 2);
        fake.Program(op, exitCode: 7, stdout, stderr);
        fake.SetRunMode(op, FakeSandboxGateway.RunMode.HangAfterCommit);

        var result = await client.ExecuteAsync(Session, new SandboxCommand(["deploy"], operationId: "op-1"));

        result.ExitCode.Should().Be(7);
        Encoding.UTF8.GetBytes(result.StandardOutput).Should().Equal(stdout);
        Encoding.UTF8.GetBytes(result.StandardError).Should().Equal(stderr);
        fake.SideEffectCount.Should().Be(1);
        fake.Requests.Count(r => r.Kind == CommandScriptKind.Run).Should().Be(1);
        // Recovery went through an idempotent PROBE whose manifest line survived truncation.
        fake.Requests.Should().Contain(r => r.Kind == CommandScriptKind.Probe);
        fake.MaxObservedResponseBytes.Should().BeLessThanOrEqualTo(CommandArtifactLayout.GatewayOutputByteLimit);
    }

    [Fact]
    public async Task ExecuteAsync_LostResponse_LargeChunkedStreams_RecoversExactBytesThroughTruncatingGateway()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake, transportTimeout: TimeSpan.FromMilliseconds(300));
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        var stdout = CommandTestSupport.PrintablePattern(40_000, seed: 3);
        var stderr = CommandTestSupport.PrintablePattern(25_000, seed: 4);
        fake.Program(op, exitCode: 0, stdout, stderr);
        fake.SetRunMode(op, FakeSandboxGateway.RunMode.HangAfterCommit);

        var result = await client.ExecuteAsync(Session, new SandboxCommand(["build"], operationId: "op-1"));

        Encoding.UTF8.GetBytes(result.StandardOutput).Should().Equal(stdout);
        Encoding.UTF8.GetBytes(result.StandardError).Should().Equal(stderr);
        fake.SideEffectCount.Should().Be(1);
        // Streams above the inline threshold are recovered from bounded chunk reads, never a giant line.
        fake.Requests.Count(r => r.Kind == CommandScriptKind.Read).Should().BeGreaterThan(1);
        fake.MaxObservedResponseBytes.Should().BeLessThanOrEqualTo(CommandArtifactLayout.GatewayOutputByteLimit);
    }

    [Fact]
    public void TruncatingGateway_CutsOutputThatExceedsTheByteLimit()
    {
        var overLimit = new string('x', CommandArtifactLayout.GatewayOutputByteLimit + 500);

        var cut = GatewayTruncation.Apply(overLimit);

        Encoding.UTF8.GetByteCount(cut).Should().Be(CommandArtifactLayout.GatewayOutputByteLimit);
    }

    [Fact]
    public void TruncatingGateway_CutsOutputThatExceedsTheLineLimit()
    {
        var overLimit = string.Join('\n', Enumerable.Range(0, CommandArtifactLayout.GatewayOutputLineLimit + 100));

        var cut = GatewayTruncation.Apply(overLimit);

        cut.Count(c => c == '\n').Should().BeLessThanOrEqualTo(CommandArtifactLayout.GatewayOutputLineLimit);
    }

    /// <summary>Builds the manifest sentinel line exactly as a RUN/PROBE wrapper does for two inlined streams of the given raw sizes.</summary>
    private static string ManifestLineForInlineSizes(int stdoutInlineBytes, int stderrInlineBytes)
    {
        var manifest = new CommandManifest
        {
            Digest = new string('f', 64),
            Generation = new string('f', 32),
            ExitCode = int.MinValue,
            Stdout = InlineStreamManifest(stdoutInlineBytes),
            Stderr = InlineStreamManifest(stderrInlineBytes),
            LeaseUnixSeconds = 9_999_999_999,
            CreatedUnixSeconds = 9_999_999_999,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, CommandManifest.Json);
        return CommandSentinel.Manifest(Convert.ToBase64String(json));
    }

    private static CommandStreamManifest InlineStreamManifest(int inlineRawBytes) =>
        new()
        {
            Length = inlineRawBytes,
            Sha256 = new string('f', 64),
            Inline = Convert.ToBase64String(new byte[inlineRawBytes]),
        };
}
