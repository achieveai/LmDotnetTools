using LmStreaming.Sample.FileBrowser;

namespace LmStreaming.Sample.Tests.FileBrowser;

/// <summary>
/// Covers <see cref="FilePreviewPolicy"/>: the extension/exact-name allowlist, and the dot-directory
/// exclusion that keeps a conversation's own <c>.conversations/*.jsonl</c> transcript (#251) out of the
/// preview surface even though <c>.jsonl</c> is allowlisted.
/// </summary>
public class FilePreviewPolicyTests
{
    [Theory]
    [InlineData(".conversations/fix-the-login-bug-a3f9.jsonl")]
    [InlineData(".conversations/fix-the-login-bug-a3f9_agents/reviewer-7c21.jsonl")]
    [InlineData("nested/.conversations/notes.md")]
    [InlineData(".git/COMMIT_EDITMSG")]
    public void IsUnderDotDirectory_FileBeneathADotDirectory_IsExcluded(string serverPath) =>
        FilePreviewPolicy.IsUnderDotDirectory(serverPath).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("readme.md")]
    [InlineData(".gitignore")]
    [InlineData("src/.editorconfig")]
    [InlineData("src/app/main.cs")]
    public void IsUnderDotDirectory_DotFilesAndOrdinaryPaths_AreNotExcluded(string serverPath) =>
        FilePreviewPolicy.IsUnderDotDirectory(serverPath).Should().BeFalse();

    [Fact]
    public void IsUnderDotDirectory_IsIndependentOfTheAllowlist()
    {
        // The exclusion has to be checked separately: the transcript's own extension is previewable.
        FilePreviewPolicy.IsPreviewable("reviewer-7c21.jsonl").Should().BeTrue();
        FilePreviewPolicy.IsUnderDotDirectory(".conversations/reviewer-7c21.jsonl").Should().BeTrue();
    }
}
