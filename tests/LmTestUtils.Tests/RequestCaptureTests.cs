using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace LmTestUtils.Tests;

public class RequestCaptureTests
{
    [Fact]
    public async Task RequestCapture_GetRequestAs_WorksWithOpenAIChatCompletionRequest()
    {
        // Arrange - Create a realistic OpenAI ChatCompletionRequest
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithOpenAIMessage("Test response")
            .CaptureRequests(out var requestCapture)
            .Build();

        var httpClient = new HttpClient(handler);

        // Create a ChatCompletionRequest with Union types (the problematic part)
        var requestData = new
        {
            model = "gpt-4",
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You are a helpful assistant."
                },
                new
                {
                    role = "user",
                    content = "Hello, world!"
                }
            },
            temperature = 0.7,
            max_tokens = 1000,
            tools = new object[]
            {
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "get_weather",
                        description = "Get current weather",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                location = new { type = "string" }
                            }
                        }
                    }
                }
            }
        };

        var jsonContent = JsonSerializer.Serialize(requestData);
        var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

        // Act - Make HTTP request to trigger capture
        var response = await httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);

        // Assert - Test that GetRequestAs<ChatCompletionRequest>() works
        Assert.Equal(1, requestCapture.RequestCount);

        // Debug: Print the captured JSON to understand the format
        var capturedJson = requestCapture.LastRequestBody;
        System.Diagnostics.Debug.WriteLine($"Captured JSON: {capturedJson}");

        // This is the critical test - can we deserialize the captured request back to ChatCompletionRequest?
        var chatRequest = requestCapture.GetRequestAs<ChatCompletionRequest>();

        Assert.NotNull(chatRequest);
        Assert.Equal("gpt-4", chatRequest.Model);
        Assert.Equal(0.7, chatRequest.Temperature);
        Assert.Equal(1000, chatRequest.MaxTokens);
        Assert.Equal(2, chatRequest.Messages.Count);

        // Test that Union types are handled correctly
        var systemMessage = chatRequest.Messages[0];
        Assert.Equal(RoleEnum.System, systemMessage.Role);
        Assert.NotNull(systemMessage.Content);

        var userMessage = chatRequest.Messages[1];
        Assert.Equal(RoleEnum.User, userMessage.Role);
        Assert.NotNull(userMessage.Content);

        // Test tools deserialization
        Assert.NotNull(chatRequest.Tools);
        Assert.Single(chatRequest.Tools);
        Assert.Equal("get_weather", chatRequest.Tools[0].Function.Name);
    }

    [Fact]
    public async Task ToolCapture_ShouldProvideStructuredAccessToToolData()
    {
        // Arrange - Use MockHttpHandlerBuilder to create proper request capture
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithAnthropicMessage("Test response")
            .CaptureRequests(out var requestCapture)
            .Build();

        var httpClient = new HttpClient(handler);

        // Simulate an Anthropic request with tools
        var requestData = new
        {
            model = "claude-3-7-sonnet-20250219",
            max_tokens = 1024,
            tools = new object[]
            {
                new
                {
                    name = "getWeather",
                    description = "Get current weather for a location",
                    input_schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            location = new { type = "string", description = "City name" },
                                                         units = new { type = "string", @enum = new string[] { "celsius", "fahrenheit" } }
                        },
                                                 required = new string[] { "location" }
                    }
                },
                new
                {
                    name = "python_mcp-execute_code",
                    description = "Execute Python code",
                    input_schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            code = new { type = "string", description = "Python code to execute" }
                        },
                                                 required = new string[] { "code" }
                    }
                }
            },
            messages = new object[] { new { role = "user", content = "What's the weather like?" } }
        };

        var jsonContent = JsonSerializer.Serialize(requestData);
        var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

        // Act - Make HTTP request to trigger capture
        var response = await httpClient.PostAsync("https://api.anthropic.com/v1/messages", content);

        // Assert - Test structured tool access  
        Assert.Equal(1, requestCapture.RequestCount);
        var anthropicRequest = requestCapture.GetAnthropicRequest()!;
        var tools = anthropicRequest.Tools.ToList();

        Assert.Equal(2, tools.Count);

        // Test first tool (getWeather)
        var weatherTool = tools[0];
        Assert.Equal("getWeather", weatherTool.Name);
        Assert.Equal("Get current weather for a location", weatherTool.Description);
        Assert.NotNull(weatherTool.InputSchema);

        // Test tool property introspection
        Assert.True(weatherTool.HasInputProperty("location"));
        Assert.True(weatherTool.HasInputProperty("units"));
        Assert.False(weatherTool.HasInputProperty("nonexistent"));

        // Test property type checking
        Assert.Equal("string", weatherTool.GetInputPropertyType("location"));
        Assert.Equal("string", weatherTool.GetInputPropertyType("units"));
        Assert.Null(weatherTool.GetInputPropertyType("nonexistent"));

        // Test second tool (python_mcp-execute_code)
        var pythonTool = tools[1];
        Assert.Equal("python_mcp-execute_code", pythonTool.Name);
        Assert.Equal("Execute Python code", pythonTool.Description);
        Assert.True(pythonTool.HasInputProperty("code"));
        Assert.Equal("string", pythonTool.GetInputPropertyType("code"));
    }

    [Fact]
    public async Task RequestCapture_StructuredAssertions_AreSuperiorToStringBased()
    {
        // This test demonstrates why structured assertions are better than string-based ones

        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithAnthropicMessage("Test response")
            .CaptureRequests(out var requestCapture)
            .Build();

        var httpClient = new HttpClient(handler);

        var requestData = new
        {
            model = "claude-3-7-sonnet-20250219",
            tools = new object[]
            {
                new
                {
                    name = "calculator_add",
                    description = "Add two numbers",
                    input_schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            a = new { type = "number" },
                            b = new { type = "number" }
                        }
                    }
                }
            },
            messages = new object[] { new { role = "user", content = "Calculate 2+3" } }
        };

        var jsonContent = JsonSerializer.Serialize(requestData);
        var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("https://api.anthropic.com/v1/messages", content);

        var anthropicRequest = requestCapture.GetAnthropicRequest()!;

        // ❌ BAD: String-based assertions (fragile, imprecise)
        // Assert.Contains("calculator_add", jsonContent);  // Could match in descriptions or other places
        // Assert.Contains("\"type\": \"number\"", jsonContent);  // Could match unrelated fields

        // ✅ GOOD: Structured assertions (precise, robust)
        var tools = anthropicRequest.Tools.ToList();
        Assert.Single(tools);

        var calcTool = tools[0];
        Assert.Equal("calculator_add", calcTool.Name);  // Exact name match
        Assert.Equal("Add two numbers", calcTool.Description);  // Exact description

        // Precise type checking for specific properties
        Assert.Equal("number", calcTool.GetInputPropertyType("a"));
        Assert.Equal("number", calcTool.GetInputPropertyType("b"));

        // Benefits of structured approach:
        // 1. Type safety - we get actual types, not string matches
        // 2. Precise targeting - we test exactly what we want
        // 3. Robust - immune to JSON formatting changes
        // 4. Readable - intent is clear from the assertions
        // 5. IntelliSense support - autocomplete for properties
    }
}