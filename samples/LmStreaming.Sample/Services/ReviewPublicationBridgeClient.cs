using System.Net;
using System.Text;
using System.Text.Json;

namespace LmStreaming.Sample.Services;

/// <summary>
/// What the daemon backend answered for one typed publication action: either a typed receipt or a
/// typed rejection. Both are values the review agent must retain for audit, which is why a rejection
/// carries its complete typed body rather than a summary of it.
/// </summary>
/// <param name="Accepted">True for a receipt, false for a rejection.</param>
/// <param name="Payload">
/// The typed body to hand back to the agent: the backend's answer projected down to the members of the
/// daemon's typed outcome. Never a raw provider body, and never a member the contract does not name.
/// </param>
/// <param name="RejectionCode">The mechanical reason code; null when <paramref name="Accepted"/>.</param>
public sealed record ReviewPublicationResult(bool Accepted, string Payload, string? RejectionCode);

/// <summary>
/// Forwards one typed publication action to the daemon backend that owns the provider credentials.
/// </summary>
/// <remarks>
/// The split is spec §6.1: this side decides nothing about the PR. It carries the agent's typed intent
/// and the host's scope to a backend that alone validates repository/PR/provider scope, lifecycle,
/// expected head, ref replyability, diff anchors, size and idempotency — and alone holds the GitHub /
/// Azure DevOps credentials.
/// </remarks>
public interface IReviewPublicationBridge
{
    /// <summary>Sends <paramref name="operation"/> with the agent's <paramref name="arguments"/>.</summary>
    Task<ReviewPublicationResult> SendAsync(
        string operation,
        JsonElement arguments,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// The HTTP implementation of <see cref="IReviewPublicationBridge"/>: one authenticated POST to
/// <c>api/review-publication/rounds/{roundId:long}/actions/{operation}</c> per action.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope is applied here, not by the caller.</b> The instance is constructed around one
/// <see cref="ReviewPublicationScope"/> and writes the whole <c>scope</c> object itself, after dropping
/// any <c>scope</c> the model supplied. A model therefore cannot aim the daemon's credentials at another
/// pull request, and — the sharper risk — cannot set <c>livePostingAuthorized</c> to turn a
/// collect-only round into a live write. There is no unscoped path through this type.
/// </para>
/// <para>
/// <b>Two per-action fields are the agent's.</b> <c>actionId</c> (the idempotency key) and
/// <c>sources</c> (the evidence citations the daemon validates and can reject as
/// <c>invalid_source_reference</c>) are lifted OUT of the agent's arguments and INTO the scope object,
/// because that is where the daemon's <c>PublicationScope</c> carries them. Everything else the agent
/// sent is copied through as a sibling of <c>scope</c>, byte-for-byte.
/// </para>
/// <para>
/// <b>The bridge secret is a header and only a header.</b> It is sent raw in
/// <c>X-Review-Bridge-Auth</c> (the daemon's <c>ReviewBridgeAuthAttribute</c> compares it in fixed
/// time), never written into the payload, never logged, and never part of a
/// <see cref="ReviewPublicationResult"/>.
/// </para>
/// <para>
/// <b>Only typed bodies reach the model.</b> A 200 carrying no <c>rejectionCode</c> is a receipt; a
/// 409/422 carrying a parseable typed rejection is that rejection. Everything else — a 500, a proxy's
/// HTML error page, a transport failure — collapses to <see cref="BackendUnavailableCode"/> with a
/// fixed message, because those bodies can carry provider tokens, internal hostnames and stack traces.
/// </para>
/// </remarks>
public sealed class ReviewPublicationBridgeClient : IReviewPublicationBridge
{
    /// <summary>The header the daemon's <c>ReviewBridgeAuthAttribute</c> reads. Raw secret, no scheme.</summary>
    internal const string AuthHeaderName = "X-Review-Bridge-Auth";

    /// <summary>The stable code for any answer that is not a typed receipt or a typed rejection.</summary>
    public const string BackendUnavailableCode = "publication_backend_unavailable";

    /// <summary>The stable code for an action the host refuses to put on the wire because it is too big.</summary>
    public const string RequestTooLargeCode = "publication_request_too_large";

    /// <summary>
    /// The largest backend answer the host will read. A typed receipt is a handful of ids; anything at
    /// this scale is a proxy error page, a stack trace, or a stream that does not end — none of which can
    /// become a typed answer, so reading further only spends the review turn's memory.
    /// </summary>
    internal const int MaxResponseBytes = 128 * 1024;

    /// <summary>
    /// The largest action the host will send. Deliberately far above the response ceiling: a grouped
    /// inline-findings batch is legitimately orders of magnitude larger than a receipt. The daemon owns
    /// the real size policy (spec §6.1); this is only the transport backstop that stops a looping agent
    /// from posting an unbounded body.
    /// </summary>
    internal const int MaxRequestBytes = 1024 * 1024;

    /// <summary>
    /// The per-action budget. A backend that accepts the connection and then never answers must not hold
    /// the review turn open, because the turn has no other way to end.
    /// </summary>
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The message that replaces an untyped backend body, so nothing raw reaches the model.</summary>
    private const string BackendUnavailableMessage =
        "The review publication backend did not return a typed receipt or rejection. "
        + "The action was not recorded as published; do not retry with a different action id.";

    /// <summary>
    /// The message for an action the host would not put on the wire. Says explicitly that nothing was
    /// sent, because the agent's next move depends on it: the action id is still unused, so the
    /// shortened action must reuse it rather than mint a new one.
    /// </summary>
    private const string RequestTooLargeMessage =
        "This action is too large for the publication bridge and was NOT sent. Nothing was published and "
        + "the action id was not consumed — split the content into smaller actions, or shorten it, and "
        + "retry using the same action id.";

    /// <summary>The agent's argument name for the idempotency key, lifted into <c>scope.actionId</c>.</summary>
    internal const string ActionIdArgument = "actionId";

    /// <summary>The agent's argument name for evidence citations, lifted into <c>scope.sources</c>.</summary>
    internal const string SourcesArgument = "sources";

    /// <summary>
    /// Argument names the host consumes rather than forwards as siblings of <c>scope</c>. <c>scope</c>
    /// itself is here so a model-supplied one is DROPPED — writing the host's copy afterwards would
    /// otherwise leave the object carrying two <c>scope</c> members, and which one the backend honours
    /// would be a JSON-parser detail rather than a security decision.
    /// <para>
    /// The comparison is case-INSENSITIVE because the decision on the other side is. The daemon's
    /// controller binds with <see cref="JsonSerializerDefaults.Web"/>, which matches member names
    /// case-insensitively, and the host writes its own <c>scope</c> FIRST — so a surviving <c>Scope</c>
    /// would be the later member and the one the binder honours. Under an exact-name comparison the
    /// whole guard is bypassable by pressing shift.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> HostConsumedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "scope",
        ActionIdArgument,
        SourcesArgument,
    };

    /// <summary>
    /// Default headers a shared <see cref="HttpClient"/> must not carry, because it merges them into
    /// every request it sends — which would present an unrelated credential to the daemon on every
    /// publication.
    /// </summary>
    private static readonly string[] CredentialHeaderNames = ["Authorization", "Proxy-Authorization", "Cookie"];

    /// <summary>
    /// The members of the daemon's typed <c>PublicationOutcome</c>, and the ONLY members that reach the
    /// model. Everything else in a backend answer is dropped: a body from the credential-holding side
    /// can carry provider tokens, internal hostnames and stack frames, and a member nobody designed must
    /// not become a channel into the review transcript.
    /// <para>
    /// This list is the contract seam with the daemon's publication operations. A member added there and
    /// not here is silently invisible to the agent — which is the safe direction to fail, but it does
    /// mean the two must be changed together.
    /// </para>
    /// </summary>
    private static readonly string[] ProjectedOutcomeFields =
    [
        "status",
        "actionId",
        "providerReviewId",
        "providerThreadId",
        "providerCommentId",
        "providerPermalink",
        "relationshipDegraded",
        "acceptedAt",
        "rejectionCode",
    ];

    private readonly HttpClient _httpClient;
    private readonly Uri _baseAddress;
    private readonly ReviewPublicationScope _scope;
    private readonly string _secret;
    private readonly TimeSpan _timeout;

    /// <summary>The budget one action gets before the backend counts as having failed to answer.</summary>
    internal TimeSpan RequestTimeout => _timeout;

    /// <summary>Creates a bridge bound to one round-scoped conversation.</summary>
    /// <remarks>
    /// The base address is held here rather than assigned to <see cref="HttpClient.BaseAddress" />
    /// because one client is shared by every conversation in the process; mutating its base address per
    /// conversation would be a race between concurrent agent builds. Request URIs are absolute.
    /// </remarks>
    /// <param name="httpClient">Process-wide client; must carry no default credential header.</param>
    /// <param name="baseAddress">Absolute base URL of the daemon that owns the publication route.</param>
    /// <param name="scope">The host-owned round scope every action on this bridge is written with.</param>
    /// <param name="secret">The raw bridge secret, presented only in <see cref="AuthHeaderName"/>.</param>
    /// <param name="timeout">Per-action budget; <see cref="DefaultRequestTimeout"/> when omitted.</param>
    /// <exception cref="ArgumentNullException">The client, base address or scope is null.</exception>
    /// <exception cref="ArgumentException">
    /// The base address is relative, the secret is blank, the timeout is not positive, or the client
    /// carries a default credential header it would forward to the daemon.
    /// </exception>
    public ReviewPublicationBridgeClient(
        HttpClient httpClient,
        Uri baseAddress,
        ReviewPublicationScope scope,
        string secret,
        TimeSpan? timeout = null
    )
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        if (!baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("The bridge base address must be absolute.", nameof(baseAddress));
        }

        if (timeout is { } budget && budget <= TimeSpan.Zero)
        {
            throw new ArgumentException("The bridge request timeout must be positive.", nameof(timeout));
        }

        foreach (var header in CredentialHeaderNames)
        {
            if (httpClient.DefaultRequestHeaders.Contains(header))
            {
                throw new ArgumentException(
                    $"The publication bridge refuses a client carrying a default '{header}' header: "
                        + "HttpClient merges its defaults into every request, which would forward an "
                        + "unrelated credential to the daemon. Only the bridge secret may travel here.",
                    nameof(httpClient)
                );
            }
        }

        _httpClient = httpClient;
        _baseAddress = EnsureTrailingSlash(baseAddress);
        _scope = scope;
        _secret = secret;
        _timeout = timeout ?? DefaultRequestTimeout;
    }

    /// <summary>
    /// Builds a bridge when the deployment is configured for typed publication, and <c>null</c> when it
    /// is not. A missing scope, a missing or unusable base URL, or a blank secret all mean no bridge —
    /// and therefore no publication tools at all, rather than tools that fail on every call.
    /// </summary>
    public static ReviewPublicationBridgeClient? TryCreate(
        HttpClient httpClient,
        ReviewPublicationScope? scope,
        string? baseUrl,
        string? secret
    )
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        return
            scope is null
            || string.IsNullOrWhiteSpace(secret)
            || !Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            ? null
            : new ReviewPublicationBridgeClient(httpClient, parsed, scope, secret);
    }

    /// <summary>
    /// Keeps a configured path prefix in every resolved URI. <c>new Uri(base, relative)</c> discards the
    /// last segment of a base that does not end in a slash, so <c>https://host/daemon</c> would silently
    /// resolve to <c>https://host/api/...</c>.
    /// </summary>
    private static Uri EnsureTrailingSlash(Uri baseAddress) =>
        baseAddress.AbsoluteUri.EndsWith('/') ? baseAddress : new Uri(baseAddress.AbsoluteUri + "/");

    /// <inheritdoc />
    public async Task<ReviewPublicationResult> SendAsync(
        string operation,
        JsonElement arguments,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var payload = BuildPayload(arguments);
        if (Encoding.UTF8.GetByteCount(payload) > MaxRequestBytes)
        {
            // Refused BEFORE the send, so the action id is never consumed and the agent can retry the
            // shortened action under the same id (spec §6.5).
            return new ReviewPublicationResult(false, RequestTooLargeMessage, RequestTooLargeCode);
        }

        var target = new Uri(_baseAddress, $"api/review-publication/rounds/{_scope.RoundId}/actions/{operation}");

        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(AuthHeaderName, _secret);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);

        try
        {
            // ResponseHeadersRead: the answer is bounded below, and buffering the whole body inside
            // SendAsync would spend that memory before any ceiling could apply.
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token)
                .ConfigureAwait(false);

            if (!IsConfiguredOrigin(response.RequestMessage?.RequestUri ?? target))
            {
                return Unavailable();
            }

            var body = await ReadBoundedAsync(response, budget.Token).ConfigureAwait(false);
            return body is null ? Unavailable() : Interpret(response.StatusCode, body);
        }
        catch (Exception ex)
            when (ex is HttpRequestException or OperationCanceledException or IOException
                && !cancellationToken.IsCancellationRequested
            )
        {
            // The exception text names hosts, ports and sometimes credentials. The agent needs to know
            // the action did not land, not how the socket failed. The caller's own cancellation is
            // excluded by the filter and propagates: an abandoned review turn is not a backend answer.
            return Unavailable();
        }
    }

    /// <summary>
    /// Whether an answer came back from the origin this bridge was configured for. .NET's auto-redirect
    /// clears only <c>Authorization</c>, so a redirect off-origin re-sends
    /// <see cref="AuthHeaderName"/> to wherever it points. The host cannot un-send that, but it must not
    /// compound the leak by treating a stranger's body as the daemon's decision about the action.
    /// Prevention belongs upstream, on the shared client: <c>AllowAutoRedirect = false</c>.
    /// </summary>
    private bool IsConfiguredOrigin(Uri uri) =>
        uri.IsAbsoluteUri
        && string.Equals(uri.Scheme, _baseAddress.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, _baseAddress.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == _baseAddress.Port;

    /// <summary>
    /// Reads at most <see cref="MaxResponseBytes"/> of the answer, or <c>null</c> when the backend keeps
    /// going past it. Deliberately bounds the READ rather than trusting <c>Content-Length</c>: a body
    /// that lies about its size, or declares none at all, is exactly the case this exists for.
    /// </summary>
    private static async Task<string?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[MaxResponseBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total > MaxResponseBytes ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>
    /// Serializes the request the daemon's controller deserializes: a host-authored <c>scope</c> object
    /// plus the agent's remaining arguments as its siblings. Property names are camelCase because the
    /// controller binds with <see cref="JsonSerializerDefaults.Web"/>.
    /// </summary>
    /// <remarks>
    /// The agent's own values — above all the body prose — are copied through with
    /// <see cref="JsonElement.WriteTo"/>, so text arrives at the backend character-for-character rather
    /// than re-encoded. <c>sources</c> is always written, as an empty array when the agent cited none:
    /// the daemon's <c>PublicationScope.Sources</c> is a non-nullable list, and omitting the key would
    /// hand its store a null to dereference rather than an empty citation set.
    /// </remarks>
    private string BuildPayload(JsonElement arguments)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            writer.WritePropertyName("scope");
            writer.WriteStartObject();
            writer.WriteNumber("roundId", _scope.RoundId);
            WriteAgentString(writer, arguments, ActionIdArgument);
            writer.WriteString("provider", _scope.Provider);
            writer.WriteNumber("repoId", _scope.RepoId);
            writer.WriteString("prId", _scope.PrId);
            writer.WriteString("expectedHeadSha", _scope.ExpectedHeadSha);
            WriteSources(writer, arguments);
            writer.WriteBoolean("livePostingAuthorized", _scope.LivePostingAuthorized);
            writer.WriteEndObject();

            if (arguments.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in arguments.EnumerateObject())
                {
                    if (!HostConsumedKeys.Contains(property.Name))
                    {
                        property.WriteTo(writer);
                    }
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Copies an agent-supplied string argument into the scope, or writes an empty one.</summary>
    private static void WriteAgentString(Utf8JsonWriter writer, JsonElement arguments, string name)
    {
        writer.WriteString(
            name,
            arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : string.Empty
        );
    }

    /// <summary>Copies the agent's evidence citations into the scope, defaulting to an empty array.</summary>
    private static void WriteSources(Utf8JsonWriter writer, JsonElement arguments)
    {
        writer.WritePropertyName(SourcesArgument);
        if (
            arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(SourcesArgument, out var sources)
            && sources.ValueKind == JsonValueKind.Array
        )
        {
            sources.WriteTo(writer);
            return;
        }

        writer.WriteStartArray();
        writer.WriteEndArray();
    }

    /// <summary>
    /// Maps one backend answer onto the two outcomes the spec defines. Anything that is not a receipt or
    /// a parseable typed rejection is treated as "no typed answer", never as a rejection with an
    /// improvised code and never with its body attached.
    /// </summary>
    /// <remarks>
    /// A 200 is only a receipt when its <c>rejectionCode</c> is absent. The daemon's controller returns
    /// 200 exclusively for <c>RejectionCode is null</c>, so the extra check costs nothing today — but a
    /// backend that ever pairs 200 with a code must not have that read to the agent as a receipt for an
    /// action that did not publish. In the other direction a 409/422 carrying no code is a non-answer,
    /// because there is no mechanical reason for the agent to act on.
    /// </remarks>
    private static ReviewPublicationResult Interpret(HttpStatusCode status, string body)
    {
        if (status is not (HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity))
        {
            return Unavailable();
        }

        var (projected, rejectionCode) = Project(body);
        if (projected is null)
        {
            return Unavailable();
        }

        if (rejectionCode is { } code)
        {
            return new ReviewPublicationResult(false, projected, code);
        }

        return status == HttpStatusCode.OK ? new ReviewPublicationResult(true, projected, null) : Unavailable();
    }

    /// <summary>
    /// Reduces a backend answer to the typed outcome members, or returns a null projection when the body
    /// is not a JSON object at all. A 200 carrying a proxy's HTML page, an empty body or a bare JSON
    /// value is NOT a receipt: it says nothing about whether the action published, and its text can
    /// carry provider tokens, internal hostnames and stack frames.
    /// </summary>
    private static (string? Projected, string? RejectionCode) Project(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return (null, null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var field in ProjectedOutcomeFields)
                {
                    if (root.TryGetProperty(field, out var value))
                    {
                        writer.WritePropertyName(field);
                        value.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            return (Encoding.UTF8.GetString(buffer.ToArray()), ReadRejectionCode(root));
        }
    }

    /// <summary>The rejection code carried by a typed rejection body, or null when there is not one.</summary>
    private static string? ReadRejectionCode(JsonElement root) =>
        root.TryGetProperty("rejectionCode", out var code)
        && code.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(code.GetString())
            ? code.GetString()
            : null;

    private static ReviewPublicationResult Unavailable() =>
        new(false, BackendUnavailableMessage, BackendUnavailableCode);
}
