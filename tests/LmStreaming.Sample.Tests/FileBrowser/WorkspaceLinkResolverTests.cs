using LmStreaming.Sample.FileBrowser;

namespace LmStreaming.Sample.Tests.FileBrowser;

/// <summary>
/// Covers <see cref="WorkspaceLinkResolver"/>: every link shape a model writes for a workspace file (Windows and
/// POSIX absolute paths, <c>file://</c> URIs, relative paths, query/fragment suffixes) converted into the
/// workspace-relative '/' path the file API accepts, and every escape (outside HostPath, a sibling directory that
/// shares the HostPath's prefix, dot segments, NUL, a web scheme) refused before the gateway is touched.
/// </summary>
public class WorkspaceLinkResolverTests
{
    private const string WindowsHost = @"B:\ws";
    private const string PosixHost = "/workspace";

    [Theory]
    [InlineData(@"B:\ws\docs\a.md", WindowsHost, "docs/a.md")]
    [InlineData(@"b:/ws/docs/a.md", WindowsHost, "docs/a.md")]
    [InlineData(@"B:\WS\docs\a.md", WindowsHost, "docs/a.md")]
    [InlineData(@"B:\ws/docs\a.md", WindowsHost, "docs/a.md")]
    [InlineData(@"B:\ws\docs\a.md", @"B:\ws\", "docs/a.md")]
    [InlineData(@"B:\ws\docs\a.md", "b:/ws", "docs/a.md")]
    [InlineData(@"B:\ws", WindowsHost, "")]
    [InlineData(@"B:\ws\", WindowsHost, "")]
    [InlineData("file:///B:/ws/docs/a.md", WindowsHost, "docs/a.md")]
    [InlineData("FILE:///b:/ws/docs/a.md", WindowsHost, "docs/a.md")]
    [InlineData("file:///B:/ws/docs/my%20file.md", WindowsHost, "docs/my file.md")]
    [InlineData("file://localhost/B:/ws/docs/a.md", WindowsHost, "docs/a.md")]
    // The drive written where the host goes: a common malformed spelling that still names a local path.
    [InlineData("file://B:/ws/docs/a.md", WindowsHost, "docs/a.md")]
    [InlineData(@"B:\ws\docs\a.md#L10", WindowsHost, "docs/a.md")]
    [InlineData("file:///B:/ws/docs/a.md?x=1#L2", WindowsHost, "docs/a.md")]
    [InlineData("docs/a.md", WindowsHost, "docs/a.md")]
    [InlineData("./docs/a.md", WindowsHost, "docs/a.md")]
    [InlineData("docs/a.md#L10", WindowsHost, "docs/a.md")]
    [InlineData("docs/a.md?x=1", WindowsHost, "docs/a.md")]
    [InlineData("docs/my%20file.md", WindowsHost, "docs/my file.md")]
    [InlineData("docs/a.md", PosixHost, "docs/a.md")]
    [InlineData("a", PosixHost, "a")]
    // A colon after a slash is part of a POSIX name, not a URI scheme.
    [InlineData("notes/a:b.md", PosixHost, "notes/a:b.md")]
    [InlineData("/workspace/docs/a.md", PosixHost, "docs/a.md")]
    [InlineData("/workspace/", PosixHost, "")]
    [InlineData("file:///workspace/docs/a.md", PosixHost, "docs/a.md")]
    [InlineData("/home/u/ws/docs/a.md", "/home/u/ws", "docs/a.md")]
    public void Resolve_WorkspaceLink_ReturnsWorkspaceRelativePath(string target, string hostPath, string expected)
    {
        var result = WorkspaceLinkResolver.Resolve(target, hostPath);

        result.Success.Should().BeTrue();
        result.FailureCode.Should().BeNull();
        result.Path.Should().Be(expected);
    }

    [Theory]
    // `/workspace` is a container mount; the local Windows backend has none, so it is not this workspace.
    [InlineData("/workspace/docs/a.md", WindowsHost)]
    [InlineData(@"C:\ws\docs\a.md", WindowsHost)]
    // A sibling that shares the HostPath's characters must not match: the prefix test is segment-bounded.
    [InlineData(@"B:\ws2\a.md", WindowsHost)]
    // A rooted path with no drive, or a bare file:/// root, is not claimed by a drive-letter HostPath.
    [InlineData(@"\ws\docs\a.md", WindowsHost)]
    [InlineData("file:///", WindowsHost)]
    [InlineData("/workspace2/a.md", PosixHost)]
    [InlineData(@"B:\ws\docs\a.md", PosixHost)]
    [InlineData("/etc/passwd", PosixHost)]
    // POSIX paths are case-sensitive; only a Windows HostPath compares case-insensitively.
    [InlineData("/Workspace/docs/a.md", PosixHost)]
    public void Resolve_AbsolutePathOutsideHostPath_IsOutsideWorkspace(string target, string hostPath)
    {
        var result = WorkspaceLinkResolver.Resolve(target, hostPath);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(WorkspaceLinkResolver.OutsideWorkspace);
        result.Path.Should().BeNull();
    }

    [Theory]
    [InlineData("../secret.md", WindowsHost)]
    [InlineData("docs/../../secret.md", WindowsHost)]
    [InlineData("docs/%2E%2E/%2E%2E/secret.md", WindowsHost)]
    [InlineData("docs/./a.md", WindowsHost)]
    [InlineData(@"B:\ws\..\secret.md", WindowsHost)]
    [InlineData("/workspace/../etc/passwd", PosixHost)]
    [InlineData("docs/a%00.md", WindowsHost)]
    // A backslash in a relative path is a legal POSIX name character, never a separator.
    [InlineData(@"docs\a.md", WindowsHost)]
    [InlineData("docs%5Ca.md", WindowsHost)]
    [InlineData("http://example.com/docs/a.md", WindowsHost)]
    [InlineData("https://example.com/a.md", PosixHost)]
    [InlineData("mailto:someone@example.com", WindowsHost)]
    [InlineData("svn+ssh://example.com/docs/a.md", WindowsHost)]
    [InlineData("file://otherhost/B:/ws/docs/a.md", WindowsHost)]
    // A file URI with no path, or a relative one, names no workspace file.
    [InlineData("file://localhost", WindowsHost)]
    [InlineData("file:docs/a.md", WindowsHost)]
    [InlineData("", WindowsHost)]
    [InlineData("   ", WindowsHost)]
    [InlineData("#L10", WindowsHost)]
    public void Resolve_MalformedOrEscapingLink_IsInvalidPath(string target, string hostPath)
    {
        var result = WorkspaceLinkResolver.Resolve(target, hostPath);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(WorkspaceLinkResolver.InvalidPath);
        result.Path.Should().BeNull();
    }

    [Fact]
    public void Resolve_NullTarget_IsInvalidPath() =>
        WorkspaceLinkResolver.Resolve(null, WindowsHost).FailureCode.Should().Be(WorkspaceLinkResolver.InvalidPath);

    [Fact]
    public void Resolve_AbsoluteTargetWithoutHostPath_IsOutsideWorkspace() =>
        WorkspaceLinkResolver
            .Resolve(@"B:\ws\docs\a.md", hostPath: null)
            .FailureCode.Should()
            .Be(WorkspaceLinkResolver.OutsideWorkspace);
}
