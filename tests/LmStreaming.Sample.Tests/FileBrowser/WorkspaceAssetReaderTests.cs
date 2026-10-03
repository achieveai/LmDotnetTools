using LmStreaming.Sample.FileBrowser;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.FileBrowser;

public sealed class WorkspaceAssetReaderTests
{
    [Fact]
    public async Task BinaryAssets_UseBoundedNativeOperationAndDecodeExactBytes()
    {
        byte[] bytes = [0, 255, 128, 1];
        var files = new FakeFileBrowser
        {
            BinaryExecResult = new()
            {
                ExitCode = 0,
                StandardOutput = bytes,
                StandardError = [],
                OperationId = "read",
            },
        };
        var result = await WorkspaceAssetReader.ReadAsync(
            files,
            "session",
            "reports",
            "logo.png",
            8_388_608,
            CancellationToken.None
        );
        result.Status.Should().Be(200);
        result.Bytes.Should().Equal(bytes);
        var command = files.Commands.Single();
        command.Arguments[0].Should().Be("python3");
        command.Environment!["PATH"].Should().Be("/usr/local/bin");
        command.MaxOutputBytes.Should().Be(8_392_704);
        command.ExecutionTimeout.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task DecodeCap_RefusesOversizedOutputEvenWhenGatewayIgnoresLimit()
    {
        var files = new FakeFileBrowser
        {
            BinaryExecResult = new()
            {
                ExitCode = 0,
                StandardOutput = [0, 1, 2, 3],
                StandardError = [],
                OperationId = "read",
            },
        };
        var result = await WorkspaceAssetReader.ReadAsync(
            files,
            "session",
            "reports",
            "index.html",
            3,
            CancellationToken.None
        );
        result.Status.Should().Be(413);
        result.Bytes.Should().BeEmpty();
    }
}
