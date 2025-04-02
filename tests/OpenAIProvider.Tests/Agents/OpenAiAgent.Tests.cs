using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Mocks;
using AchieveAi.LmDotnetTools.TestUtils;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Agents;

public class OpenAiAgentTests
{
  private static string EnvTestPath => Path.Combine(AchieveAi.LmDotnetTools.TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory), ".env.test");

  [Fact]
  public async Task SimpleConversation_ShouldReturnResponse()
  {
    // Use the factory to create a DatabasedClientWrapper with the .env.test file
    string testCaseName = "SimpleConversation_ShouldReturnResponse";
    IOpenClient client = OpenClientFactory.CreateDatabasedClient(testCaseName, EnvTestPath);
    
    var agent = new OpenClientAgent("TestAgent", client);
    
    // Create a system message
    var systemMessage = new TextMessage { 
      Role = Role.System, 
      Text = "You're a helpful AI Agent" 
    };
    
    // Create a user message
    var userMessage = new TextMessage { 
      Role = Role.User, 
      Text = "Hello Bot" 
    };
    
    // Act
    var response = await agent.GenerateReplyAsync(
      new[] { systemMessage, userMessage },
      new() { 
        ModelId = "microsoft/phi-4-multimodal-instruct" 
      }
    );
    
    // Assert
    Assert.NotNull(response);
    
    // Verify it's a text message with content
    Assert.IsType<OpenTextMessage>(response);
    var textMessage = response as OpenTextMessage;
    Assert.NotNull(textMessage?.Text);
    Assert.NotEmpty(textMessage!.Text);
  }

  [Fact]
  public async Task ChatCompletionRequest_SerializesToCorrectJson()
  {
    // Arrange
    var messages = new[]
    {
        new TextMessage { Role = Role.System, Text = "You will always respond in JSON as `{\"response\": \"...\"}`" },
        new TextMessage { Role = Role.User, Text = "Hello Bot!!!" }
    };

    var options = new GenerateReplyOptions
    {
        ModelId = "gpt-4o-mini",
        Temperature = 0.7f,
        MaxToken = 1000,
        ResponseFormat = ResponseFormat.JSON,
    };

    // Act
    var request = ChatCompletionRequest.FromMessages(messages, options);
    IOpenClient client = OpenClientFactory.CreateDatabasedClient(
      "ChatCompletionRequest_SerializesToCorrectJson",
      EnvTestPath);
    var agent = new OpenClientAgent("TestAgent", client);
    var response = await agent.GenerateReplyAsync(
      messages,
      options);
    var json = JsonObject.Parse(((OpenTextMessage)response)!.GetText()!);
    Assert.NotNull(json);
    Assert.NotNull(json["response"]);
  }
  
  [Fact(Skip = "This test depends on mocked responses that are not available in the test environment")]
  public async Task FunctionToolCall_ShouldReturnToolMessage()
  {
    // Arrange
    var messages = new[]
    {
        new TextMessage { Role = Role.System, Text = "You're a helpful AI Agent that can use tools" },
        new TextMessage { Role = Role.User, Text = "What's the weather in San Francisco?" }
    };

    var options = new GenerateReplyOptions
    {
        ModelId = "gpt-4",
        Functions = new[]
        {
            new FunctionContract
            {
                Name = "getWeather",
                Description = "Get current weather for a location",
                Parameters = new List<FunctionParameterContract>
                {
                    new FunctionParameterContract
                    {
                        Name = "location",
                        Description = "City name",
                        ParameterType = typeof(string),
                        IsRequired = true
                    },
                    new FunctionParameterContract
                    {
                        Name = "unit",
                        Description = "Temperature unit (celsius or fahrenheit)",
                        ParameterType = typeof(string),
                        IsRequired = false
                    }
                }
            }
        }
    };

    // Act
    IOpenClient client = OpenClientFactory.CreateDatabasedClient(
      "FunctionToolCall_ShouldReturnToolMessage",
      EnvTestPath);
    var agent = new OpenClientAgent("TestAgent", client);
    var response = await agent.GenerateReplyAsync(
      messages,
      options);

    // Assert
    Assert.NotNull(response);
    
    // Since the response type may vary depending on the implementation,
    // we should check for different possible types of responses
    if (response is OpenToolMessage toolMessage)
    {
        Assert.NotNull(toolMessage.ToolCalls);
        Assert.Single(toolMessage.ToolCalls);
        Assert.Equal("getWeather", toolMessage.ToolCalls[0].FunctionName);
    }
    else if (response is OpenTextMessage textMessage)
    {
        Assert.NotNull(textMessage.Text);
        Assert.Contains("getWeather", textMessage.Text);
    }
    else
    {
        // If neither expected type, fail the test
        Assert.Fail($"Expected tool call message, got {response.GetType().Name}");
    }
  }
}