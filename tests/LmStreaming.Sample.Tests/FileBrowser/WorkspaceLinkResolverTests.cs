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
    // Whitespace around an href is not part of the URL (a browser strips it too), but a name's own leading or
    // trailing space reaches the resolver percent-encoded, and the trim runs before decoding, so it survives.
    [InlineData(" docs/a.md\t", WindowsHost, "docs/a.md")]
    [InlineData(@" B:\ws\docs\a.md ", WindowsHost, "docs/a.md")]
    [InlineData("docs/%20a.md%20", WindowsHost, "docs/ a.md ")]
    [InlineData(" %20docs/a.md ", PosixHost, " docs/a.md")]
    [InlineData("file:///B:/ws/docs/a.md%20", WindowsHost, "docs/a.md ")]
    [InlineData("docs/a.md", PosixHost, "docs/a.md")]
    [InlineData("a", PosixHost, "a")]
    // A colon after a slash is part of a POSIX name, not a URI scheme.
    [InlineData("notes/a:b.md", PosixHost, "notes/a:b.md")]
    [InlineData("/workspace/docs/a.md", PosixHost, "docs/a.md")]
    [InlineData("/workspace/", PosixHost, "")]
    [InlineData("file:///workspace/docs/a.md", PosixHost, "docs/a.md")]
    [InlineData("/home/u/ws/docs/a.md", "/home/u/ws", "docs/a.md")]
    // `sandbox:` addresses the CONTAINER mount, so the payload is measured against `/workspace` and
    // never against HostPath — these rows pass a WINDOWS host on purpose.
    [InlineData("sandbox:/workspace/docs/a.md", WindowsHost, "docs/a.md")]
    [InlineData(
        "sandbox:/workspace/docs/rdb-embedded-database/design-specification.md",
        WindowsHost,
        "docs/rdb-embedded-database/design-specification.md"
    )]
    [InlineData("SANDBOX:/workspace/docs/a.md", WindowsHost, "docs/a.md")]
    [InlineData("sandbox:/workspace", WindowsHost, "")]
    [InlineData("sandbox:/workspace/", WindowsHost, "")]
    // Leading slashes collapse, so the authority-style spellings a model also writes still land.
    [InlineData("sandbox://workspace/docs/a.md", WindowsHost, "docs/a.md")]
    [InlineData("sandbox:///workspace/docs/a.md", WindowsHost, "docs/a.md")]
    [InlineData("sandbox:/workspace/docs/my%20file.md", WindowsHost, "docs/my file.md")]
    [InlineData("sandbox:/workspace/docs/a.md?x=1#L2", WindowsHost, "docs/a.md")]
    [InlineData("sandbox:/workspace/docs/a.md", PosixHost, "docs/a.md")]
    // Dot segments are NORMALISED now (see the class summary), not refused outright.
    [InlineData("docs/./a.md", WindowsHost, "docs/a.md")]
    [InlineData("docs/nested/../a.md", WindowsHost, "docs/a.md")]
    [InlineData("sandbox:/workspace/docs/nested/../a.md", WindowsHost, "docs/a.md")]
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
    // A `sandbox:` path outside the container mount is outside the workspace, whatever the host is.
    [InlineData("sandbox:/etc/passwd", WindowsHost)]
    [InlineData("sandbox:/etc/passwd", PosixHost)]
    // Segment-bounded there too: `/workspace2` is a sibling, not a child.
    [InlineData("sandbox:/workspace2/a.md", WindowsHost)]
    // The container mount is POSIX, so its name is case-sensitive.
    [InlineData("sandbox:/Workspace/docs/a.md", WindowsHost)]
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
    // Like `file:docs/a.md`, a `sandbox:` URI with a relative payload names no container path.
    [InlineData("sandbox:docs/a.md", WindowsHost)]
    [InlineData("sandbox:", WindowsHost)]
    // Normalising `..` did not make escaping legal: popping above the root is still refused.
    [InlineData("sandbox:/workspace/../etc/passwd", WindowsHost)]
    [InlineData("sandbox:/workspace/docs/../../etc/passwd", WindowsHost)]
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

    /// <summary>
    /// The shapes the CLIENT produces for a link written inside a previewed file: the renderer joins that file's
    /// own directory onto the href by naive concatenation (see `workspaceLinks.resolveAgainstBaseDir`) and leaves
    /// the dot segments here, because the controller's component-wise pass refuses one outright. So the join is
    /// only correct if this normalises it - and only safe if a `..` that climbs out still fails.
    /// </summary>
    [Theory]
    [InlineData(
        "docs/rdb-embedded-database/evidence/storage-source-fit.md",
        "docs/rdb-embedded-database/evidence/storage-source-fit.md"
    )]
    [InlineData("docs/rdb/./evidence/x.md", "docs/rdb/evidence/x.md")]
    // A sibling directory one level up - the shape the all-dots rejection made unreachable.
    [InlineData("docs/rdb/../siblings/x.md", "docs/siblings/x.md")]
    [InlineData("docs/rdb/../../top.md", "top.md")]
    // A base that already ends in a separator leaves a doubled one; empty components are dropped.
    [InlineData("docs/rdb//x.md", "docs/rdb/x.md")]
    public void Resolve_BaseDirJoinedPath_NormalizesDotSegments(string target, string expected)
    {
        var result = WorkspaceLinkResolver.Resolve(target, PosixHost);

        result.Success.Should().BeTrue();
        result.FailureCode.Should().BeNull();
        result.Path.Should().Be(expected);
    }

    [Theory]
    // A base cannot be used to climb out any more than a bare target can: one `..` too many is still a refusal,
    // never a clamp to the root (which would silently resolve a DIFFERENT file).
    [InlineData("docs/rdb/../../../etc/passwd")]
    [InlineData("docs/../../outside/x.md")]
    public void Resolve_BaseDirJoinedPathEscapingTheWorkspace_IsInvalidPath(string target)
    {
        var result = WorkspaceLinkResolver.Resolve(target, PosixHost);

        result.Success.Should().BeFalse();
        result.FailureCode.Should().Be(WorkspaceLinkResolver.InvalidPath);
        result.Path.Should().BeNull();
    }
}
