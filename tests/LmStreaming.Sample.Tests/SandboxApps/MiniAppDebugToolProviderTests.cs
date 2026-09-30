using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.SandboxApps;

namespace LmStreaming.Sample.Tests.SandboxApps;

public sealed class MiniAppDebugToolProviderTests
{
    private const string SessionId = "owned-session";

    [Fact]
    public async Task TestRequest_RunsTheDiscoveredEntryInTheBoundSession_AndReturnsBothPipes()
    {
        var browser = BrowserWithDemoApp();
        browser.Setup(x => x.SupportsStreamingAsync(SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        browser
            .Setup(x =>
                x.ExecuteWorkspaceCommandStreamingAsync(
                    SessionId,
                    It.IsAny<SandboxCommand>(),
                    It.IsAny<Func<SandboxOutputChunk, CancellationToken, ValueTask>>(),
                    It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<IReadOnlyDictionary<string, string>>(),
                    It.IsAny<long>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                async (
                    string _,
                    SandboxCommand command,
                    Func<SandboxOutputChunk, CancellationToken, ValueTask> onOutput,
                    ReadOnlyMemory<byte> stdin,
                    IReadOnlyDictionary<string, string>? env,
                    long maxOutputBytes,
                    CancellationToken ct
                ) =>
                {
                    command.Arguments.Should().Equal("/workspace/mini-web-apps/demo/app.py");
                    command.WorkingDirectory.Should().Be("mini-web-apps/demo");
                    Encoding.UTF8.GetString(stdin.Span).Should().Be("count=1");
                    env!["REQUEST_METHOD"].Should().Be("POST");
                    env["PATH_INFO"].Should().Be("/save");
                    env["CONTENT_TYPE"].Should().Be("application/x-www-form-urlencoded");
                    env["CONTENT_LENGTH"].Should().Be("7");
                    env["HTTP_X_CSRF_TOKEN"].Should().Be(env["SANDBOX_APP_CSRF_TOKEN"]);
                    maxOutputBytes.Should().BeLessThanOrEqualTo(256 * 1024);
                    await onOutput(
                        new SandboxOutputChunk(
                            SandboxOutputStream.Stdout,
                            "Status: 201\r\nContent-Type: text/plain\r\n\r\ncreated"u8.ToArray()
                        ),
                        ct
                    );
                    await onOutput(new SandboxOutputChunk(SandboxOutputStream.Stderr, "trace line"u8.ToArray()), ct);
                    return new SandboxStreamResult(0, 47, 10);
                }
            );

        var result = await InvokeAsync(
            browser.Object,
            "TestMiniAppRequest",
            """{"app_id":"demo","method":"POST","path":"/save","body":"count=1","content_type":"application/x-www-form-urlencoded"}"""
        );

        result.Payload.IsError.Should().BeFalse();
        using var json = JsonDocument.Parse(result.Payload.Text);
        json.RootElement.GetProperty("exit_code").GetInt32().Should().Be(0);
        json.RootElement.GetProperty("stdout").GetString().Should().Contain("created");
        json.RootElement.GetProperty("stderr").GetString().Should().Be("trace line");
        browser.VerifyAll();
    }

    [Theory]
    [InlineData("../other", "/")]
    [InlineData("demo", "/../secret")]
    [InlineData("demo", "https://elsewhere/")]
    public async Task TestRequest_RejectsOutOfScopeTargetsBeforeGatewayExecution(string appId, string path)
    {
        var browser = new Mock<IWorkspaceFileBrowser>(MockBehavior.Strict);
        var args = JsonSerializer.Serialize(new { app_id = appId, path });

        var result = await InvokeAsync(browser.Object, "TestMiniAppRequest", args);

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_args");
        browser.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InspectApp_ReadsOnlyANamedFileWithinTheDiscoveredApp()
    {
        var browser = BrowserWithDemoApp();
        browser
            .Setup(x =>
                x.ReadWorkspaceFileBytesAsync(
                    SessionId,
                    "mini-web-apps/demo/app.py",
                    16384,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync("print('ok')"u8.ToArray());

        var result = await InvokeAsync(browser.Object, "InspectMiniApp", """{"app_id":"demo","file":"app.py"}""");

        result.Payload.IsError.Should().BeFalse();
        using var json = JsonDocument.Parse(result.Payload.Text);
        json.RootElement.GetProperty("file_content").GetString().Should().Be("print('ok')");
        browser.Verify(
            x =>
                x.ReadWorkspaceFileBytesAsync(
                    SessionId,
                    "mini-web-apps/demo/app.py",
                    16384,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task InspectApp_RejectsTraversalBeforeReading()
    {
        var browser = new Mock<IWorkspaceFileBrowser>(MockBehavior.Strict);
        var result = await InvokeAsync(browser.Object, "InspectMiniApp", """{"app_id":"demo","file":"../secret"}""");
        result.Payload.ErrorCode.Should().Be("invalid_args");
        browser.VerifyNoOtherCalls();
    }

    private static Mock<IWorkspaceFileBrowser> BrowserWithDemoApp()
    {
        var browser = new Mock<IWorkspaceFileBrowser>(MockBehavior.Strict);
        browser
            .Setup(x => x.ListWorkspaceDirectoryAsync(SessionId, "mini-web-apps", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SandboxDirectoryEntry("demo", SandboxEntryType.Directory, null, false)]);
        browser
            .Setup(x => x.ListWorkspaceDirectoryAsync(SessionId, "mini-web-apps/demo", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SandboxDirectoryEntry("mini-web-app.json", SandboxEntryType.File, 70, false),
                new SandboxDirectoryEntry("app.py", SandboxEntryType.File, 32, false),
            ]);
        browser
            .Setup(x =>
                x.ReadWorkspaceFileBytesAsync(
                    SessionId,
                    "mini-web-apps/demo/mini-web-app.json",
                    8192,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync("""{"id":"demo","name":"Demo","entry":"app.py","runtime":"python3"}"""u8.ToArray());
        return browser;
    }

    private static async Task<ToolHandlerResult.Resolved> InvokeAsync(
        IWorkspaceFileBrowser browser,
        string toolName,
        string args
    )
    {
        var provider = new MiniAppDebugToolProvider(
            browser,
            new SandboxAppDiscovery(browser),
            SessionId,
            "workspace",
            "site.lvh.me"
        );
        var tool = provider.GetFunctions().Single(x => x.Contract.Name == toolName);
        return Assert.IsType<ToolHandlerResult.Resolved>(
            await tool.Handler(args, new ToolCallContext(), CancellationToken.None)
        );
    }
}
