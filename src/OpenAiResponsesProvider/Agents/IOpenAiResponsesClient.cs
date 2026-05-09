using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;

/// <summary>
///     Transport contract for the OpenAI Responses API. Currently exposes streaming only —
///     the Responses API event grammar makes the streaming path the natural primary surface
///     and non-streaming consumers can buffer the stream into a final aggregate.
/// </summary>
public interface IOpenAiResponsesClient : IDisposable
{
    /// <summary>
    ///     Sends a <c>response.create</c> request and returns the resulting event stream.
    ///     Events are yielded in wire order.
    /// </summary>
    IAsyncEnumerable<ResponseEvent> StreamResponseAsync(
        ResponseCreateRequest request,
        CancellationToken cancellationToken = default
    );
}
