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

    [Theory]
    [InlineData("budget.xlsx")]
    [InlineData("BUDGET.XLSX")]
    [InlineData("macros.xlsm")]
    [InlineData("reports/2026/q3.xlsx")]
    public void IsSpreadsheet_OoxmlWorkbooks_AreRecognised(string name) =>
        FilePreviewPolicy.IsSpreadsheet(name).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("legacy.xls")]
    [InlineData("binary.xlsb")]
    [InlineData("data.csv")]
    [InlineData("notes.md")]
    [InlineData("xlsx")]
    public void IsSpreadsheet_EverythingElse_IsNot(string name) =>
        FilePreviewPolicy.IsSpreadsheet(name).Should().BeFalse();

    [Fact]
    public void IsPreviewable_ASpreadsheet_IsEligibleEvenThoughItIsNotText()
    {
        // The spreadsheet set is deliberately SEPARATE from the text allowlist — these files are read by
        // the spreadsheet reader, not decoded as UTF-8 — but both make a file preview-eligible.
        FilePreviewPolicy.IsPreviewable("budget.xlsx").Should().BeTrue();
        FilePreviewPolicy.IsPreviewable("macros.xlsm").Should().BeTrue();
    }

    [Fact]
    public void IsPreviewable_TheTextAllowlistIsUnchangedByTheSpreadsheetSet()
    {
        // Guards the one way this could go wrong quietly: widening `IsPreviewable` must not smuggle a
        // spreadsheet extension into the TEXT path, nor drop anything that was already text-previewable.
        FilePreviewPolicy.IsSpreadsheet("data.csv").Should().BeFalse();
        FilePreviewPolicy.IsSpreadsheet("notes.md").Should().BeFalse();
        FilePreviewPolicy.IsPreviewable("data.csv").Should().BeTrue();
        FilePreviewPolicy.IsPreviewable("legacy.xls").Should().BeFalse("the legacy BIFF format is not read here");
        FilePreviewPolicy.IsPreviewable("image.png").Should().BeFalse();
    }

    [Fact]
    public void IsUnderDotDirectory_IsIndependentOfTheAllowlist()
    {
        // The exclusion has to be checked separately: the transcript's own extension is previewable.
        FilePreviewPolicy.IsPreviewable("reviewer-7c21.jsonl").Should().BeTrue();
        FilePreviewPolicy.IsUnderDotDirectory(".conversations/reviewer-7c21.jsonl").Should().BeTrue();
    }
}
