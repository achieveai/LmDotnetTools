namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Agents;

using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Mocks;
using AchieveAi.LmDotnetTools.TestUtils;
using AchieveAi.LmDotnetTools.TestUtils.MockTools;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Xunit;

public class FunctionToolTests
{
  [Fact]
  public async Task RequestFormat_FunctionTools()
  {
    TestLogger.Log("Starting RequestFormat_FunctionTools test");
    
    // Arrange
    var captureClient = new CaptureAnthropicClient();
    var agent = new AnthropicAgent("TestAgent", captureClient);
    TestLogger.Log("Created agent and capture client");
    
    var messages = new[]
    {
      new TextMessage { Role = Role.User, Text = "What's the weather in San Francisco?" }
    };
    TestLogger.Log($"Created {messages.Length} messages");

    // Get weather function from MockWeatherTool
    var weatherFunction = MockToolCallHelper.CreateMockToolCalls(
        new[] { typeof(MockWeatherTool) }
    ).Item1.First();
    
    var options = new GenerateReplyOptions
    {
      ModelId = "claude-3-7-sonnet-20250219",
      Functions = new[] { weatherFunction }
    };
    TestLogger.Log("Created options with function tools");
    TestLogger.LogObject("Function definition", weatherFunction);

    // Act
    TestLogger.Log("About to call GenerateReplyAsync");
    var response = await agent.GenerateReplyAsync(messages, options);
    TestLogger.Log("After GenerateReplyAsync call");
    
    // Log captured data
    TestLogger.Log($"CapturedRequest: {(captureClient.CapturedRequest != null ? "not null" : "null")}");
    if (captureClient.CapturedRequest?.Tools != null)
    {
      TestLogger.Log($"Tools count: {captureClient.CapturedRequest.Tools.Count}");
      foreach (var t in captureClient.CapturedRequest.Tools)
      {
        TestLogger.Log($"Tool - Name: {t.Name}, Description: {(t.Description ?? "null")}");
      }
    }
    else
    {
      TestLogger.Log("No tools captured in request");
    }

    // Assert with safe null checks
    Assert.NotNull(captureClient.CapturedRequest);
    
    // Some implementations might not set the Tools property if there are no tools
    Assert.NotNull(captureClient.CapturedRequest.Tools);
    Assert.NotEmpty(captureClient.CapturedRequest.Tools);
    
    // Check the first tool's properties with safe null checks
    var tool = captureClient.CapturedRequest.Tools[0];
    Assert.Equal("getWeather", tool.Name);
    Assert.NotNull(tool.InputSchema);
    Assert.NotNull(tool.Description);
  }
  
  [Fact]
  public async Task MultipleTools_ShouldBeCorrectlyConfigured()
  {
    TestLogger.Log("Starting MultipleTools_ShouldBeCorrectlyConfigured test");
    
    // Arrange
    var captureClient = new CaptureAnthropicClient();
    var agent = new AnthropicAgent("TestAgent", captureClient);
    TestLogger.Log("Created agent and capture client");
    
    var messages = new[] 
    {
      new TextMessage { Role = Role.System, Text = "You are a helpful assistant that can use tools to help users." },
      new TextMessage { Role = Role.User, Text = "List files in root and \"code\" directories." }
    };
    TestLogger.Log($"Created messages array with {messages.Length} messages");

    // Get mock functions from MockPythonExecutionTool
    var mockFunctions = MockToolCallHelper.CreateMockToolCalls(new[] { typeof(MockPythonExecutionTool) }).Item1;
    
    // Create multiple function definitions based on example_requests.json
    // But extract parameter information from the mock tools
    var listDirectoryTemplate = mockFunctions.First(f => f.Name == "list_directory");
    var listDirectoryFunction = new FunctionContract
    {
      Name = "python_mcp-list_directory",
      Description = "List the contents of a directory within the code directory",
      Parameters = listDirectoryTemplate.Parameters
    };
    
    var deleteFileTemplate = mockFunctions.First(f => f.Name == "delete_file");
    var deleteFileFunction = new FunctionContract
    {
      Name = "python_mcp-delete_file",
      Description = "Delete a file from the code directory",
      Parameters = deleteFileTemplate.Parameters
    };
    
    var getDirTreeTemplate = mockFunctions.First(f => f.Name == "get_directory_tree");
    var getDirTreeFunction = new FunctionContract
    {
      Name = "python_mcp-get_directory_tree",
      Description = "Get an ASCII tree representation of a directory structure",
      Parameters = getDirTreeTemplate.Parameters
    };
    
    var cleanupTemplate = mockFunctions.First(f => f.Name == "cleanup_code_directory");
    var cleanupFunction = new FunctionContract
    {
      Name = "python_mcp-cleanup_code_directory",
      Description = "Clean up the code directory by removing all files and subdirectories",
      Parameters = cleanupTemplate.Parameters
    };
    
    var options = new GenerateReplyOptions
    {
      ModelId = "claude-3-7-sonnet-20250219",
      MaxToken = 2000,
      Temperature = 0.7f,
      Functions = new[] { 
        listDirectoryFunction, 
        deleteFileFunction, 
        getDirTreeFunction, 
        cleanupFunction 
      }
    };
    TestLogger.Log("Created options with multiple function tools");
    
    // Act
    TestLogger.Log("About to call GenerateReplyAsync");
    var response = await agent.GenerateReplyAsync(messages, options);
    TestLogger.Log("After GenerateReplyAsync call");
    
    // Assert
    Assert.NotNull(captureClient.CapturedRequest);
    Assert.NotNull(captureClient.CapturedRequest.Tools);
    Assert.Equal(4, captureClient.CapturedRequest.Tools!.Count);
    
    // Verify that all tools were properly configured
    var toolNames = captureClient.CapturedRequest.Tools!
      .Select(t => t.Name)
      .ToList();
      
    Assert.Contains("python_mcp-list_directory", toolNames);
    Assert.Contains("python_mcp-delete_file", toolNames);
    Assert.Contains("python_mcp-get_directory_tree", toolNames);
    Assert.Contains("python_mcp-cleanup_code_directory", toolNames);
  }
  
  [Fact]
  public async Task ToolUseResponse_ShouldBeCorrectlyParsed()
  {
    TestLogger.Log("Starting ToolUseResponse_ShouldBeCorrectlyParsed test");
    
    // Arrange - create a special mock that returns a tool use response
    var mockClient = new ToolResponseMockClient();
    var agent = new AnthropicAgent("TestAgent", mockClient);
    TestLogger.Log("Created agent and mock client for tool use response");
    
    var messages = new[]
    {
      new TextMessage { Role = Role.User, Text = "List files in the root directory" }
    };
    
    // Extract list_directory function from MockPythonExecutionTool
    var listDirTemplate = MockToolCallHelper.CreateMockToolCalls(
        new[] { typeof(MockPythonExecutionTool) }
    ).Item1.First(f => f.Name == "list_directory");
    
    // Create function definition following the original test pattern
    var listDirFunction = new FunctionContract
    {
      Name = "python_mcp-list_directory",
      Description = "List directory contents",
      Parameters = listDirTemplate.Parameters
    };
    
    var options = new GenerateReplyOptions
    {
      ModelId = "claude-3-7-sonnet-20250219",
      Functions = new[] { listDirFunction }
    };
    
    // Act
    TestLogger.Log("About to call GenerateReplyAsync");
    var response = await agent.GenerateReplyAsync(messages, options);
    TestLogger.Log("After GenerateReplyAsync call");
    
    // Verify we got a proper response with text
    Assert.NotNull(response);
    Assert.IsType<TextMessage>(response.First());
    
    var textResponse = (TextMessage)response.First();
    Assert.Contains("I'll help you list the files", textResponse.Text);
    
    // Check that the mock client received the right request
    Assert.NotNull(mockClient.LastRequest);
    Assert.NotNull(mockClient.LastRequest.Tools);
    Assert.Single(mockClient.LastRequest.Tools!);
    Assert.Equal("python_mcp-list_directory", mockClient.LastRequest.Tools![0].Name);
  }
} 