using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.ServerSentEvents;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Agents;

public class OpenClient : IOpenClient, IDisposable
{
    public readonly static JsonSerializerOptions S_jsonSerializerOptions = CreateJsonSerializerOptions();

    private readonly HttpClient _httpClient;

    private readonly string _baseUrl;

    public OpenClient(string apiKey, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl, nameof(baseUrl));
        ArgumentNullException.ThrowIfNull(apiKey, nameof(apiKey));

        _baseUrl = baseUrl;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(
                "application/json"));

        _httpClient.DefaultRequestHeaders.Add(
            "Authorization",
            $"Bearer {apiKey}");

        _httpClient.Timeout = TimeSpan.FromMinutes(5);
    }

    public OpenClient(HttpClient httpClient, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(httpClient, nameof(httpClient));
        ArgumentNullException.ThrowIfNull(baseUrl, nameof(baseUrl));
        _baseUrl = baseUrl;
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        _httpClient.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task<ChatCompletionResponse> CreateChatCompletionsAsync(
        ChatCompletionRequest chatCompletionRequest,
        CancellationToken cancellationToken = default)
    {
        chatCompletionRequest = chatCompletionRequest with { Stream = false };
        var response = await HttpRequestRaw(
            _httpClient,
            HttpMethod.Post,
            chatCompletionRequest,
            $"{_baseUrl.TrimEnd('/')}/chat/completions",
            cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();

        return await JsonSerializer.DeserializeAsync<ChatCompletionResponse>(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                S_jsonSerializerOptions,
                cancellationToken
            ) ?? throw new Exception("Failed to deserialize response");
    }

    public async IAsyncEnumerable<ChatCompletionResponse> StreamingChatCompletionsAsync(
        ChatCompletionRequest chatCompletionRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        chatCompletionRequest = chatCompletionRequest with { Stream = true };
        var response = await HttpRequestRaw(
            _httpClient,
            HttpMethod.Post,
            chatCompletionRequest,
            $"{_baseUrl.TrimEnd('/')}/chat/completions",
            streaming: true,
            cancellationToken: cancellationToken);

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var sseItem in SseParser.Create(stream).EnumerateAsync(cancellationToken))
        {
            // Skip "[DONE]" event which indicates the end of the stream
            if (sseItem.Data == "[DONE]")
            {
                break;
            }

            // Parse the SSE data as JSON into a ChatCompletionResponse
            ChatCompletionResponse? res = null;
            try
            {
                res = JsonSerializer.Deserialize<ChatCompletionResponse>(
                    sseItem.Data,
                    S_jsonSerializerOptions);

                if (res == null)
                {
                    continue;
                }

                // Add default assistant role if not present
                if (res.Choices?.Count > 0 && res.Choices[0].FinishReason != null)
                {
                    if (res.Choices[0].Delta?.Role.HasValue != true)
                    {
                        res.Choices[0].Delta = new ChatMessage
                        {
                            Role = RoleEnum.Assistant,
                            Content = res.Choices[0].Delta?.Content ?? new Union<string, Union<TextContent, ImageContent>[]>(string.Empty)
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to deserialize SSE response: {ex.Message}, Data: {sseItem.Data}");
            }

            if (res != null)
            {
                yield return res;
            }
        }
    }

    public static async Task<HttpResponseMessage> HttpRequestRaw(
        HttpClient httpClient,
        HttpMethod verb,
        object? postData,
        string url,
        bool streaming = false,
        bool retried = true,
        CancellationToken cancellationToken = default)
    {
        // var url = $"{_baseUrl.TrimEnd('/')}{url_path}";
        HttpResponseMessage response;
        string resultAsString;
        HttpRequestMessage req = new HttpRequestMessage(verb, url);
        if (postData != null)
        {
            if (postData is HttpContent)
            {
                req.Content = postData as HttpContent;
            }
            else
            {
                string jsonContent = JsonSerializer.Serialize(
                    postData,
                    S_jsonSerializerOptions);

                var stringContent = new StringContent(
                    jsonContent,
                    Encoding.UTF8,
                    "application/json");

                req.Content = stringContent;
            }
        }

        try
        {
            response = await httpClient.SendAsync(
                req,
                streaming
                ? HttpCompletionOption.ResponseHeadersRead
                : HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        }
        catch (HttpRequestException)
        {
            if (retried)
            {
                throw;
            }
            else
            {
                await Task.Delay(1000);
                return await HttpRequestRaw(
                    httpClient,
                    verb,
                    postData,
                    url,
                    streaming,
                    false,
                    cancellationToken);
            }
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }
        else
        {
            try
            {
                resultAsString = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception e)
            {
                resultAsString =
                    "Additionally, the following error was thrown when attempting to read the response content: " +
                    e.ToString();
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new AuthenticationException(
                    "Server rejected your authorization, most likely due to an invalid API Key. Full API response follows: " +
                    resultAsString);
            }
            else
            {
                if (retried)
                {
                    switch (response.StatusCode)
                    {
                        case System.Net.HttpStatusCode.InternalServerError:
                            throw new HttpRequestException(
                                "Server had an internal server error, which can happen occasionally.  Please retry your request.  " +
                                GetErrorMessage(resultAsString, response, url, url));
                        case System.Net.HttpStatusCode.BadGateway:
                            throw new HttpRequestException(
                                "Server is a gateway or proxy server and returned an invalid response.  " +
                                GetErrorMessage(resultAsString, response, url, url));
                        case System.Net.HttpStatusCode.ServiceUnavailable:
                            throw new HttpRequestException(
                                "Server is currently unavailable, which can happen occasionally.  Please retry your request.  " +
                                GetErrorMessage(resultAsString, response, url, url));
                        case System.Net.HttpStatusCode.TooManyRequests:
                            throw new HttpRequestException(
                                "Server is currently rate limiting your requests.  Please retry your request.  " +
                                GetErrorMessage(resultAsString, response, url, url));
                        case System.Net.HttpStatusCode.RequestTimeout:
                            throw new HttpRequestException(
                                "Server timed out your request.  Please retry your request.  " +
                                GetErrorMessage(resultAsString, response, url, url));
                        default:
                            throw new HttpRequestException(
                                GetErrorMessage(resultAsString, response, url, url));
                    }
                }
                else
                {
                    await Task.Delay(1000);
                    return await HttpRequestRaw(
                        httpClient,
                        verb,
                        postData,
                        url,
                        streaming,
                        false,
                        cancellationToken);
                }
            }
        }
    }

    internal static string GetErrorMessage(
        string resultAsString,
        HttpResponseMessage response,
        string name,
        string description = "")
    {
        return $"Error at {name} ({description}) with HTTP status code: {response.StatusCode}. Content: {resultAsString ?? "<no content>"}";
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static JsonSerializerOptions CreateJsonSerializerOptions()
    {
        var jsonSerializerOptions = new JsonSerializerOptions()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowTrailingCommas = true,
        };

        jsonSerializerOptions.Converters.Add(new UnionJsonConverter<string, Union<TextContent, ImageContent>[]>());
        jsonSerializerOptions.Converters.Add(new UnionJsonConverter<TextContent, ImageContent>());

        return jsonSerializerOptions;
    }
}