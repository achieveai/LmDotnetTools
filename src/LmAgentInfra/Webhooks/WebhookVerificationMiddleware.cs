namespace AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;

/// <summary>
/// The inbound webhook security layer (ADR 0005), enforced in front of the
/// <see cref="Controllers.AuthWebhookController"/> for the <c>POST /api/auth/webhook/{provider}</c>
/// route — and ONLY there. It runs before MVC binds the body or any token is resolved: it caps the
/// body, requires <c>application/json</c>, and validates the sender's HMAC signature, freshness
/// timestamp, and single-use delivery id, rejecting anything that fails closed. It adds no route, so a
/// host's endpoint surface is unchanged, and the controller's own shared-secret check remains as
/// defence-in-depth.
/// <para>
/// Hosts wire this by hand (this library registers nothing); a host that does not wire it is
/// unaffected. Notably the sandbox gateway does not sign its callbacks, so
/// <c>CodeReviewDaemon.Sample</c> deliberately leaves this unwired — see its <c>Program.cs</c>.
/// </para>
/// </summary>
public sealed class WebhookVerificationMiddleware
{
    private const string RoutePrefix = "/api/auth/webhook";

    private readonly RequestDelegate _next;
    private readonly WebhookRequestVerifier _verifier;
    private readonly DeliveryReplayCache _replayCache;
    private readonly long _maxBodyBytes;
    private readonly ILogger<WebhookVerificationMiddleware> _logger;

    /// <summary>Creates the middleware over its verifier, replay cache, and shared limits.</summary>
    /// <param name="next">The next term in the pipeline.</param>
    /// <param name="verifier">Decides provider/content-type/size/freshness/signature.</param>
    /// <param name="replayCache">Rejects a repeat of an already-accepted delivery id.</param>
    /// <param name="limits">Shared limits; only the body cap is read here.</param>
    /// <param name="logger">Diagnostics sink; only opaque, truncated identifiers are logged.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public WebhookVerificationMiddleware(
        RequestDelegate next,
        WebhookRequestVerifier verifier,
        DeliveryReplayCache replayCache,
        WebhookVerificationLimits limits,
        ILogger<WebhookVerificationMiddleware> logger
    )
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _replayCache = replayCache ?? throw new ArgumentNullException(nameof(replayCache));
        _maxBodyBytes = (limits ?? throw new ArgumentNullException(nameof(limits))).MaxBodyBytes;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// The exact route shape this layer guards: <c>POST /api/auth/webhook/{provider}</c> with exactly one
    /// non-empty provider segment and no trailing path. Used both by the host's <c>UseWhen</c> branch and
    /// here, so a suffix path like <c>/api/auth/webhook/github/extra</c> is never HMAC-verified or allowed
    /// to consume a delivery id — it simply falls through to MVC (which 404s it).
    /// </summary>
    /// <param name="request">The incoming request to classify.</param>
    /// <returns><c>true</c> when the request targets the guarded route.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public static bool IsWebhookRoute(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (
            !HttpMethods.IsPost(request.Method)
            || !request.Path.StartsWithSegments(RoutePrefix, StringComparison.OrdinalIgnoreCase, out var remaining)
        )
        {
            return false;
        }

        var segment = (remaining.Value ?? string.Empty).Trim('/');
        return segment.Length > 0 && !segment.Contains('/', StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies one request and either rejects it with a status that names the failure class, or passes
    /// it downstream with the body rewound so MVC can still bind it.
    /// </summary>
    /// <param name="context">The request being processed.</param>
    /// <returns>A task that completes when the request has been rejected or handled downstream.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;

        // Verify only the exact webhook route; anything else passes straight through.
        if (!IsWebhookRoute(request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Cheap pre-read guard: a declared length over the cap is rejected before buffering the body.
        if (request.ContentLength is { } declared && declared > _maxBodyBytes)
        {
            await RejectAsync(context, StatusCodes.Status413PayloadTooLarge, "body too large").ConfigureAwait(false);
            return;
        }

        var (body, overflowed) = await ReadCappedAsync(request, context.RequestAborted).ConfigureAwait(false);
        if (overflowed)
        {
            await RejectAsync(context, StatusCodes.Status413PayloadTooLarge, "body too large").ConfigureAwait(false);
            return;
        }

        var input = new WebhookVerificationInput(
            ProviderSegment(request.Path),
            request.ContentType,
            body,
            request.Headers[WebhookHeaderNames.Signature],
            request.Headers[WebhookHeaderNames.Timestamp],
            request.Headers[WebhookHeaderNames.DeliveryId]
        );

        var now = DateTimeOffset.UtcNow;
        var result = _verifier.Verify(input, now);
        if (!result.IsValid)
        {
            _logger.LogWarning(
                "Rejected webhook callback for provider {Provider}: {Rejection}.",
                input.Provider,
                result.Rejection
            );
            await RejectAsync(context, StatusFor(result.Rejection), result.Rejection.ToString()).ConfigureAwait(false);
            return;
        }

        // Signature is valid → the delivery id is trustworthy; reject a replay of it.
        if (!_replayCache.TryRegister(input.DeliveryId!, now))
        {
            // Log only a truncated prefix of the (single-use) delivery id — enough to correlate, without
            // preserving the full replay identifier in the logs.
            _logger.LogWarning(
                "Rejected replayed webhook delivery {DeliveryIdPrefix} for provider {Provider}.",
                Truncate(input.DeliveryId!),
                input.Provider
            );
            await RejectAsync(context, StatusCodes.Status409Conflict, "duplicate delivery").ConfigureAwait(false);
            return;
        }

        // Rewind so the downstream controller can still bind [FromBody].
        request.Body.Position = 0;
        await _next(context).ConfigureAwait(false);
    }

    private async Task<(byte[] Body, bool Overflowed)> ReadCappedAsync(HttpRequest request, CancellationToken ct)
    {
        request.EnableBuffering();

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > _maxBodyBytes)
            {
                request.Body.Position = 0;
                return ([], true);
            }

            buffer.Write(chunk, 0, read);
        }

        request.Body.Position = 0;
        return (buffer.ToArray(), false);
    }

    private static string ProviderSegment(PathString path)
    {
        var value = path.Value ?? string.Empty;
        if (value.Length <= RoutePrefix.Length)
        {
            return string.Empty;
        }

        var rest = value.AsSpan(RoutePrefix.Length).TrimStart('/');
        var slash = rest.IndexOf('/');
        return (slash < 0 ? rest : rest[..slash]).ToString();
    }

    /// <summary>A short, correlation-only prefix of a delivery id (never the full single-use value).</summary>
    private static string Truncate(string deliveryId) => deliveryId.Length <= 8 ? "…" : deliveryId[..8] + "…";

    private static int StatusFor(WebhookRejection rejection) =>
        rejection switch
        {
            WebhookRejection.UnknownProvider => StatusCodes.Status404NotFound,
            WebhookRejection.UnsupportedContentType => StatusCodes.Status415UnsupportedMediaType,
            WebhookRejection.BodyTooLarge => StatusCodes.Status413PayloadTooLarge,
            WebhookRejection.MissingHeaders => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status401Unauthorized,
        };

    private static async Task RejectAsync(HttpContext context, int statusCode, string reason)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync($"webhook rejected: {reason}").ConfigureAwait(false);
    }
}
