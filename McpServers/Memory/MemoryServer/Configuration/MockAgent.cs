using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace MemoryServer.Configuration;

// Simple mock agent for when API keys are not configured
public class MockAgent : IAgent
{
  private readonly string _name;
  
  public MockAgent(string name)
  {
    _name = name;
  }
  
  public Task<IEnumerable<IMessage>> GenerateReplyAsync(
    IEnumerable<IMessage> messages,
    GenerateReplyOptions? options = null,
    CancellationToken cancellationToken = default)
  {
    var response = new TextMessage 
    { 
      Text = $"Mock response from {_name}. LLM features are disabled - API key not configured.",
      Role = Role.Assistant,
      FromAgent = _name
    };
    return Task.FromResult<IEnumerable<IMessage>>(new[] { response });
  }
} 