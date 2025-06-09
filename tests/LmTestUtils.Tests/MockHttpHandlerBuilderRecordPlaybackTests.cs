using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils;
using Xunit;

namespace LmTestUtils.Tests;

public class MockHttpHandlerBuilderRecordPlaybackTests
{
    [Fact]
    public async Task WithRecordPlayback_ExistingFile_ShouldPlaybackRecordedInteraction()
    {
        // Arrange - Create a test data file
        var testFilePath = Path.GetTempFileName();
        var testData = new RecordPlaybackData
        {
            Interactions = new List<RecordedInteraction>
            {
                new RecordedInteraction
                {
                    SerializedRequest = JsonDocument.Parse("""
                        {
                            "model": "gpt-4",
                            "messages": [
                                {"role": "user", "content": "Hello"}
                            ]
                        }
                        """).RootElement.Clone(),
                    SerializedResponse = JsonDocument.Parse("""
                        {
                            "id": "test-response",
                            "choices": [
                                {"message": {"role": "assistant", "content": "Hello! How can I help?"}}
                            ]
                        }
                        """).RootElement.Clone(),
                    IsStreaming = false,
                    Provider = "OpenAI"
                }
            }
        };
        
        await File.WriteAllTextAsync(testFilePath, JsonSerializer.Serialize(testData, new JsonSerializerOptions { WriteIndented = true }));
        
        try
        {
            // Create handler with record/playback
            var handler = MockHttpHandlerBuilder.Create()
                .WithRecordPlayback(testFilePath)
                .Build();
            
            using var httpClient = new HttpClient(handler);
            
            // Make a request that matches the recorded data
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = new StringContent("""
                    {
                        "model": "gpt-4",
                        "messages": [
                            {"role": "user", "content": "Hello"}
                        ]
                    }
                    """, Encoding.UTF8, "application/json")
            };
            
            // Act
            var response = await httpClient.SendAsync(request);
            
            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var responseContent = await response.Content.ReadAsStringAsync();
            var responseJson = JsonDocument.Parse(responseContent);
            
            Assert.Equal("test-response", responseJson.RootElement.GetProperty("id").GetString());
            Assert.Equal("Hello! How can I help?", 
                responseJson.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content").GetString());
        }
        finally
        {
            File.Delete(testFilePath);
        }
    }
    
    [Fact]
    public async Task WithRecordPlayback_NoMatchingRequest_ShouldThrowException()
    {
        // Arrange - Create a test data file with one interaction
        var testFilePath = Path.GetTempFileName();
        var testData = new RecordPlaybackData
        {
            Interactions = new List<RecordedInteraction>
            {
                new RecordedInteraction
                {
                    SerializedRequest = JsonDocument.Parse("""
                        {
                            "model": "gpt-4",
                            "messages": [
                                {"role": "user", "content": "Hello"}
                            ]
                        }
                        """).RootElement.Clone(),
                    SerializedResponse = JsonDocument.Parse("""
                        {
                            "id": "test-response"
                        }
                        """).RootElement.Clone()
                }
            }
        };
        
        await File.WriteAllTextAsync(testFilePath, JsonSerializer.Serialize(testData, new JsonSerializerOptions { WriteIndented = true }));
        
        try
        {
            var handler = MockHttpHandlerBuilder.Create()
                .WithRecordPlayback(testFilePath)
                .Build();
            
            using var httpClient = new HttpClient(handler);
            
            // Make a request that doesn't match the recorded data
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = new StringContent("""
                    {
                        "model": "gpt-3.5-turbo",
                        "messages": [
                            {"role": "user", "content": "Different message"}
                        ]
                    }
                    """, Encoding.UTF8, "application/json")
            };
            
            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => httpClient.SendAsync(request));
            Assert.Contains("No recorded interaction found", exception.Message);
        }
        finally
        {
            File.Delete(testFilePath);
        }
    }
    
    [Fact]
    public async Task WithRecordPlayback_FlexibleMatching_ShouldMatchBasedOnKeyProperties()
    {
        // Arrange - Create test data with basic request structure
        var testFilePath = Path.GetTempFileName();
        var testData = new RecordPlaybackData
        {
            Interactions = new List<RecordedInteraction>
            {
                new RecordedInteraction
                {
                    SerializedRequest = JsonDocument.Parse("""
                        {
                            "model": "claude-3-sonnet-20240229",
                            "messages": [
                                {"role": "user", "content": "Hello"},
                                {"role": "assistant", "content": "Hi there!"}
                            ]
                        }
                        """).RootElement.Clone(),
                    SerializedResponse = JsonDocument.Parse("""
                        {
                            "type": "message",
                            "id": "msg_flexible_match",
                            "content": [{"type": "text", "text": "Flexible match response"}]
                        }
                        """).RootElement.Clone()
                }
            }
        };
        
        await File.WriteAllTextAsync(testFilePath, JsonSerializer.Serialize(testData, new JsonSerializerOptions { WriteIndented = true }));
        
        try
        {
            var handler = MockHttpHandlerBuilder.Create()
                .WithRecordPlayback(testFilePath)
                .Build();
            
            using var httpClient = new HttpClient(handler);
            
            // Make a request with same model and messages but different formatting/extra properties
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent("""
                    {
                        "model": "claude-3-sonnet-20240229",
                        "max_tokens": 1000,
                        "temperature": 0.7,
                        "messages": [
                            {"role": "user", "content": "Hello"},
                            {"role": "assistant", "content": "Hi there!"}
                        ]
                    }
                    """, Encoding.UTF8, "application/json")
            };
            
            // Act
            var response = await httpClient.SendAsync(request);
            
            // Assert - Should match despite extra properties
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var responseContent = await response.Content.ReadAsStringAsync();
            var responseJson = JsonDocument.Parse(responseContent);
            
            Assert.Equal("msg_flexible_match", responseJson.RootElement.GetProperty("id").GetString());
        }
        finally
        {
            File.Delete(testFilePath);
        }
    }
    
    [Fact]
    public async Task WithRecordPlayback_NonExistentFile_ShouldCreateEmptyDataSet()
    {
        // Arrange - Use a non-existent file path
        var testFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".json");
        
        try
        {
            var handler = MockHttpHandlerBuilder.Create()
                .WithRecordPlayback(testFilePath)
                .Build();
            
            using var httpClient = new HttpClient(handler);
            
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = new StringContent("""{"model": "gpt-4"}""", Encoding.UTF8, "application/json")
            };
            
            // Act & Assert - Should throw since no recorded interactions and no API forwarding
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => httpClient.SendAsync(request));
            Assert.Contains("No recorded interaction found", exception.Message);
        }
        finally
        {
            if (File.Exists(testFilePath))
                File.Delete(testFilePath);
        }
    }
    
    [Fact]
    public void RequestMatcher_ExactMatch_ShouldMatchIdenticalRequests()
    {
        // Arrange
        var requestJson = """
            {
                "model": "gpt-4",
                "messages": [
                    {"role": "user", "content": "Test message"}
                ]
            }
            """;
        
        var incomingRequest = JsonDocument.Parse(requestJson).RootElement;
        var recordedRequest = JsonDocument.Parse(requestJson).RootElement;
        
        // Act
        var matches = RequestMatcher.MatchesRecordedRequest(incomingRequest, recordedRequest, exactMatch: true);
        
        // Assert
        Assert.True(matches);
    }
    
    [Fact]
    public void RequestMatcher_FlexibleMatch_ShouldMatchKeyProperties()
    {
        // Arrange
        var incomingRequestJson = """
            {
                "model": "gpt-4",
                "temperature": 0.7,
                "max_tokens": 1000,
                "messages": [
                    {"role": "user", "content": "Test message"}
                ]
            }
            """;
        
        var recordedRequestJson = """
            {
                "model": "gpt-4",
                "messages": [
                    {"role": "user", "content": "Test message"}
                ]
            }
            """;
        
        var incomingRequest = JsonDocument.Parse(incomingRequestJson).RootElement;
        var recordedRequest = JsonDocument.Parse(recordedRequestJson).RootElement;
        
        // Act
        var matches = RequestMatcher.MatchesRecordedRequest(incomingRequest, recordedRequest, exactMatch: false);
        
        // Assert
        Assert.True(matches);
    }
    
    [Fact]
    public void RequestMatcher_DifferentModel_ShouldNotMatch()
    {
        // Arrange
        var incomingRequestJson = """
            {
                "model": "gpt-3.5-turbo",
                "messages": [
                    {"role": "user", "content": "Test message"}
                ]
            }
            """;
        
        var recordedRequestJson = """
            {
                "model": "gpt-4",
                "messages": [
                    {"role": "user", "content": "Test message"}
                ]
            }
            """;
        
        var incomingRequest = JsonDocument.Parse(incomingRequestJson).RootElement;
        var recordedRequest = JsonDocument.Parse(recordedRequestJson).RootElement;
        
        // Act
        var matches = RequestMatcher.MatchesRecordedRequest(incomingRequest, recordedRequest, exactMatch: false);
        
        // Assert
        Assert.False(matches);
    }
    
    [Fact]
    public async Task WithRecordPlayback_WithRequestCapture_ShouldCaptureRequestsAndPlayback()
    {
        // Arrange
        var testFilePath = Path.GetTempFileName();
        var testData = new RecordPlaybackData
        {
            Interactions = new List<RecordedInteraction>
            {
                new RecordedInteraction
                {
                    SerializedRequest = JsonDocument.Parse("""
                        {
                            "model": "claude-3-sonnet-20240229",
                            "messages": [
                                {"role": "user", "content": "What's the weather?"}
                            ]
                        }
                        """).RootElement.Clone(),
                    SerializedResponse = JsonDocument.Parse("""
                        {
                            "type": "message",
                            "id": "msg_weather_response",
                            "content": [{"type": "text", "text": "I'll check the weather for you."}]
                        }
                        """).RootElement.Clone()
                }
            }
        };
        
        await File.WriteAllTextAsync(testFilePath, JsonSerializer.Serialize(testData, new JsonSerializerOptions { WriteIndented = true }));
        
        try
        {
            var handler = MockHttpHandlerBuilder.Create()
                .CaptureRequests(out var capture)
                .WithRecordPlayback(testFilePath)
                .Build();
            
            using var httpClient = new HttpClient(handler);
            
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent("""
                    {
                        "model": "claude-3-sonnet-20240229",
                        "messages": [
                            {"role": "user", "content": "What's the weather?"}
                        ]
                    }
                    """, Encoding.UTF8, "application/json")
            };
            
            // Act
            var response = await httpClient.SendAsync(request);
            
            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            
            // Debug: Check what was captured
            System.Diagnostics.Debug.WriteLine($"Request count: {capture.RequestCount}");
            System.Diagnostics.Debug.WriteLine($"Last request body: {capture.LastRequestBody}");
            
            // Verify request was captured
            var capturedRequest = capture.GetAnthropicRequest();
            Assert.NotNull(capturedRequest);
            Assert.Equal("claude-3-sonnet-20240229", capturedRequest.Model);
            Assert.Equal("What's the weather?", capturedRequest.Messages.First().Content);
            
            // Verify response was from playback
            var responseContent = await response.Content.ReadAsStringAsync();
            var responseJson = JsonDocument.Parse(responseContent);
            Assert.Equal("msg_weather_response", responseJson.RootElement.GetProperty("id").GetString());
        }
        finally
        {
            File.Delete(testFilePath);
        }
    }
    
    [Fact]
    public async Task WithRecordPlayback_AnthropicToolUse_ShouldMatchAndPlayback()
    {
        // Arrange - Create test data with tool usage
        var testFilePath = Path.GetTempFileName();
        var testData = new RecordPlaybackData
        {
            Interactions = new List<RecordedInteraction>
            {
                new RecordedInteraction
                {
                    SerializedRequest = JsonDocument.Parse("""
                        {
                            "model": "claude-3-sonnet-20240229",
                            "tools": [
                                {
                                    "type": "function",
                                    "function": {
                                        "name": "getWeather",
                                        "description": "Get weather info"
                                    }
                                }
                            ],
                            "messages": [
                                {"role": "user", "content": "What's the weather in SF?"}
                            ]
                        }
                        """).RootElement.Clone(),
                    SerializedResponse = JsonDocument.Parse("""
                        {
                            "type": "message",
                            "id": "msg_tool_response",
                            "content": [
                                {
                                    "type": "tool_use",
                                    "id": "toolu_weather123",
                                    "name": "getWeather",
                                    "input": {"location": "San Francisco"}
                                }
                            ]
                        }
                        """).RootElement.Clone()
                }
            }
        };
        
        await File.WriteAllTextAsync(testFilePath, JsonSerializer.Serialize(testData, new JsonSerializerOptions { WriteIndented = true }));
        
        try
        {
            var handler = MockHttpHandlerBuilder.Create()
                .WithRecordPlayback(testFilePath)
                .Build();
            
            using var httpClient = new HttpClient(handler);
            
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent("""
                    {
                        "model": "claude-3-sonnet-20240229",
                        "tools": [
                            {
                                "type": "function",
                                "function": {
                                    "name": "getWeather",
                                    "description": "Get weather info",
                                    "parameters": {"type": "object"}
                                }
                            }
                        ],
                        "messages": [
                            {"role": "user", "content": "What's the weather in SF?"}
                        ]
                    }
                    """, Encoding.UTF8, "application/json")
            };
            
            // Act
            var response = await httpClient.SendAsync(request);
            
            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var responseContent = await response.Content.ReadAsStringAsync();
            var responseJson = JsonDocument.Parse(responseContent);
            
            Assert.Equal("msg_tool_response", responseJson.RootElement.GetProperty("id").GetString());
            var toolUse = responseJson.RootElement.GetProperty("content")[0];
            Assert.Equal("tool_use", toolUse.GetProperty("type").GetString());
            Assert.Equal("getWeather", toolUse.GetProperty("name").GetString());
        }
        finally
        {
            File.Delete(testFilePath);
        }
    }
} 