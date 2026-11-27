using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.Sandbox.Integration.Tests;

/// <summary>
/// The SDK's transport for a borrowed <see cref="HttpClient"/>: it never follows redirects (the SDK's precondition),
/// records every request the SDK sends, and can act at the moment the gateway accepts a preparation (<c>202</c>), so
/// a test can cancel exactly between the submission and the first status poll.
/// </summary>
internal sealed class RecordingGatewayHandler : DelegatingHandler
{
    public RecordingGatewayHandler()
        : base(new HttpClientHandler { AllowAutoRedirect = false }) { }

    public ConcurrentQueue<(HttpMethod Method, Uri Uri)> Requests { get; } = new();

    /// <summary>Called once per accepted preparation with its operation id, before the SDK sees the reply.</summary>
    public Action<string>? OnPreparationAccepted { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        Requests.Enqueue((request.Method, request.RequestUri!));
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (
            OnPreparationAccepted is { } onAccepted
            && request.Method == HttpMethod.Post
            && request.RequestUri!.AbsolutePath.EndsWith("/plugins/prepare", StringComparison.Ordinal)
            && response.StatusCode == HttpStatusCode.Accepted
        )
        {
            // Buffer first, so the SDK still reads the whole body after this look.
            await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            using var body = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
            );
            onAccepted(body.RootElement.GetProperty("operationId").GetString()!);
        }

        return response;
    }

    /// <summary>The requests recorded after <paramref name="skip"/> earlier ones.</summary>
    public IReadOnlyList<(HttpMethod Method, Uri Uri)> Since(int skip) => [.. Requests.Skip(skip)];
}
