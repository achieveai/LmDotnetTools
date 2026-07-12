using System.Text;
using AchieveAi.LmDotnetTools.Sandbox.Transfer;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Transfer;

/// <summary>
/// Unit tests for the transfer script builders and their marker parser
/// (<see cref="TransferScripts"/>): each submission is classified only by opaque hex keys on the marker
/// line, a caller path with shell metacharacters is always POSIX single-quoted into a shell variable
/// (never interpreted, never leaked onto an emitted line), and a WRITE's embedded chunk round-trips
/// through <see cref="TransferScripts.ParseRequest"/>.
/// </summary>
public class TransferScriptsTests
{
    [Fact]
    public void BuildStat_MarkerLine_CarriesOnlyHexKey_NotTheRawPath()
    {
        const string path = "dir/secret file $(whoami).txt";

        var script = TransferScripts.BuildStat(path);

        var markerLine = script.Split('\n', 2)[0];
        markerLine.Should().StartWith("#LMSBX 1 XFER STAT path=");
        markerLine.Should().Contain(TransferPath.Key(path));
        markerLine.Should().NotContain("secret file");
        markerLine.Should().NotContain("whoami");
    }

    [Theory]
    [InlineData("dir/a b.txt")]
    [InlineData("dir/$(rm -rf ~).txt")]
    [InlineData("dir/`id`.txt")]
    [InlineData("dir/na;me&& .txt")]
    [InlineData("dir/quote'inside.txt")]
    public void BuildRead_QuotesThePathIntoAShellVariable_NeverOntoTheMarker(string path)
    {
        var script = TransferScripts.BuildRead(path, offset: 0, length: 16);

        var lines = script.Split('\n');
        lines[0].Should().NotContain(path);
        // The path only ever appears inside the single-quoted F assignment.
        script.Should().Contain("F=\"$WS/\"");
    }

    [Fact]
    public void ParseRequest_Stat_ReturnsKindAndPathKey()
    {
        var request = TransferScripts.ParseRequest(TransferScripts.BuildStat("a/b.txt"));

        request.Kind.Should().Be(TransferScriptKind.Stat);
        request.PathKey.Should().Be(TransferPath.Key("a/b.txt"));
    }

    [Fact]
    public void ParseRequest_Read_ReturnsOffsetAndLength()
    {
        var request = TransferScripts.ParseRequest(TransferScripts.BuildRead("a/b.txt", offset: 4096, length: 12288));

        request.Kind.Should().Be(TransferScriptKind.Read);
        request.PathKey.Should().Be(TransferPath.Key("a/b.txt"));
        request.Offset.Should().Be(4096);
        request.Length.Should().Be(12288);
    }

    [Fact]
    public void ParseRequest_Write_RecoversTheChunkBytes()
    {
        var chunk = Encoding.UTF8.GetBytes("chunk payload with = and / base64-ish");
        var base64 = Convert.ToBase64String(chunk);
        var script = TransferScripts.BuildWriteChunk("a/b.txt", "op123", offset: 0, length: chunk.Length, chunkBase64: base64);

        var request = TransferScripts.ParseRequest(script);

        request.Kind.Should().Be(TransferScriptKind.Write);
        request.TmpKey.Should().Be(TransferPath.Key(TransferPath.TempRelative("a/b.txt", "op123")));
        request.Offset.Should().Be(0);
        request.Length.Should().Be(chunk.Length);
        request.ChunkBytes.Should().Equal(chunk);
    }

    [Fact]
    public void ParseRequest_Finalize_ReturnsTmpAndDstKeysAndSize()
    {
        var script = TransferScripts.BuildFinalize("a/b.txt", "op123", size: 42, sha256: new string('a', 64));

        var request = TransferScripts.ParseRequest(script);

        request.Kind.Should().Be(TransferScriptKind.Finalize);
        request.TmpKey.Should().Be(TransferPath.Key(TransferPath.TempRelative("a/b.txt", "op123")));
        request.DstKey.Should().Be(TransferPath.Key("a/b.txt"));
        request.Size.Should().Be(42);
        request.Sha.Should().Be(new string('a', 64));
    }

    [Fact]
    public void ParseRequest_List_ReturnsDirAndArtifactKeys()
    {
        var script = TransferScripts.BuildList("proj", ".lmsbx-sdk/xfer/list.op");

        var request = TransferScripts.ParseRequest(script);

        request.Kind.Should().Be(TransferScriptKind.List);
        request.DirKey.Should().Be(TransferPath.Key("proj"));
        request.ArtKey.Should().Be(TransferPath.Key(".lmsbx-sdk/xfer/list.op"));
    }

    [Fact]
    public void ParseRequest_Cleanup_ReturnsPathKey()
    {
        var request = TransferScripts.ParseRequest(TransferScripts.BuildCleanup("a/b.txt.op.tmp"));

        request.Kind.Should().Be(TransferScriptKind.Cleanup);
        request.PathKey.Should().Be(TransferPath.Key("a/b.txt.op.tmp"));
    }

    [Fact]
    public void ParseRequest_NoMarker_Throws()
    {
        var act = () => TransferScripts.ParseRequest("echo hello\n");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void SplitNulListing_TrailingNul_DoesNotProduceEmptyEntry()
    {
        TransferPath.SplitNulListing("a\0b\0c\0").Should().Equal("a", "b", "c");
    }

    [Fact]
    public void SplitNulListing_EmptyArtifact_YieldsNoEntries()
    {
        TransferPath.SplitNulListing(string.Empty).Should().BeEmpty();
    }
}
