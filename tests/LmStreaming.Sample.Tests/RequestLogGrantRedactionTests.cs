namespace LmStreaming.Sample.Tests;

/// <summary>
/// Bug#15's workspace grant must never reach a log line. Serilog's default request-logging template writes
/// <c>{RequestPath}</c>, and on the raw workspace route the path CONTAINS the grant — a bearer credential
/// for an hour of read access to one conversation's workspace. <c>Program.cs</c> logs
/// <c>{SafeRequestPath}</c> instead; this pins what that produces.
/// </summary>
/// <remarks>
/// Tested directly rather than by asserting on emitted log lines. The claim worth pinning is the rewrite
/// itself — that the credential goes and nothing else does — and a test that drove Serilog would prove the
/// wiring while leaving the rewrite, which is where a mistake would actually live, unexercised. The wiring
/// is one <c>diagnosticContext.Set</c> call beside the template that names the property.
/// </remarks>
public class RequestLogGrantRedactionTests
{
    [Fact]
    public void RedactWorkspaceGrant_ReplacesTheGrantSegment() =>
        Program
            .RedactWorkspaceGrant("/api/conversations/t1/workspace/CfDJ8ABCDEF-secret/report/index.html")
            .Should()
            .Be("/api/conversations/t1/workspace/[grant]/report/index.html");

    /// <summary>
    /// The file's own path is what makes the log line useful, so only the credential segment goes. Nothing
    /// after the grant is a credential.
    /// </summary>
    [Fact]
    public void RedactWorkspaceGrant_KeepsTheFilePathAfterIt() =>
        Program
            .RedactWorkspaceGrant("/api/conversations/t1/workspace/tok/report/img/dot.png")
            .Should()
            .Be("/api/conversations/t1/workspace/[grant]/report/img/dot.png");

    [Fact]
    public void RedactWorkspaceGrant_RedactsEvenWithNoTrailingPath() =>
        Program
            .RedactWorkspaceGrant("/api/conversations/t1/workspace/tok")
            .Should()
            .Be("/api/conversations/t1/workspace/[grant]");

    /// <summary>
    /// Anchored on the whole route shape, not on a bare <c>workspace</c> segment: every other path in this
    /// host must come through character for character, or the request log stops being a record of what was
    /// requested.
    /// </summary>
    [Theory]
    [InlineData("/api/conversations/t1/files?path=report")]
    [InlineData("/api/conversations/t1/files/grant")]
    [InlineData("/api/workspaces")]
    [InlineData("/api/workspaces/ws-1/plugins")]
    [InlineData("/api/conversations/t1/workspace")]
    [InlineData("/workspace/tok/x")]
    [InlineData("/dist/assets/index-abc.js")]
    [InlineData("/")]
    public void RedactWorkspaceGrant_LeavesEveryOtherPathUnchanged(string path) =>
        Program.RedactWorkspaceGrant(path).Should().Be(path);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RedactWorkspaceGrant_EmptyOrNull_IsEmpty(string? path) =>
        Program.RedactWorkspaceGrant(path).Should().BeEmpty();

    /// <summary>
    /// The one thing this function exists to guarantee, stated as the property rather than as an example:
    /// the grant string must not appear in the result, whatever it looks like.
    /// </summary>
    [Theory]
    [InlineData("CfDJ8NrhE1s")]
    [InlineData("a.b.c")]
    [InlineData("grant-with-dashes_and_underscores")]
    public void RedactWorkspaceGrant_TheGrantNeverSurvives(string grant) =>
        Program.RedactWorkspaceGrant($"/api/conversations/t1/workspace/{grant}/index.html").Should().NotContain(grant);
}
