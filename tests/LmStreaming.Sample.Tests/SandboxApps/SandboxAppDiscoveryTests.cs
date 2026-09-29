using System.Text;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.SandboxApps;

namespace LmStreaming.Sample.Tests.SandboxApps;

public sealed class SandboxAppDiscoveryTests
{
    [Fact]
    public async Task Register_FindsWorkspaceAppAndBuildsWorkspaceBoundLink()
    {
        var browser = Browser("""{"id":"budget","name":"Budget Explorer","entry":"app.py","runtime":"python3"}""");
        var discovery = new SandboxAppDiscovery(browser.Object);

        var app = await discovery.ResolveAsync("session", "workspace-1", "budget", default);

        app.Should().NotBeNull();
        app!.Kind.Should().Be("mini-web-app");
        app.WorkspaceId.Should().Be("workspace-1");
        app.Link.Should().Be("#mini-app?workspace=workspace-1&app=budget");
        app.Definition.Executable.Should().Be("/workspace/mini-web-apps/budget/app.py");
        app.Definition.Arguments.Should().BeEmpty();
        app.Definition.WorkingDirectory.Should().Be("mini-web-apps/budget");
    }

    [Theory]
    [InlineData("../secret.py")]
    [InlineData("/workspace/secret.py")]
    [InlineData("nested\\secret.py")]
    public async Task Register_RejectsUnsafeEntrypoint(string entry)
    {
        var browser = Browser(
            $$"""{"id":"budget","name":"Budget","entry":"{{entry.Replace("\\", "\\\\")}}","runtime":"python3"}"""
        );
        var discovery = new SandboxAppDiscovery(browser.Object);

        var app = await discovery.ResolveAsync("session", "workspace-1", "budget", default);

        app.Should().BeNull();
    }

    [Fact]
    public async Task Register_RejectsSymlinkEntrypoint()
    {
        var browser = Browser(
            """{"id":"budget","name":"Budget","entry":"app.py","runtime":"python3"}""",
            SandboxEntryType.Symlink
        );
        var discovery = new SandboxAppDiscovery(browser.Object);

        (await discovery.ResolveAsync("session", "workspace-1", "budget", default)).Should().BeNull();
    }

    private static Mock<IWorkspaceFileBrowser> Browser(
        string manifest,
        SandboxEntryType entryType = SandboxEntryType.File
    )
    {
        var browser = new Mock<IWorkspaceFileBrowser>();
        browser
            .Setup(x => x.ListWorkspaceDirectoryAsync("session", "mini-web-apps", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SandboxDirectoryEntry("budget", SandboxEntryType.Directory, null, false)]);
        browser
            .Setup(x => x.ListWorkspaceDirectoryAsync("session", "mini-web-apps/budget", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SandboxDirectoryEntry("mini-web-app.json", SandboxEntryType.File, manifest.Length, false),
                new SandboxDirectoryEntry("app.py", entryType, 64, false),
            ]);
        browser
            .Setup(x =>
                x.ReadWorkspaceFileBytesAsync(
                    "session",
                    "mini-web-apps/budget/mini-web-app.json",
                    8192,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(Encoding.UTF8.GetBytes(manifest));
        return browser;
    }
}
