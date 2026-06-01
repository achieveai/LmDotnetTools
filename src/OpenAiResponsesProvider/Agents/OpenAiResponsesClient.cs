using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;

/// <summary>
///     HTTP+SSE client for the OpenAI Responses API <c>/v1/responses</c> endpoint. Reads
///     <c>text/event-stream</c> framed JSON events, parses them with
///     <see cref="ResponseEventParser"/>, and yields typed <see cref="ResponseEvent"/> records.
/// </summary>
/// <remarks>
///     The client owns the supplied <see cref="HttpClient"/> when <c>disposeClient</c> is true.
///     Tests inject a pre-configured <c>HttpClient</c> with an in-process
///     <c>HttpMessageHandler</c> so they can short-circuit the upstream call.
/// </remarks>
public sealed class OpenAiResponsesClient : IOpenAiResponsesClient
{
    /// <summary>Default Responses API path (OpenAI). The GitHub Copilot host uses <c>/responses</c>.</summary>
    public const string DefaultResponsesPath = "/v1/responses";

    private static readonly JsonSerializerOptions s_serializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _disposeClient;
    private readonly string _responsesPath;
    private readonly ILogger<OpenAiResponsesClient> _logger;

    public OpenAiResponsesClient(
        HttpClient httpClient,
        bool disposeClient = false,
        ILogger<OpenAiResponsesClient>? logger = null,
        string? responsesPath = null
    )
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _disposeClient = disposeClient;
        _responsesPath = string.IsNullOrWhiteSpace(responsesPath) ? DefaultResponsesPath : responsesPath!;
        _logger = logger ?? NullLogger<OpenAiResponsesClient>.Instance;
    }

    /// <summary>
    ///     Convenience overload that builds a client against <paramref name="baseAddress"/> with
    ///     the given API key. Constructs an internal <see cref="HttpClient"/> that the returned
    ///     instance owns and disposes.
    /// </summary>
    public static OpenAiResponsesClient Create(
        Uri baseAddress,
        string? apiKey = null,
        ILogger<OpenAiResponsesClient>? logger = null,
        string? responsesPath = null
    )
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        var client = new HttpClient { BaseAddress = baseAddress };
        if (!string.IsNullOrEmpty(apiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return new OpenAiResponsesClient(client, disposeClient: true, logger, responsesPath);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ResponseEvent> StreamResponseAsync(
        ResponseCreateRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        // Force streaming on the wire even if the caller forgot. The server will reject
        // stream=false for our event-driven contract anyway; failing fast here keeps the
        // diagnostic close to the call site.
        var streamingRequest = request.Stream is true ? request : request with { Stream = true };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _responsesPath)
        {
            Content = JsonContent.Create(streamingRequest, options: s_serializerOptions),
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        _logger.LogDebug(
            "POST {Path} model={Model} inputItemCount={InputCount} stream=true",
            _responsesPath,
            streamingRequest.Model,
            streamingRequest.Input.Count
        );

        using var response = await _http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        ).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody;
            try
            {
                errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception readEx) when (readEx is not OperationCanceledException)
            {
                errorBody = "<unable to read response body>";
            }

            _logger.LogError(
                "OpenAI Responses API error: {StatusCode} {ReasonPhrase} Body={ErrorBody}",
                (int)response.StatusCode,
                response.ReasonPhrase,
                errorBody
            );

            throw new HttpRequestException(
                $"OpenAI Responses API returned {(int)response.StatusCode} {response.ReasonPhrase}: {errorBody}",
                inner: null,
                response.StatusCode
            );
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        await foreach (var ev in ReadSseAsync(responseStream, cancellationToken).ConfigureAwait(false))
        {
            yield return ev;
        }
    }

    /// <summary>
    ///     Reads SSE-framed JSON events from <paramref name="stream"/>. Each <c>data:</c> payload
    ///     is parsed via <see cref="ResponseEventParser"/>; comments and unknown event-name lines
    ///     are ignored. The stream terminates either on EOF or on a sentinel <c>data: [DONE]</c>
    ///     line for parity with chat-completions clients that expect it.
    /// </summary>
    internal static async IAsyncEnumerable<ResponseEvent> ReadSseAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, 1024, leaveOpen: true);
        var dataBuffer = new StringBuilder();

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                // Empty line: dispatch any accumulated data block.
                if (dataBuffer.Length > 0)
                {
                    var payload = dataBuffer.ToString();
                    _ = dataBuffer.Clear();
                    if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
                    {
                        yield break;
                    }

                    yield return ResponseEventParser.Parse(payload);
                }

                continue;
            }

            if (line[0] == ':')
            {
                continue; // SSE comment
            }

            // We accept either "data: {json}" or a raw "{json}" line for tolerance with mock servers
            // that omit the prefix in tests.
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var content = line[5..].TrimStart();
                if (dataBuffer.Length > 0)
                {
                    _ = dataBuffer.Append('\n');
                }

                _ = dataBuffer.Append(content);
            }
        }

        // Final dispatch if the stream ended without a trailing blank line.
        if (dataBuffer.Length > 0)
        {
            var payload = dataBuffer.ToString();
            if (!string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                yield return ResponseEventParser.Parse(payload);
            }
        }
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _http.Dispose();
        }
    }
}
