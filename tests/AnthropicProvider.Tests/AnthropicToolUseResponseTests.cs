using System.Text.Json;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.AnthropicProvider.Utils;
using Xunit;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests;

public class AnthropicToolUseResponseTests
{
    [Fact]
    public void Deserialize_ToolUseResponse_ShouldPopulateCorrectly()
    {
        // Arrange
        string json = @"{
            ""id"": ""msg_014fBvULMGnEoN6yXutiqiQx"",
            ""type"": ""message"",
            ""role"": ""assistant"",
            ""model"": ""claude-3-7-sonnet-20250219"",
            ""content"": [
                {
                    ""type"": ""text"",
                    ""text"": ""I'll help you list the files in the root and \""code\"" directories. Let me use the appropriate tool to do that.""
                },
                {
                    ""type"": ""tool_use"",
                    ""id"": ""toolu_01LhLY6M7AhrzHAjo9FBzXH6"",
                    ""name"": ""python_mcp-list_directory"",
                    ""input"": {
                        ""relative_path"": "".""
                    }
                }
            ],
            ""stop_reason"": ""tool_use"",
            ""stop_sequence"": null,
            ""usage"": {
                ""input_tokens"": 533,
                ""cache_creation_input_tokens"": 0,
                ""cache_read_input_tokens"": 0,
                ""output_tokens"": 86
            }
        }";

        // Act
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        var response = JsonSerializer.Deserialize<AnthropicResponse>(json, options);

        // Assert
        Assert.NotNull(response);
        Assert.Equal("msg_014fBvULMGnEoN6yXutiqiQx", response.Id);
        Assert.Equal("message", response.Type);
        Assert.Equal("assistant", response.Role);
        Assert.Equal("claude-3-7-sonnet-20250219", response.Model);
        Assert.Equal("tool_use", response.StopReason);
        Assert.Null(response.StopSequence);
        
        // Validate content
        Assert.NotNull(response.Content);
        Assert.Equal(2, response.Content.Count);
        
        // Validate text content
        var textContent = response.Content[0];
        Assert.Equal("text", textContent.Type);
        Assert.IsType<AnthropicResponseTextContent>(textContent);
        var typedTextContent = (AnthropicResponseTextContent)textContent;
        Assert.Equal("I'll help you list the files in the root and \"code\" directories. Let me use the appropriate tool to do that.", typedTextContent.Text);
        
        // Validate tool_use content
        var toolContent = response.Content[1];
        Assert.Equal("tool_use", toolContent.Type);
        Assert.IsType<AnthropicResponseToolUseContent>(toolContent);
        var typedToolContent = (AnthropicResponseToolUseContent)toolContent;
        Assert.Equal("toolu_01LhLY6M7AhrzHAjo9FBzXH6", typedToolContent.Id);
        Assert.Equal("python_mcp-list_directory", typedToolContent.Name);
        Assert.NotNull(typedToolContent.Input);
        
        // Verify the relative_path in input
        var relativePath = typedToolContent.Input.GetProperty("relative_path").GetString();
        Assert.Equal(".", relativePath);
        
        // Check for an extension method to handle the tool_use type
        Assert.True(response.ContainsToolCall());
        
        // Validate usage
        Assert.NotNull(response.Usage);
        Assert.Equal(533, response.Usage.InputTokens);
        Assert.Equal(86, response.Usage.OutputTokens);
    }
} 