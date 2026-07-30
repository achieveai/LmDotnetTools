using System.Net;
using System.Net.Http.Headers;
using AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// Delivers lifecycle events over signed HTTP POST (ADR 0005).
/// <para>
/// The request this builds carries exactly four headers: <c>Content-Type: application/json</c> and
/// the three <c>X-Sandbox-*</c> signature headers. That is a deliberate whitelist, not an
/// observation about what happens to be set. A lifecycle callback points at a third party the
/// subscriber chose, so anything the host's own outbound credentials — an <c>Authorization</c>
/// header, an <c>X-Sbx-*</c> sandbox-gateway header — would ride along on is a credential handed to
/// that third party. The client's default headers are cleared at construction and the request is
/// stripped again immediately before sending, because the two leaks arrive by different routes:
/// defaults are merged by <see cref="HttpClient"/> after this class is done building the request,
/// while a wrapping handler can add headers after any check this class performs on its own.
/// </para>
/// </summary>
public sealed class HttpLifecycleDeliverySender : ILifecycleDeliverySender, IDisposable
{
    /// <summary>
    /// Prefix of the sandbox-gateway credential headers. Distinct from the <c>X-Sandbox-</c>
    /// signature prefix this class does send — the two look alike and must not be conflated.
    /// </summary>
    private const string SandboxCredentialHeaderPrefix = "X-Sbx-";

    private const string AuthorizationHeaderName = "Authorization";

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HttpLifecycleDeliverySender> _logger;
    private readonly bool _ownsHttpClient;

    /// <summary>Creates a sender over its own client.</summary>
    /// <param name="httpClient">
    /// The client used for every delivery. Its default headers are cleared and its timeout disabled:
    /// attempt timeouts are the pipeline's to enforce, and a second competing timeout would make an
    /// attempt fail earlier than configured for reasons nothing logs.
    /// </param>
    /// <param name="timeProvider">Clock used to interpret an absolute <c>Retry-After</c> date.</param>
    /// <param name="logger">Diagnostics sink. Receives identifiers and status codes only.</param>
    public HttpLifecycleDeliverySender(
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILogger<HttpLifecycleDeliverySender> logger
    )
        : this(httpClient, timeProvider, logger, ownsHttpClient: true) { }

    private HttpLifecycleDeliverySender(
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILogger<HttpLifecycleDeliverySender> logger,
        bool ownsHttpClient
    )
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _timeProvider = timeProvider;
        _logger = logger;
        _ownsHttpClient = ownsHttpClient;

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// Creates a sender that borrows a client it must not dispose — for a client owned by
    /// <c>IHttpClientFactory</c>, whose lifetime the factory manages.
    /// </summary>
    /// <param name="httpClient">A client owned by someone else.</param>
    /// <param name="timeProvider">Clock used to interpret an absolute <c>Retry-After</c> date.</param>
    /// <param name="logger">Diagnostics sink. Receives identifiers and status codes only.</param>
    /// <returns>A sender that leaves <paramref name="httpClient"/> alive on dispose.</returns>
    public static HttpLifecycleDeliverySender OverSharedClient(
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILogger<HttpLifecycleDeliverySender> logger
    ) => new(httpClient, timeProvider, logger, ownsHttpClient: false);

    /// <inheritdoc />
    public async Task<LifecycleDeliveryResult> SendAsync(
        LifecycleSubscription subscription,
        string deliveryId,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);

        using var request = new HttpRequestMessage(HttpMethod.Post, subscription.CallbackUri)
        {
            Content = new ReadOnlyMemoryContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        // Signed over the delivery id the caller supplied, never a freshly minted one. The receiver
        // keys its replay cache on that id, so re-identifying a retry would present an ordinary
        // network retry as a second, distinct event.
        new WebhookRequestSigner(subscription.SigningSecret, _timeProvider)
            .Sign(body.Span, deliveryId)
            .ApplyTo(request);

        StripInheritedCredentials(request);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var result = Classify(response);
            if (result.Outcome != LifecycleDeliveryOutcome.Succeeded)
            {
                _logger.LogDebug(
                    "Lifecycle delivery {DeliveryId} to subscription {SubscriptionId} returned {StatusCode} ({Outcome})",
                    deliveryId,
                    subscription.SubscriptionId,
                    result.StatusCode,
                    result.Outcome
                );
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            // Rethrown rather than classified: only the pipeline knows whether its own shutdown token
            // or the attempt timeout fired, and those two mean opposite things.
            throw;
        }
        catch (HttpRequestException ex)
        {
            // Connection refused, DNS failure, TLS failure. All of them are properties of the network
            // at this instant rather than of the request, so all of them are worth another attempt.
            _logger.LogDebug(
                ex,
                "Lifecycle delivery {DeliveryId} to subscription {SubscriptionId} failed in transport",
                deliveryId,
                subscription.SubscriptionId
            );
            return LifecycleDeliveryResult.Retryable("transport");
        }
    }

    /// <summary>Disposes the client when this sender owns it.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    /// Removes host credentials that may have been attached upstream. The signature headers use the
    /// <c>X-Sandbox-</c> prefix and are therefore untouched by the <c>X-Sbx-</c> sweep.
    /// </summary>
    private static void StripInheritedCredentials(HttpRequestMessage request)
    {
        _ = request.Headers.Remove(AuthorizationHeaderName);

        var credentialHeaders = request
            .Headers.Where(header =>
                header.Key.StartsWith(SandboxCredentialHeaderPrefix, StringComparison.OrdinalIgnoreCase)
            )
            .Select(header => header.Key)
            .ToArray();

        foreach (var name in credentialHeaders)
        {
            _ = request.Headers.Remove(name);
        }
    }

    private LifecycleDeliveryResult Classify(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            return LifecycleDeliveryResult.Succeeded(status);
        }

        if (response.StatusCode == HttpStatusCode.Gone)
        {
            return LifecycleDeliveryResult.Gone(status);
        }

        if (
            response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || status >= 500
        )
        {
            return LifecycleDeliveryResult.Retryable("http_status", status, ReadRetryAfter(response));
        }

        // Everything else — including a redirect this client deliberately does not chase — is the
        // subscriber rejecting the request itself. Repeating it would only repeat the rejection.
        return LifecycleDeliveryResult.Permanent("http_status", status);
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retryAfter)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - _timeProvider.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }
}
