namespace CodeReviewDaemon.Sample.Auth;

/// <summary>
/// The daemon's webhook security layer (plan §9), enforced in front of the shared
/// <c>AuthWebhookController</c> for the <c>POST /api/auth/webhook/{provider}</c> route — and ONLY there.
/// It runs before MVC binds the body or any token is resolved: it caps the body, requires
/// <c>application/json</c>, and validates the gateway's HMAC signature, freshness timestamp, and
/// single-use delivery id, rejecting anything that fails closed. The shared controller is left untouched
/// (so <c>LmStreaming.Sample</c> is unaffected) and this adds no route, so the daemon's single-endpoint
/// surface is preserved; the controller's own shared-secret check remains as defence-in-depth.
/// </summary>
internal sealed class WebhookVerificationMiddleware
{
    /// <summary>Lowercase-hex HMAC-SHA256 over <c>{timestamp}.{rawBody}</c>.</summary>
    public const string SignatureHeader = "X-Sandbox-Signature";

    /// <summary>Unix-seconds (or ISO-8601) send time, bound into the signature.</summary>
    public const string TimestampHeader = "X-Sandbox-Timestamp";

    /// <summary>Unique per-callback id; a repeat within the TTL is a rejected replay.</summary>
    public const string DeliveryHeader = "X-Sandbox-Delivery-Id";

    private const string RoutePrefix = "/api/auth/webhook";

    private readonly RequestDelegate _next;
    private readonly WebhookRequestVerifier _verifier;
    private readonly DeliveryReplayCache _replayCache;
    private readonly long _maxBodyBytes;
    private readonly ILogger<WebhookVerificationMiddleware> _logger;

    public WebhookVerificationMiddleware(
        RequestDelegate next,
        WebhookRequestVerifier verifier,
        DeliveryReplayCache replayCache,
        WebhookVerificationLimits limits,
        ILogger<WebhookVerificationMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _replayCache = replayCache ?? throw new ArgumentNullException(nameof(replayCache));
        _maxBodyBytes = (limits ?? throw new ArgumentNullException(nameof(limits))).MaxBodyBytes;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;

        // Only POSTs to the webhook route are verified; anything else (there is nothing else) passes.
        if (!HttpMethods.IsPost(request.Method)
            || !request.Path.StartsWithSegments(RoutePrefix, StringComparison.OrdinalIgnoreCase))
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
            request.Headers[SignatureHeader],
            request.Headers[TimestampHeader],
            request.Headers[DeliveryHeader]);

        var now = DateTimeOffset.UtcNow;
        var result = _verifier.Verify(input, now);
        if (!result.IsValid)
        {
            _logger.LogWarning("Rejected webhook callback for provider {Provider}: {Rejection}.", input.Provider, result.Rejection);
            await RejectAsync(context, StatusFor(result.Rejection), result.Rejection.ToString()).ConfigureAwait(false);
            return;
        }

        // Signature is valid → the delivery id is trustworthy; reject a replay of it.
        if (!_replayCache.TryRegister(input.DeliveryId!, now))
        {
            _logger.LogWarning("Rejected replayed webhook delivery {DeliveryId} for provider {Provider}.", input.DeliveryId, input.Provider);
            await RejectAsync(context, StatusCodes.Status409Conflict, "duplicate delivery").ConfigureAwait(false);
            return;
        }

        // Rewind so the shared controller can still bind [FromBody].
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

    private static int StatusFor(WebhookRejection rejection) => rejection switch
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
