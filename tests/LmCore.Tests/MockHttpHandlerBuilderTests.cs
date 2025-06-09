using AchieveAi.LmDotnetTools.LmTestUtils;
using System.Net;
using System.Text.Json;
using Xunit;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text;
using System.Linq;
using System.Net.Http;

namespace AchieveAi.LmDotnetTools.LmCore.Tests;

/// <summary>
/// Tests for MockHttpHandlerBuilder - validates core mock infrastructure
/// This represents the foundation of WI-MM001: Core MockHttpHandlerBuilder Infrastructure
/// </summary>
public class MockHttpHandlerBuilderTests
{
    [Fact]
    public async Task MockHttpHandlerBuilder_BasicAnthropicResponse_ReturnsValidJson()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithAnthropicMessage("Hello from Claude!", "claude-3-sonnet-20240229", 10, 15)
            .Build();

        var httpClient = new HttpClient(handler);

        // Act
        var response = await httpClient.PostAsync(
            "https://api.anthropic.com/v1/messages",
            new StringContent("""{"model": "claude-3-sonnet-20240229", "messages": []}"""));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.NotNull(responseBody);
        
        var json = JsonDocument.Parse(responseBody);
        Assert.Equal("message", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("assistant", json.RootElement.GetProperty("role").GetString());
        Assert.Equal("Hello from Claude!", 
            json.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("claude-3-sonnet-20240229", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(10, json.RootElement.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal(15, json.RootElement.GetProperty("usage").GetProperty("output_tokens").GetInt32());
    }

    [Fact]
    public async Task MockHttpHandlerBuilder_BasicOpenAIResponse_ReturnsValidJson()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithOpenAIMessage("Hello from GPT!", "gpt-4", 12, 18)
            .Build();

        var httpClient = new HttpClient(handler);

        // Act
        var response = await httpClient.PostAsync(
            "https://api.openai.com/v1/chat/completions",
            new StringContent("""{"model": "gpt-4", "messages": []}"""));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.NotNull(responseBody);
        
        var json = JsonDocument.Parse(responseBody);
        Assert.Equal("chat.completion", json.RootElement.GetProperty("object").GetString());
        Assert.Equal("Hello from GPT!", 
            json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("gpt-4", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(12, json.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(18, json.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
        Assert.Equal(30, json.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task MockHttpHandlerBuilder_RequestCapture_CapturesRequestDetails()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .CaptureRequests(out var capture)
            .RespondWithAnthropicMessage("Test response")
            .Build();

        var httpClient = new HttpClient(handler);
        var requestContent = """{"model": "claude-3-sonnet-20240229", "messages": [{"role": "user", "content": "Hello"}]}""";

        // Act
        await httpClient.PostAsync(
            "https://api.anthropic.com/v1/messages",
            new StringContent(requestContent, System.Text.Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(1, capture.RequestCount);
        Assert.NotNull(capture.LastRequest);
        Assert.Equal(HttpMethod.Post, capture.LastRequest.Method);
        Assert.Contains("/v1/messages", capture.LastRequest.RequestUri?.ToString());
        
        var anthropicRequest = capture.GetAnthropicRequest();
        Assert.NotNull(anthropicRequest);
        Assert.Equal("claude-3-sonnet-20240229", anthropicRequest.Model);
    }

    [Fact]
    public async Task MockHttpHandlerBuilder_ErrorResponse_ReturnsSpecifiedError()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithError(HttpStatusCode.BadRequest, """{"error": "Invalid request"}""")
            .Build();

        var httpClient = new HttpClient(handler);

        // Act
        var response = await httpClient.PostAsync(
            "https://api.anthropic.com/v1/messages",
            new StringContent("{}"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid request", responseBody);
    }

    [Fact]
    public async Task MockHttpHandlerBuilder_RetryScenario_FailsThenSucceeds()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RetryScenario(2, HttpStatusCode.InternalServerError)
            .Build();

        var httpClient = new HttpClient(handler);

        // First two requests should fail
        var response1 = await httpClient.GetAsync("https://api.test.com/test");
        var response2 = await httpClient.GetAsync("https://api.test.com/test");
        
        // Third request should succeed
        var response3 = await httpClient.GetAsync("https://api.test.com/test");

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response1.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response2.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response3.StatusCode);
    }

    [Fact]
    public async Task MockHttpHandlerBuilder_RequestCapture_ExtractsAnthropicDetails()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .CaptureRequests(out var capture)
            .RespondWithAnthropicMessage("Test response")
            .Build();

        var httpClient = new HttpClient(handler);

        var requestBody = @"{""model"":""claude-3-sonnet-20240229"",""messages"":[{""role"":""user"",""content"":""Hello""}],""max_tokens"":100}";

        // Act
        var response = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(requestBody, Encoding.UTF8, "application/json"));

        // Assert
        Assert.True(response.IsSuccessStatusCode);
        Assert.NotNull(capture.LastRequest);
        Assert.Equal("POST", capture.LastRequest.Method.Method);

        var anthropicRequest = capture.GetAnthropicRequest();
        Assert.NotNull(anthropicRequest);
        Assert.Equal("claude-3-sonnet-20240229", anthropicRequest.Model);
        Assert.Single(anthropicRequest.Messages);
        var firstMessage = anthropicRequest.Messages.First();
        Assert.Equal("user", firstMessage.Role);
        Assert.Equal("Hello", firstMessage.Content);
    }

    /// <summary>
    /// Test basic tool use response generation - validates WI-MM003: Tool Use Response Support
    /// </summary>
    [Fact]
    public async Task MockHttpHandlerBuilder_ToolUseResponse_GeneratesValidAnthropicToolResponse()
    {
        // Arrange
        var toolInputData = new { relative_path = "." };
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithToolUse("python_mcp-list_directory", toolInputData, 
                "I'll help you list the files in the root directory.")
            .Build();

        var httpClient = new HttpClient(handler);

        // Act
        var response = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        // Assert
        Assert.True(response.IsSuccessStatusCode);
        
        var jsonResponse = await response.Content.ReadAsStringAsync();
        
        Assert.Contains("\"type\":\"tool_use\"", jsonResponse);
        Assert.Contains("\"name\":\"python_mcp-list_directory\"", jsonResponse);
        Assert.Contains("\"relative_path\":\".\"", jsonResponse);
        Assert.Contains("\"stop_reason\":\"tool_use\"", jsonResponse);

        // Validate JSON structure
        using var document = JsonDocument.Parse(jsonResponse);
        var root = document.RootElement;
        Assert.Equal("message", root.GetProperty("type").GetString());
        Assert.Equal("assistant", root.GetProperty("role").GetString());
        Assert.Equal("tool_use", root.GetProperty("stop_reason").GetString());
        
        var content = root.GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(2, content.Length);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("tool_use", content[1].GetProperty("type").GetString());
        Assert.Equal("python_mcp-list_directory", content[1].GetProperty("name").GetString());
    }

    /// <summary>
    /// Test multiple tool use in a single response - validates WI-MM003: Tool Use Response Support
    /// </summary>
    [Fact]
    public async Task MockHttpHandlerBuilder_MultipleToolUse_GeneratesValidResponse()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithMultipleToolUse(
                ("python_mcp-list_directory", new { relative_path = "." }),
                ("python_mcp-get_directory_tree", new { max_depth = 2 })
            )
            .Build();

        var httpClient = new HttpClient(handler);

        // Act
        var response = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        // Assert
        Assert.True(response.IsSuccessStatusCode);
        
        var jsonResponse = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(jsonResponse);
        var root = document.RootElement;
        
        var content = root.GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(3, content.Length); // 1 text + 2 tool_use
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Contains("2 tool(s)", content[0].GetProperty("text").GetString());
        
        Assert.Equal("tool_use", content[1].GetProperty("type").GetString());
        Assert.Equal("python_mcp-list_directory", content[1].GetProperty("name").GetString());
        
        Assert.Equal("tool_use", content[2].GetProperty("type").GetString());
        Assert.Equal("python_mcp-get_directory_tree", content[2].GetProperty("name").GetString());
    }

    /// <summary>
    /// Test conditional tool responses - validates WI-MM003: Tool Use Response Support
    /// </summary>
    [Fact]
    public async Task MockHttpHandlerBuilder_ConditionalToolResponse_WorksCorrectly()
    {
        // Arrange - setup a scenario where first request gets tool use, second gets final response
        var handler = MockHttpHandlerBuilder.Create()
            .WhenFirstToolRequest("list_directory", new { relative_path = "." })
            .WhenToolResults("Based on the directory listing, I found 5 files.")
            .Build();

        var httpClient = new HttpClient(handler);

        // Act 1 - First request (should get tool use)
        var firstResponse = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(@"{""messages"":[{""role"":""user"",""content"":""List files using list_directory""}]}", 
                Encoding.UTF8, "application/json"));

        // Act 2 - Second request with tool results (should get final response)
        var secondResponse = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(@"{""messages"":[{""role"":""user"",""content"":""List files""},{""role"":""assistant"",""content"":[{""type"":""tool_use"",""id"":""tool_123"",""name"":""list_directory"",""input"":{}}]},{""role"":""user"",""content"":[{""type"":""tool_result"",""tool_use_id"":""tool_123"",""content"":""file1.txt\nfile2.txt""}]}]}", 
                Encoding.UTF8, "application/json"));

        // Assert
        Assert.True(firstResponse.IsSuccessStatusCode);
        Assert.True(secondResponse.IsSuccessStatusCode);

        var firstJson = await firstResponse.Content.ReadAsStringAsync();
        var secondJson = await secondResponse.Content.ReadAsStringAsync();

        // First response should be tool use
        Assert.Contains("\"type\":\"tool_use\"", firstJson);
        Assert.Contains("\"name\":\"list_directory\"", firstJson);

        // Second response should be final text
        Assert.Contains("Based on the directory listing, I found 5 files", secondJson);
        Assert.DoesNotContain("\"type\":\"tool_use\"", secondJson);
    }

    /// <summary>
    /// Test Python MCP tool pattern - validates WI-MM003: Tool Use Response Support
    /// </summary>
    [Fact]
    public async Task MockHttpHandlerBuilder_PythonMcpTool_GeneratesCorrectFormat()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithPythonMcpTool("list_directory", new { relative_path = "code" })
            .Build();

        var httpClient = new HttpClient(handler);

        // Act
        var response = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        // Assert
        Assert.True(response.IsSuccessStatusCode);
        
        var jsonResponse = await response.Content.ReadAsStringAsync();
        
        Assert.Contains("\"name\":\"python_mcp-list_directory\"", jsonResponse);
        Assert.Contains("\"relative_path\":\"code\"", jsonResponse);
        
        // Parse the JSON and check the actual text content (handles JSON escaping properly)
        using var document = JsonDocument.Parse(jsonResponse);
        var root = document.RootElement;
        var content = root.GetProperty("content").EnumerateArray().ToArray();
        var textContent = content[0].GetProperty("text").GetString();
        
        // Check the parsed text content instead of the raw JSON string
        Assert.Equal("I'll help you by using the list_directory function.", textContent);
        
        var toolUse = content[1];
        Assert.Equal("python_mcp-list_directory", toolUse.GetProperty("name").GetString());
        var toolId = toolUse.GetProperty("id").GetString();
        Assert.NotNull(toolId);
        Assert.StartsWith("toolu_", toolId);
    }

    /// <summary>
    /// Test enhanced conditional logic with multiple conditions - validates WI-MM005: Conditional Response Logic
    /// </summary>
    [Fact]
    public async Task MockHttpHandlerBuilder_MultiConditionalResponse_WorksCorrectly()
    {
        // Arrange - setup multiple conditions with different responses
        var handler = MockHttpHandlerBuilder.Create()
            .WithConditions()
            .When((req, idx) => req.IsFirstMessage(idx), @"{""response"":""first""}")
            .When((req, idx) => req.IsSecondMessage(idx), @"{""response"":""second""}")
            .When((req, idx) => req.HasToolResults(), @"{""response"":""tool_result""}")
            .Otherwise(@"{""response"":""default""}")
            .Build();

        var httpClient = new HttpClient(handler);

        // Act 1 - First request
        var firstResponse = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(@"{""messages"":[{""role"":""user"",""content"":""Hello""}]}", 
                Encoding.UTF8, "application/json"));

        // Act 2 - Second request  
        var secondResponse = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(@"{""messages"":[{""role"":""user"",""content"":""World""}]}", 
                Encoding.UTF8, "application/json"));

        // Act 3 - Request with tool results
        var toolResultResponse = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(@"{""messages"":[{""role"":""user"",""content"":[{""type"":""tool_result"",""tool_use_id"":""123"",""content"":""result""}]}]}", 
                Encoding.UTF8, "application/json"));

        // Act 4 - Request that doesn't match any condition
        var defaultResponse = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(@"{""messages"":[{""role"":""assistant"",""content"":""No match""}]}", 
                Encoding.UTF8, "application/json"));

        // Assert
        Assert.True(firstResponse.IsSuccessStatusCode);
        Assert.True(secondResponse.IsSuccessStatusCode);
        Assert.True(toolResultResponse.IsSuccessStatusCode);
        Assert.True(defaultResponse.IsSuccessStatusCode);

        var firstJson = await firstResponse.Content.ReadAsStringAsync();
        var secondJson = await secondResponse.Content.ReadAsStringAsync();
        var toolResultJson = await toolResultResponse.Content.ReadAsStringAsync();
        var defaultJson = await defaultResponse.Content.ReadAsStringAsync();

        Assert.Contains(@"""response"":""first""", firstJson);
        Assert.Contains(@"""response"":""second""", secondJson);
        Assert.Contains(@"""response"":""tool_result""", toolResultJson);
        Assert.Contains(@"""response"":""default""", defaultJson);
    }

    /// <summary>
    /// Test predefined condition helpers - validates WI-MM005: Predefined Condition Helpers
    /// </summary>
    [Fact]
    public async Task MockHttpHandlerBuilder_PredefinedConditions_WorkCorrectly()
    {
        // Arrange - test various predefined conditions
        var handler = MockHttpHandlerBuilder.Create()
            .CaptureRequests(out var capture)
            .WhenHasToolResults()
            .ThenRespondWith(@"{""type"":""tool_response""}")
            .WhenContainsText("weather")
            .ThenRespondWith(@"{""type"":""weather_response""}")
            .WhenMessageCount(2)
            .ThenRespondWith(@"{""type"":""two_messages""}")
            .WhenHasRole("assistant")
            .ThenRespondWith(@"{""type"":""assistant_role""}")
            .RespondWithAnthropicMessage("Default response")
            .Build();

        var httpClient = new HttpClient(handler);

        // Act 1 - Request with tool results
        var toolResponse = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(@"{""messages"":[{""role"":""user"",""content"":[{""type"":""tool_result"",""tool_use_id"":""123"",""content"":""data""}]}]}", 
                Encoding.UTF8, "application/json"));

        // Act 2 - Request mentioning weather  
        var weatherResponse = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(@"{""messages"":[{""role"":""user"",""content"":""What's the weather like?""}]}", 
                Encoding.UTF8, "application/json"));

        // Act 3 - Request with 2 messages
        var twoMessageResponse = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(@"{""messages"":[{""role"":""user"",""content"":""Hello""},{""role"":""assistant"",""content"":""Hi""}]}", 
                Encoding.UTF8, "application/json"));

        // Act 4 - Request with assistant role
        var assistantResponse = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(@"{""messages"":[{""role"":""assistant"",""content"":""I am assistant""}]}", 
                Encoding.UTF8, "application/json"));

        // Assert
        var toolJson = await toolResponse.Content.ReadAsStringAsync();
        var weatherJson = await weatherResponse.Content.ReadAsStringAsync();
        var twoMessageJson = await twoMessageResponse.Content.ReadAsStringAsync();
        var assistantJson = await assistantResponse.Content.ReadAsStringAsync();

        Assert.Contains(@"""type"":""tool_response""", toolJson);
        Assert.Contains(@"""type"":""weather_response""", weatherJson);
        Assert.Contains(@"""type"":""two_messages""", twoMessageJson);
        Assert.Contains(@"""type"":""assistant_role""", assistantJson);
    }

    /// <summary>
    /// Test conversation state management - validates WI-MM005: State Management
    /// </summary>
    [Fact]
    public async Task MockHttpHandlerBuilder_ConversationState_TracksCorrectly()
    {
        // Arrange - setup stateful conversation tracking
        var state = new ConversationState();
        var handler = MockHttpHandlerBuilder.Create()
            .WithState(state)
            .WhenStateful((req, idx, s) => s.RequestCount == 1, @"{""response"":""first_stateful""}")
            .WhenStateful((req, idx, s) => s.RequestCount == 2, @"{""response"":""second_stateful""}")
            .WhenStateful((req, idx, s) => s.RequestCount >= 3, @"{""response"":""subsequent_stateful""}")
            .Build();

        var httpClient = new HttpClient(handler);

        // Act - make multiple requests
        var responses = new List<HttpResponseMessage>();
        for (int i = 0; i < 4; i++)
        {
            var response = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
                new StringContent(@"{""messages"":[{""role"":""user"",""content"":""Hello""}]}", 
                    Encoding.UTF8, "application/json"));
            responses.Add(response);
        }

        // Assert
        var firstJson = await responses[0].Content.ReadAsStringAsync();
        var secondJson = await responses[1].Content.ReadAsStringAsync();
        var thirdJson = await responses[2].Content.ReadAsStringAsync();
        var fourthJson = await responses[3].Content.ReadAsStringAsync();

        Assert.Contains(@"""response"":""first_stateful""", firstJson);
        Assert.Contains(@"""response"":""second_stateful""", secondJson);
        Assert.Contains(@"""response"":""subsequent_stateful""", thirdJson);
        Assert.Contains(@"""response"":""subsequent_stateful""", fourthJson);

        // Verify state tracking
        Assert.Equal(4, state.RequestCount);
    }

    /// <summary>
    /// Test request extension methods - validates WI-MM005: Request Matching Logic
    /// </summary>
    [Fact]
    public async Task MockHttpHandlerBuilder_RequestExtensions_WorkCorrectly()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .CaptureRequests(out var capture)
            .WithConditions()
            .When((req, idx) => req.MentionsTool("calculator"), @"{""tool"":""calculator""}")
            .When((req, idx) => req.IsAnthropicRequest(), @"{""provider"":""anthropic""}")
            .Otherwise(@"{""default"":""response""}")
            .Build();

        var httpClient = new HttpClient(handler);

        // Act 1 - Anthropic request
        var anthropicResponse = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(@"{""model"":""claude-3""}", Encoding.UTF8, "application/json"));

        // Act 2 - Request mentioning calculator tool
        var calculatorResponse = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(@"{""messages"":[{""role"":""user"",""content"":""Use calculator tool""}]}", 
                Encoding.UTF8, "application/json"));

        // Assert
        var anthropicJson = await anthropicResponse.Content.ReadAsStringAsync();
        var calculatorJson = await calculatorResponse.Content.ReadAsStringAsync();

        Assert.Contains(@"""provider"":""anthropic""", anthropicJson);
        Assert.Contains(@"""tool"":""calculator""", calculatorJson);
    }

    /// <summary>
    /// Test complex multi-request conversation flow - validates WI-MM005: Multi-Request Conversation Support
    /// </summary>
    [Fact]
    public async Task MockHttpHandlerBuilder_ComplexConversationFlow_WorksCorrectly()
    {
        // Arrange - setup complex conversation with tool use → tool result → final response
        var handler = MockHttpHandlerBuilder.Create()
            .CaptureRequests(out var capture)
            .WithConditions()
            .When((req, idx) => req.HasToolResults(), @"{""type"":""message"",""role"":""assistant"",""content"":[{""type"":""text"",""text"":""Based on the weather data, it's sunny in San Francisco!""}]}")
            .When((req, idx) => req.IsFirstMessage(idx), @"{""type"":""message"",""role"":""assistant"",""content"":[{""type"":""tool_use"",""id"":""tool_1"",""name"":""get_weather"",""input"":{""location"":""San Francisco""}}]}")
            .Otherwise(@"{""type"":""message"",""role"":""assistant"",""content"":[{""type"":""text"",""text"":""Default response""}]}")
            .Build();

        var httpClient = new HttpClient(handler);

        // Act 1 - Initial user request (should get tool use)
        var initialResponse = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(@"{""messages"":[{""role"":""user"",""content"":""What's the weather in San Francisco?""}]}", 
                Encoding.UTF8, "application/json"));

        // Act 2 - Follow-up with tool results (should get final response)
        var followupResponse = await httpClient.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(@"{""messages"":[{""role"":""user"",""content"":""Weather""},{""role"":""assistant"",""content"":[{""type"":""tool_use"",""id"":""tool_1"",""name"":""get_weather"",""input"":{""location"":""San Francisco""}}]},{""role"":""user"",""content"":[{""type"":""tool_result"",""tool_use_id"":""tool_1"",""content"":""Sunny, 72°F""}]}]}", 
                Encoding.UTF8, "application/json"));

        // Assert
        Assert.True(initialResponse.IsSuccessStatusCode);
        Assert.True(followupResponse.IsSuccessStatusCode);

        var initialJson = await initialResponse.Content.ReadAsStringAsync();
        var followupJson = await followupResponse.Content.ReadAsStringAsync();

        // First response should be tool use
        Assert.Contains(@"""type"":""tool_use""", initialJson);
        Assert.Contains(@"""name"":""get_weather""", initialJson);
        Assert.Contains(@"""location"":""San Francisco""", initialJson);

        // Second response should be final text
        Assert.Contains("sunny in San Francisco", followupJson);
        Assert.DoesNotContain(@"""type"":""tool_use""", followupJson);
    }
} 