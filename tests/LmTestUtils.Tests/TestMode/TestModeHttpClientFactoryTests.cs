using System.Net;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

namespace AchieveAi.LmDotnetTools.LmTestUtils.Tests.TestMode;

public class TestModeHttpClientFactoryTests
{
    [Fact]
    public async Task OpenAiHandler_NonStreamingInstructionChain_ReturnsChatCompletionJson()
    {
        using var client = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0, wordsPerChunk: 3);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://test-mode/v1/chat/completions")
        {
            Content = new StringContent(
                """
                {
                  "model": "test-model",
                  "stream": false,
                  "messages": [
                    {
                      "role": "user",
                      "content": "Hello\n<|instruction_start|>{\"instruction_chain\":[{\"id_message\":\"non-stream\",\"messages\":[{\"text_message\":{\"length\":8}}]}]}<|instruction_end|>"
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json"
            ),
        };

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("choices").GetArrayLength() > 0);

        var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        Assert.False(string.IsNullOrWhiteSpace(content));

        var usage = root.GetProperty("usage");
        Assert.Equal(100, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(50, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(150, usage.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task OpenAiFactory_WithCapture_CapturesRequests()
    {
        var capture = new RequestCapture();
        using var client = TestModeHttpClientFactory.CreateOpenAiTestClient(capture: capture, chunkDelayMs: 0);

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://test-mode/v1/chat/completions")
        {
            Content = new StringContent(
                """
                {
                  "model": "captured-model",
                  "stream": false,
                  "messages": [{"role":"user","content":"ping"}]
                }
                """,
                Encoding.UTF8,
                "application/json"
            ),
        };

        _ = await client.SendAsync(request);

        Assert.Equal(1, capture.RequestCount);
        var captured = capture.GetOpenAIRequest();
        Assert.NotNull(captured);
        Assert.Equal("captured-model", captured.Model);
    }

    [Fact]
    public async Task OpenAiFactory_WithStatusSequence_UsesSequenceBeforeInnerHandler()
    {
        using var client = TestModeHttpClientFactory.CreateOpenAiTestClient(
            statusSequence: [HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK],
            chunkDelayMs: 0
        );

        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, "http://test-mode/v1/chat/completions")
        {
            Content = new StringContent(
                """
                {
                  "model": "test-model",
                  "stream": false,
                  "messages": [{"role":"user","content":"first"}]
                }
                """,
                Encoding.UTF8,
                "application/json"
            ),
        };

        using var secondRequest = new HttpRequestMessage(HttpMethod.Post, "http://test-mode/v1/chat/completions")
        {
            Content = new StringContent(
                """
                {
                  "model": "test-model",
                  "stream": false,
                  "messages": [{"role":"user","content":"second"}]
                }
                """,
                Encoding.UTF8,
                "application/json"
            ),
        };

        using var firstResponse = await client.SendAsync(firstRequest);
        using var secondResponse = await client.SendAsync(secondRequest);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
    }
}
