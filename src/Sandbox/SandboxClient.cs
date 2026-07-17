namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// Credential-scoped client for the sandbox gateway's control plane: authenticated lifecycle
/// (create/get/list/delete), marketplace preview, and session discovery. One instance is bound to
/// exactly one <see cref="SandboxClientOptions"/> credential — a caller needing a different app
/// identity constructs a separate <see cref="SandboxClient"/> rather than mutating this one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership boundary.</b> This SDK owns gateway request construction, the <c>X-Sbx-*</c>/
/// <c>X-Session-ID</c> headers, REST serialization, and error classification. It deliberately
/// does NOT own: deciding when to create/recreate/delete a sandbox, host-path resolution, session
/// caching, credential selection, or OAuth/network/discovery POLICY (which auth providers or
/// network rules to attach) — callers pass fully-formed <see cref="SandboxCreateRequest"/> values
/// and this client only transmits them.
/// </para>
/// <para>
/// <b>Disposal never deletes sandboxes.</b> <see cref="Dispose"/> only releases local transport
/// resources (the owned <see cref="HttpClient"/>, when one was created for this instance) — it
/// never issues a gateway DELETE. Explicit sandbox teardown is always <see cref="DeleteAsync"/>.
/// </para>
/// </remarks>
public sealed partial class SandboxClient : IDisposable
{
    private const string AppIdHeader = "X-Sbx-App-Id";
    private const string AppKeyHeader = "X-Sbx-App-Key";
    private const string SessionIdHeader = "X-Session-ID";

    private readonly SandboxClientOptions _options;
    private bool _disposed;

    /// <summary>
    /// The underlying transport. Internal (not private) so tests can verify owned-vs-borrowed
    /// disposal semantics directly; never used by production call sites outside this partial class.
    /// </summary>
    internal HttpClient Transport { get; }

    /// <summary>
    /// Whether <see cref="Transport"/> was created (and is therefore disposed) by this instance, as
    /// opposed to borrowed from a caller. Internal test-only introspection.
    /// </summary>
    internal bool OwnsTransport { get; }

    /// <summary>
    /// Creates a client that OWNS its transport: a dedicated <see cref="HttpClient"/> configured with
    /// no automatic redirects and no automatic retries, disposed when this instance is disposed.
    /// </summary>
    public SandboxClient(SandboxClientOptions options)
        : this(options, CreateOwnedHttpClient(options), ownsHttpClient: true) { }

    /// <summary>
    /// Creates a client over a BORROWED <paramref name="httpClient"/> (e.g. one from
    /// <c>IHttpClientFactory</c> for DI/testing): this instance never disposes it, and never mutates
    /// its <see cref="HttpClient.DefaultRequestHeaders"/> or <see cref="HttpClient.Timeout"/> — doing
    /// either would leak this credential/timeout onto every other concurrent user of the shared
    /// client. The one-wire-submission-per-operation guarantee this SDK documents elsewhere applies
    /// only to the owned no-retry pipeline: a borrowed handler configured with its own retry policy
    /// must not retry side-effecting requests (create/delete) itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>SECURITY PRECONDITION — the borrowed handler MUST NOT follow redirects automatically.</b>
    /// Configure the borrowed client's underlying handler with
    /// <see cref="HttpClientHandler.AllowAutoRedirect"/> (or
    /// <c>SocketsHttpHandler.AllowAutoRedirect</c>) set to <c>false</c>. This SDK authenticates with
    /// custom <c>X-Sbx-App-Id</c>/<c>X-Sbx-App-Key</c> headers, and .NET's automatic-redirect logic
    /// only strips the standard <c>Authorization</c> header on a cross-origin redirect — it re-sends
    /// every custom header (including this SDK's credential headers) to the redirect target. If the
    /// borrowed handler follows a gateway/proxy <c>3xx</c> internally, that replay happens BEFORE this
    /// SDK ever sees a response, so the SDK cannot observe or prevent it: preventing the leak on a
    /// borrowed, auto-following handler is technically impossible from here, and this SDK does not
    /// claim to. The owned-transport constructor (<see cref="SandboxClient(SandboxClientOptions)"/>)
    /// disables auto-redirect for you; when you bring your own client you own that guarantee.
    /// </para>
    /// <para>
    /// As defense in depth, any <c>3xx</c> this SDK actually observes (i.e. when the borrowed handler
    /// did not auto-follow) is rejected as <see cref="SandboxErrorKind.Protocol"/> rather than
    /// followed — this SDK never chases a redirect itself. That rejection is the only redirect
    /// protection enforceable on a borrowed client; the no-auto-redirect precondition above is what
    /// closes the gap for a handler that would otherwise follow internally.
    /// </para>
    /// </remarks>
    public SandboxClient(SandboxClientOptions options, HttpClient httpClient)
        : this(options, httpClient, ownsHttpClient: false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
    }

    private SandboxClient(SandboxClientOptions options, HttpClient httpClient, bool ownsHttpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Transport = httpClient;
        OwnsTransport = ownsHttpClient;
    }

    /// <summary>
    /// Builds the dedicated transport for an OWNED client. <see cref="HttpClientHandler.AllowAutoRedirect"/>
    /// is disabled so a redirecting gateway/proxy never silently replays a request (potentially with
    /// credentials) to an unexpected host; no retry handler is layered on top, so a side-effecting
    /// request (create/delete) is sent exactly once by this pipeline. The client's own
    /// <see cref="HttpClient.Timeout"/> is left effectively unbounded — <see cref="SandboxClientOptions.TransportTimeout"/>
    /// is enforced per-call via a linked <see cref="CancellationTokenSource"/> instead (see
    /// <see cref="SendRestAsync"/>), so it applies identically to both owned and borrowed clients.
    /// </summary>
    private static HttpClient CreateOwnedHttpClient(SandboxClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = options.ServerAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Releases the owned <see cref="HttpClient"/> when this instance created one. A borrowed client
    /// (see the two-argument constructor) is left untouched — it remains usable by its owner after
    /// this call. Never issues any gateway request, so a live sandbox is never deleted as a side
    /// effect of disposing this client.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (OwnsTransport)
        {
            Transport.Dispose();
        }
    }
}
