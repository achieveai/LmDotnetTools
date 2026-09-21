using LmStreaming.Sample.FileBrowser;

namespace LmStreaming.Sample.Tests.FileBrowser;

/// <summary>
/// Pins the CLOSED extension-to-media-type map the path-addressed raw workspace endpoint serves untrusted
/// workspace bytes with (Bug#15). The point of the map being closed is that an extension nobody listed is
/// <c>application/octet-stream</c> rather than whatever a 380-entry framework table happens to say, so the
/// unknown-extension case is as load-bearing as the known ones.
/// </summary>
public class WorkspaceContentTypesTests
{
    [Theory]
    [InlineData("index.html", "text/html; charset=utf-8")]
    [InlineData("index.htm", "text/html; charset=utf-8")]
    [InlineData("page.xhtml", "application/xhtml+xml; charset=utf-8")]
    [InlineData("style.css", "text/css; charset=utf-8")]
    [InlineData("app.js", "text/javascript; charset=utf-8")]
    [InlineData("app.mjs", "text/javascript; charset=utf-8")]
    [InlineData("data.json", "application/json; charset=utf-8")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("dot.png", "image/png")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("anim.gif", "image/gif")]
    [InlineData("shot.webp", "image/webp")]
    [InlineData("report.pdf", "application/pdf")]
    [InlineData("notes.txt", "text/plain; charset=utf-8")]
    [InlineData("readme.md", "text/plain; charset=utf-8")]
    [InlineData("font.woff2", "font/woff2")]
    public void ForFileName_KnownExtension_ReturnsMappedType(string name, string expected) =>
        WorkspaceContentTypes.ForFileName(name).Should().Be(expected);

    [Fact]
    public void ForFileName_IsCaseInsensitive() =>
        WorkspaceContentTypes.ForFileName("INDEX.HTML").Should().Be("text/html; charset=utf-8");

    [Theory]
    [InlineData("archive.zip")]
    [InlineData("tool.exe")]
    [InlineData("page.hta")]
    [InlineData("sheet.xsl")]
    [InlineData("data.xml")]
    [InlineData("noextension")]
    [InlineData("")]
    public void ForFileName_UnknownOrRiskyExtension_FallsBackToOctetStream(string name) =>
        WorkspaceContentTypes.ForFileName(name).Should().Be(WorkspaceContentTypes.Default);

    /// <summary>
    /// The three media types whose response MUST carry the CSP sandbox header: each one is a document the
    /// browser will execute script from, so each one needs the opaque origin even in a top-level tab.
    /// </summary>
    [Theory]
    [InlineData("text/html; charset=utf-8")]
    [InlineData("application/xhtml+xml; charset=utf-8")]
    [InlineData("image/svg+xml")]
    public void IsActiveDocument_TrueForScriptableDocumentTypes(string contentType) =>
        WorkspaceContentTypes.IsActiveDocument(contentType).Should().BeTrue();

    [Theory]
    [InlineData("image/png")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("application/pdf")]
    [InlineData("text/css; charset=utf-8")]
    [InlineData("application/octet-stream")]
    public void IsActiveDocument_FalseForInertTypes(string contentType) =>
        WorkspaceContentTypes.IsActiveDocument(contentType).Should().BeFalse();

    /// <summary>
    /// <c>.js</c> is served as script but is NOT an active document: a script is fetched by a document that
    /// already carries its own CSP, and labelling it active would attach a <c>sandbox</c> directive to a
    /// subresource — which is meaningless for a script and would not travel to the page that loaded it.
    /// </summary>
    [Fact]
    public void IsActiveDocument_FalseForJavaScript() =>
        WorkspaceContentTypes.IsActiveDocument(WorkspaceContentTypes.ForFileName("app.js")).Should().BeFalse();
}
