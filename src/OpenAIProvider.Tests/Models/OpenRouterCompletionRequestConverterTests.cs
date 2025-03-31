using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using Xunit;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Models
{
    public class OpenRouterCompletionRequestConverterTests
    {
        [Fact]
        public void Convert_WithOpenRouterProviders_CreatesCorrectRequest()
        {
            // Arrange
            var messages = new List<IMessage>
            {
                new TextMessage { Role = Role.User, Text = "Hello" }
            };
            
            var options = new GenerateReplyOptions 
            { 
                ModelId = "anthropic/claude-3-opus",
                Providers = new[] { "openrouter" },
                ExtraProperties = new Dictionary<string, object?>
                {
                    ["transforms"] = new[] { "trim" }
                }
            };

            // Act
            var result = OpenRouterCompletionRequestConverter.Convert(messages, options);

            // Assert
            Assert.Equal("anthropic/claude-3-opus", result.Model);
            Assert.Equal(new[] { "trim" }, result.AdditionalParameters?["transforms"].GetValue<string[]>());
        }

        [Fact]
        public void Convert_WithRouteProperty_SetsAdditionalParameters()
        {
            // Arrange
            var messages = new List<IMessage>
            {
                new TextMessage { Role = Role.User, Text = "Hello" }
            };
            
            var options = new GenerateReplyOptions 
            { 
                ModelId = "openai/gpt-4",
                ExtraProperties = new Dictionary<string, object?>
                {
                    ["route"] = "fallback"
                }
            };

            // Act
            var result = OpenRouterCompletionRequestConverter.Convert(messages, options);

            // Assert
            Assert.Equal("openai/gpt-4", result.Model);
            Assert.Equal("fallback", result.AdditionalParameters?["route"].GetValue<string>());
        }

        [Fact]
        public void Convert_WithModelPreference_AddsCorrectParameters()
        {
            // Arrange
            var messages = new List<IMessage>
            {
                new TextMessage { Role = Role.User, Text = "Hello" }
            };
            
            var options = new GenerateReplyOptions 
            { 
                ExtraProperties = new Dictionary<string, object?>
                {
                    ["models"] = new[] 
                    { 
                        "openai/gpt-4-turbo", 
                        "anthropic/claude-3-opus" 
                    }
                }
            };

            // Act
            var result = OpenRouterCompletionRequestConverter.Convert(messages, options);

            // Assert
            var models = result.AdditionalParameters?["models"].GetValue<string[]>();
            Assert.Equal(2, models?.Length);
            Assert.Contains("openai/gpt-4-turbo", models);
            Assert.Contains("anthropic/claude-3-opus", models);
        }

        [Fact]
        public void Convert_WithResponseFormat_SetsJsonMode()
        {
            // Arrange
            var messages = new List<IMessage>
            {
                new TextMessage { Role = Role.User, Text = "Return a JSON object with name and age" }
            };
            
            var options = new GenerateReplyOptions 
            { 
                ModelId = "openai/gpt-4",
                ExtraProperties = new Dictionary<string, object?>
                {
                    ["response_format"] = new Dictionary<string, object?>
                    {
                        ["type"] = "json_object"
                    }
                }
            };

            // Act
            var result = OpenRouterCompletionRequestConverter.Convert(messages, options);

            // Assert
            Assert.NotNull(result.ResponseFormat);
            Assert.Equal("json_object", result.ResponseFormat.Type);
        }

        [Fact]
        public void Convert_WithCustomHttpHeaders_AddsToAdditionalParameters()
        {
            // Arrange
            var messages = new List<IMessage>
            {
                new TextMessage { Role = Role.User, Text = "Hello" }
            };
            
            var options = new GenerateReplyOptions 
            { 
                ModelId = "anthropic/claude-3-haiku",
                ExtraProperties = new Dictionary<string, object?>
                {
                    ["http_headers"] = new Dictionary<string, string>
                    {
                        ["Anthropic-Version"] = "2023-06-01",
                        ["X-Custom-Header"] = "value"
                    }
                }
            };

            // Act
            var result = OpenRouterCompletionRequestConverter.Convert(messages, options);

            // Assert
            var headers = result.AdditionalParameters?["http_headers"].AsObject();
            Assert.Equal("2023-06-01", headers["Anthropic-Version"].GetValue<string>());
            Assert.Equal("value", headers["X-Custom-Header"].GetValue<string>());
        }
    }

    public static class OpenRouterCompletionRequestConverter
    {
        public static ChatCompletionRequest Convert(IEnumerable<IMessage> messages, GenerateReplyOptions? options)
        {
            // First, create a basic request using the standard converter
            var request = ChatCompletionRequestConverter.Convert(messages, options);
            
            // Then, handle OpenRouter-specific parameters
            if (options?.ExtraProperties != null)
            {
                var jsonObject = new JsonObject();
                
                // Copy properties that are specific to OpenRouter
                foreach (var kvp in options.ExtraProperties)
                {
                    switch (kvp.Key)
                    {
                        case "transforms":
                            if (kvp.Value is string[] transforms)
                                jsonObject["transforms"] = JsonValue.Create(transforms);
                            break;
                        case "route":
                            if (kvp.Value is string route)
                                jsonObject["route"] = route;
                            break;
                        case "models":
                            if (kvp.Value is string[] modelPreferences)
                                jsonObject["models"] = JsonValue.Create(modelPreferences);
                            break;
                        case "http_headers":
                            if (kvp.Value is Dictionary<string, string> headers)
                            {
                                var headersObj = new JsonObject();
                                foreach (var header in headers)
                                {
                                    headersObj[header.Key] = header.Value;
                                }
                                jsonObject["http_headers"] = headersObj;
                            }
                            break;
                        case "response_format":
                            if (kvp.Value is Dictionary<string, object?> formatDict && 
                                formatDict.TryGetValue("type", out var formatType) && 
                                formatType is string formatTypeStr)
                            {
                                request.ResponseFormat = new ResponseFormat
                                {
                                    Type = formatTypeStr
                                };
                            }
                            break;
                    }
                }
                
                if (request.AdditionalParameters == null)
                {
                    request.AdditionalParameters = jsonObject;
                }
                else
                {
                    // Merge with existing additional parameters
                    foreach (var prop in jsonObject)
                    {
                        request.AdditionalParameters[prop.Key] = prop.Value;
                    }
                }
            }
            
            return request;
        }
    }
} 