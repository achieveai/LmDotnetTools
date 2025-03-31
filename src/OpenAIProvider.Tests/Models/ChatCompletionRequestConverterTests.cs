using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using Xunit;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Models
{
    public class ChatCompletionRequestConverterTests
    {
        [Fact]
        public void Convert_BasicMessages_CreatesCorrectRequest()
        {
            // Arrange
            var messages = new List<IMessage>
            {
                new TextMessage { Role = Role.System, Text = "You are a helpful assistant" },
                new TextMessage { Role = Role.User, Text = "Hello, how are you?" }
            };
            
            var options = new GenerateReplyOptions 
            { 
                ModelId = "gpt-4",
                Temperature = 0.5f,
                MaxToken = 2000
            };

            // Act
            var result = ChatCompletionRequestConverter.Convert(messages, options);

            // Assert
            Assert.Equal("gpt-4", result.Model);
            Assert.Equal(0.5, result.Temperature);
            Assert.Equal(2000, result.MaxTokens);
            Assert.Equal(2, result.Messages.Count);
            
            Assert.Equal(RoleEnum.System, result.Messages[0].Role);
            Assert.Equal("You are a helpful assistant", result.Messages[0].Content!.Get<string>());
            
            Assert.Equal(RoleEnum.User, result.Messages[1].Role);
            Assert.Equal("Hello, how are you?", result.Messages[1].Content!.Get<string>());
        }

        [Fact]
        public void Convert_WithAllOptions_SetsAllProperties()
        {
            // Arrange
            var messages = new List<IMessage>
            {
                new TextMessage { Role = Role.User, Text = "Generate something creative" }
            };
            
            var options = new GenerateReplyOptions 
            { 
                ModelId = "gpt-4-turbo",
                Temperature = 0.9f,
                MaxToken = 1000,
                TopP = 0.95f,
                StopSequence = new[] { "stop1", "stop2" },
                RandomSeed = 42,
                SafePrompt = true,
                Stream = true
            };

            // Act
            var result = ChatCompletionRequestConverter.Convert(messages, options);

            // Assert
            Assert.Equal("gpt-4-turbo", result.Model);
            Assert.Equal(0.9, result.Temperature);
            Assert.Equal(1000, result.MaxTokens);
            Assert.Equal(0.95f, result.TopP);
            Assert.Equal(new[] { "stop1", "stop2" }, result.Stop);
            Assert.Equal(42, result.RandomSeed);
            Assert.True(result.SafePrompt);
            Assert.True(result.Stream);
        }

        [Fact]
        public void Convert_WithFunctionCalls_SetsFunctionTools()
        {
            // Arrange
            var messages = new List<IMessage>
            {
                new TextMessage { Role = Role.User, Text = "What's the weather like?" }
            };
            
            var functions = new[]
            {
                new FunctionContract
                {
                    Name = "get_weather",
                    Description = "Get the current weather",
                    Parameters = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["location"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "The city name"
                            }
                        },
                        ["required"] = new JsonArray { "location" }
                    }
                }
            };
            
            var options = new GenerateReplyOptions 
            { 
                ModelId = "gpt-4",
                Functions = functions
            };

            // Act
            var result = ChatCompletionRequestConverter.Convert(messages, options);

            // Assert
            Assert.NotNull(result.Tools);
            Assert.Single(result.Tools);
            
            var tool = result.Tools[0];
            Assert.Equal("function", tool.Type);
            Assert.Equal("get_weather", tool.Function.Name);
            Assert.Equal("Get the current weather", tool.Function.Description);
            Assert.NotNull(tool.Function.Parameters);
        }

        [Fact]
        public void Convert_WithVariousMessageTypes_ConvertsCorrectly()
        {
            // Arrange
            var toolCall = new ToolCall("get_weather", "{\"location\":\"New York\"}") 
            { 
                ToolCallId = "call_123" 
            };
            
            var messages = new List<IMessage>
            {
                new TextMessage { Role = Role.System, Text = "You are a helpful assistant" },
                new TextMessage { Role = Role.User, Text = "What's the weather in New York?" },
                new ToolsCallMessage 
                { 
                    Role = Role.Assistant, 
                    ToolCalls = new[] { toolCall }.ToImmutableList() 
                },
                new TextMessage 
                { 
                    Role = Role.Function, 
                    Text = "{\"temp\":72,\"condition\":\"sunny\"}", 
                    FromAgent = "get_weather"
                },
                new TextMessage { Role = Role.Assistant, Text = "It's 72 degrees and sunny in New York." }
            };
            
            var options = new GenerateReplyOptions { ModelId = "gpt-4" };

            // Act
            var result = ChatCompletionRequestConverter.Convert(messages, options);

            // Assert
            Assert.Equal(5, result.Messages.Count);
            
            // Check tool call message conversion
            var toolCallMessage = result.Messages[2];
            Assert.Equal(RoleEnum.Assistant, toolCallMessage.Role);
            Assert.NotNull(toolCallMessage.ToolCalls);
            Assert.Single(toolCallMessage.ToolCalls);
            Assert.Equal("get_weather", toolCallMessage.ToolCalls[0].Function.Name);
            
            // Check function response conversion
            var functionResponseMessage = result.Messages[3];
            Assert.Equal(RoleEnum.Tool, functionResponseMessage.Role);
            Assert.Equal("get_weather", functionResponseMessage.Name);
            Assert.Equal("{\"temp\":72,\"condition\":\"sunny\"}", functionResponseMessage.Content.Get<string>());
        }

        [Fact]
        public void Convert_WithDefaultOptions_UsesSensibleDefaults()
        {
            // Arrange
            var messages = new List<IMessage>
            {
                new TextMessage { Role = Role.User, Text = "Hello" }
            };

            // Act - passing null options
            var result = ChatCompletionRequestConverter.Convert(messages, null);

            // Assert
            Assert.Equal("gpt-3.5-turbo", result.Model); // Default model
            Assert.Equal(0.7, result.Temperature); // Default temperature
            Assert.Equal(4096, result.MaxTokens); // Default max tokens
            Assert.Single(result.Messages);
            Assert.Equal(RoleEnum.User, result.Messages[0].Role);
        }
    }

    public static class ChatCompletionRequestConverter
    {
        public static ChatCompletionRequest Convert(IEnumerable<IMessage> messages, GenerateReplyOptions? options)
        {
            string modelName = options?.ModelId ?? "gpt-3.5-turbo";
            
            // Extract model from ExtraProperties if not in ModelId
            if (string.IsNullOrEmpty(options?.ModelId) && 
                options?.ExtraProperties != null && 
                options.ExtraProperties.TryGetValue("model", out var modelObj) && 
                modelObj is string modelStr)
            {
                modelName = modelStr;
            }
            
            var temperature = options?.Temperature ?? 0.7f;
            var maxTokens = options?.MaxToken ?? 4096;
            
            // Convert messages to ChatMessage objects
            var chatMessages = messages.Select(message => {
                var role = message.Role == Role.User ? RoleEnum.User :
                           message.Role == Role.System ? RoleEnum.System :
                           message.Role == Role.Function ? RoleEnum.Tool :
                           RoleEnum.Assistant;

                var chatMessage = new ChatMessage { 
                    Role = role,
                    Name = message.FromAgent
                };

                // Convert based on message type
                if (message is TextMessage textMessage)
                {
                    chatMessage.Content = ChatMessage.CreateContent(textMessage.Text);
                }
                else if (message is ToolsCallMessage toolsCallMessage && toolsCallMessage.ToolCalls != null)
                {
                    chatMessage.ToolCalls = toolsCallMessage.ToolCalls.Select(tc => 
                        new FunctionContent { 
                            Id = tc.ToolCallId ?? tc.ComputeToolCallId(),
                            Type = "function",
                            Function = new FunctionContentDetail {
                                Name = tc.Name,
                                Arguments = tc.Arguments
                            } 
                        }).ToList();
                }
                // Add other message type conversions as needed

                return chatMessage;
            }).ToList();

            var request = new ChatCompletionRequest(
                modelName,
                chatMessages,
                temperature,
                maxTokens
            );
            
            // Set additional options if provided
            if (options != null)
            {
                if (options.TopP.HasValue)
                    request.TopP = options.TopP.Value;
                
                if (options.StopSequence != null)
                    request.Stop = options.StopSequence;
                
                if (options.Stream.HasValue)
                    request.Stream = options.Stream.Value;
                
                if (options.SafePrompt.HasValue)
                    request.SafePrompt = options.SafePrompt.Value;
                
                if (options.RandomSeed.HasValue)
                    request.RandomSeed = options.RandomSeed.Value;
                
                // Convert function definitions to OpenAI tools format
                if (options.Functions != null && options.Functions.Length > 0)
                {
                    request.Tools = options.Functions.Select(f => new FunctionTool
                    {
                        Type = "function",
                        Function = new FunctionDefinition
                        {
                            Name = f.Name,
                            Description = f.Description,
                            Parameters = f.Parameters
                        }
                    }).ToList();
                }
            }
            
            return request;
        }
    }
} 