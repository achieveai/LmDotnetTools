using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Xunit;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

public class AnthropicResponse_ToMessages_Tests
{
    // Gets the path to the repository root directory
    private static string GetRepositoryRootPath()
    {
        // Start from the current assembly's location
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        var currentDir = Path.GetDirectoryName(assemblyLocation);
        
        // Go up to find the repository root (where you'd typically find .git, etc.)
        // This will work even if the test is run from different working directories
        while (currentDir != null && !Directory.Exists(Path.Combine(currentDir, ".git")) && 
               !File.Exists(Path.Combine(currentDir, "LmDotnetTools.sln")))
        {
            currentDir = Directory.GetParent(currentDir)?.FullName;
        }
        
        return currentDir ?? throw new InvalidOperationException("Could not find repository root");
    }
    
    private static string GetExampleFilePath(string filename)
    {
        return Path.Combine(GetRepositoryRootPath(), "src", "AnthropicProvider", "Examples", filename);
    }

    [Fact]
    public void NonStreaming_ExampleResponse_ShouldConvertToCorrectMessages()
    {
        // Arrange
        string exampleJson = File.ReadAllText(GetExampleFilePath("example_responses.json"));
        var responses = JsonSerializer.Deserialize<AnthropicResponse[]>(exampleJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Failed to deserialize example responses");

        // Act & Assert for first response - text and tool_use
        var response1 = responses[0];
        var messages1 = response1.ToMessages();

        // Assert basic properties
        Assert.Equal(2, messages1.Count);
        Assert.Equal("msg_01E", response1.Id);
        Assert.Equal(Role.Assistant, messages1[0].Role);
        Assert.Equal("msg_01E", messages1[0].FromAgent);
        
        // Verify text message content
        Assert.IsType<TextMessage>(messages1[0]);
        var textMessage = messages1[0] as TextMessage;
        Assert.NotNull(textMessage);
        Assert.Contains("I'll help you list the files in the root and \"code\" directories", textMessage.Text);
        Assert.False(textMessage.IsThinking);
        
        // Verify tool message content
        Assert.IsType<ToolsCallMessage>(messages1[1]);
        var toolMessage = messages1[1] as ToolsCallMessage;
        Assert.NotNull(toolMessage);
        var toolCalls = toolMessage.GetToolCalls();
        Assert.NotNull(toolCalls);
        var toolCall = toolCalls.First();
        Assert.Equal("python_mcp-list_directory", toolCall.FunctionName);
        Assert.Equal("toolu_018", toolCall.ToolCallId);
        
        // Act & Assert for second response - thinking content
        var response2 = responses[1];
        var messages2 = response2.ToMessages();
        
        // Assert basic properties
        Assert.Equal(3, messages2.Count);
        Assert.Equal("msg_016", response2.Id);
        
        // Verify thinking message content
        Assert.IsType<TextMessage>(messages2[0]);
        var thinkingMessage = messages2[0] as TextMessage;
        Assert.NotNull(thinkingMessage);
        Assert.Contains("The user wants to find files that are in the directory", thinkingMessage.Text);
        Assert.True(thinkingMessage.IsThinking);
        
        // Verify regular text message
        Assert.IsType<TextMessage>(messages2[1]);
        var regularTextMessage = messages2[1] as TextMessage;
        Assert.NotNull(regularTextMessage);
        Assert.Contains("I'll help you find the files that are in", regularTextMessage.Text);
        Assert.False(regularTextMessage.IsThinking);
        
        // Verify tool message
        Assert.IsType<ToolsCallMessage>(messages2[2]);
        var toolMessage2 = messages2[2] as ToolsCallMessage;
        Assert.NotNull(toolMessage2);
        var toolCalls2 = toolMessage2.GetToolCalls();
        Assert.NotNull(toolCalls2);
        var toolCall2 = toolCalls2.First();
        Assert.Equal("python_mcp-execute_python_in_container", toolCall2.FunctionName);
        Assert.Contains("import os", toolCall2.FunctionArgs);
    }
    
    [Fact]
    public void Streaming_ExampleResponse_ShouldConvertToCorrectUpdateMessages()
    {
        // Arrange
        string exampleSse = File.ReadAllText(GetExampleFilePath("example_streaming_responses.txt"));
        var sseEvents = ParseSseEvents(exampleSse);
        
        // Convert SSE events to JSON nodes and text delta objects
        var textDeltas = new List<TextUpdateMessage>();
        var toolUses = new List<string>();
        
        foreach (var sseEvent in sseEvents)
        {
            if (string.IsNullOrEmpty(sseEvent.Data)) continue;
            
            try
            {
                // Parse as JSON object
                var jsonNode = JsonNode.Parse(sseEvent.Data);
                var eventType = jsonNode?["type"]?.GetValue<string>();
                
                // Check for content_block_delta events with text_delta
                if (eventType == "content_block_delta")
                {
                    var delta = jsonNode?["delta"];
                    var deltaType = delta?["type"]?.GetValue<string>();
                    
                    if (deltaType == "text_delta" && delta?["text"] != null)
                    {
                        // Create a text update message
                        var text = delta["text"]!.GetValue<string>();
                        textDeltas.Add(new TextUpdateMessage
                        {
                            Text = text,
                            Role = Role.Assistant,
                            IsThinking = false
                        });
                    }
                }
                
                // Check for tool_use content blocks
                if (eventType == "content_block_start" && 
                    jsonNode?["content_block"]?["type"]?.GetValue<string>() == "tool_use")
                {
                    var toolId = jsonNode["content_block"]?["id"]?.GetValue<string>();
                    var toolName = jsonNode["content_block"]?["name"]?.GetValue<string>();
                    if (toolId != null && toolName != null)
                    {
                        toolUses.Add(toolName);
                    }
                }
                
                // Check for message_delta events with usage information
                if (eventType == "message_delta" && 
                    jsonNode?["delta"]?["stop_reason"] != null && 
                    jsonNode?["usage"] != null)
                {
                    // This would handle usage information if needed
                }
            }
            catch (JsonException)
            {
                // Skip invalid JSON
            }
        }
        
        // Act - Get combined text content
        var combinedText = string.Join("", textDeltas.Select(m => m.Text));
        
        // Assert
        Assert.NotEmpty(textDeltas);
        Assert.Contains("help you list", combinedText);
        Assert.Contains("the files in the root", combinedText);
        
        // Check for tool use
        Assert.Contains("python_mcp-list_directory", toolUses);
        
        // Check for message_delta events with stop_reason
        var messageDeltas = sseEvents
            .Where(e => e.Event == "message_delta")
            .Select(e => JsonNode.Parse(e.Data))
            .Where(j => j?["delta"]?["stop_reason"] != null)
            .ToList();
        
        Assert.NotEmpty(messageDeltas);
        Assert.Equal("tool_use", messageDeltas[0]?["delta"]?["stop_reason"]?.GetValue<string>());
    }
    
    // Helper method to parse SSE events
    private static List<SseEvent> ParseSseEvents(string input)
    {
        var events = new List<SseEvent>();
        var lines = input.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        
        SseEvent? currentEvent = null;
        
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                // Empty line indicates end of an event
                if (currentEvent != null)
                {
                    events.Add(currentEvent);
                    currentEvent = null;
                }
                continue;
            }
            
            if (currentEvent == null)
            {
                currentEvent = new SseEvent();
            }
            
            if (line.StartsWith("event:"))
            {
                currentEvent.Event = line.Substring(6).Trim();
            }
            else if (line.StartsWith("data:"))
            {
                currentEvent.Data = line.Substring(5).Trim();
            }
        }
        
        // Add the last event if there is one
        if (currentEvent != null)
        {
            events.Add(currentEvent);
        }
        
        return events;
    }
    
    // Simple class to represent an SSE event
    private class SseEvent
    {
        public string Event { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
    }
} 