# LmDotnet - Large Language Model SDK for .NET

LmDotnet is a comprehensive .NET SDK for working with large language models (LLMs) from multiple providers including OpenAI, Anthropic, and OpenRouter.

## Features

- **Multi-Provider Support**: Unified interface for OpenAI, Anthropic, OpenRouter, and more
- **Streaming & Synchronous**: Support for both streaming and traditional request/response patterns
- **Middleware Pipeline**: Extensible middleware for logging, caching, function calls, and usage tracking
- **Type Safety**: Strongly-typed models and responses
- **Performance Optimized**: Built for high-throughput production scenarios
- **Comprehensive Testing**: Extensive test coverage with mocking utilities

## Quick Start

### Installation

```bash
dotnet add package AchieveAi.LmDotnetTools.LmCore
dotnet add package AchieveAi.LmDotnetTools.OpenAIProvider
dotnet add package AchieveAi.LmDotnetTools.AnthropicProvider
```

### Basic Usage

```csharp
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;

// Create an agent
var agent = new OpenClientAgent("MyAgent", openClient);

// Send a message
var messages = new[] { new TextMessage { Role = Role.User, Text = "Hello!" } };
var response = await agent.GenerateReplyAsync(messages);
```

## OpenRouter Usage Tracking

LmDotnet includes comprehensive usage tracking for OpenRouter, providing automatic token and cost monitoring.

### Key Features

- ✅ **Automatic Integration**: Seamlessly activated when using OpenRouter as provider
- ✅ **Inline Usage Preferred**: Uses usage data directly from API responses when available
- ✅ **Intelligent Fallback**: Falls back to generation endpoint lookup when needed
- ✅ **Performance Optimized**: In-memory caching with configurable TTL
- ✅ **Zero Configuration**: Works out-of-the-box with sensible defaults
- ✅ **Comprehensive Logging**: Structured logging for monitoring and debugging

### Quick Setup

```bash
# Environment variables
export ENABLE_USAGE_MIDDLEWARE=true
export OPENROUTER_API_KEY=sk-or-your-api-key-here
export USAGE_CACHE_TTL_SEC=300
```

```csharp
// Usage data automatically provided in dedicated UsageMessage
var options = new GenerateReplyOptions { ModelId = "openai/gpt-4" };
var messages = await agent.GenerateReplyAsync(userMessages, options);

// Access usage information from UsageMessage
var usageMessage = messages.OfType<UsageMessage>().LastOrDefault();
if (usageMessage != null)
{
    var usage = usageMessage.Usage;
    Console.WriteLine($"Tokens: {usage.TotalTokens}, Cost: ${usage.TotalCost:F4}");
}
```

### Comprehensive Documentation

For detailed configuration, troubleshooting, and examples:

📖 **[Complete OpenRouter Usage Tracking Guide](src/OpenAIProvider/Configuration/README.md)**

## Project Structure

```
LmDotnet/
├── src/
│   ├── LmCore/              # Core interfaces and models
│   ├── OpenAIProvider/      # OpenAI and OpenRouter provider
│   ├── AnthropicProvider/   # Anthropic Claude provider
│   ├── LmConfig/           # Configuration and agent factories
│   ├── LmEmbeddings/       # Embedding services
│   └── LmTestUtils/        # Testing utilities
├── tests/                  # Comprehensive test suite
└── docs/                   # Additional documentation
```

## Supported Providers

| Provider | Models | Streaming | Function Calls | Usage Tracking |
|----------|---------|-----------|---------------|----------------|
| **OpenAI** | GPT-3.5, GPT-4, GPT-4 Turbo | ✅ | ✅ | ✅ |
| **OpenRouter** | 100+ models | ✅ | ✅ | ✅ **Enhanced** |
| **Anthropic** | Claude 3 (Sonnet, Haiku, Opus) | ✅ | ✅ | ✅ |
| **Custom** | Extensible | ✅ | ✅ | 🔧 Configurable |

## Advanced Features

### Middleware Pipeline

```csharp
// Custom middleware for logging, caching, etc.
public class CustomMiddleware : IStreamingMiddleware
{
    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context, IStreamingAgent agent, CancellationToken cancellationToken)
    {
        // Pre-processing
        yield return await agent.GenerateReplyStreamingAsync(context.Messages, context.Options, cancellationToken);
        // Post-processing
    }
}
```

### Function Calling

```csharp
var functions = new[]
{
    new FunctionDefinition
    {
        Name = "get_weather",
        Description = "Get current weather",
        Parameters = new { location = new { type = "string" } }
    }
};

var options = new GenerateReplyOptions { Functions = functions };
var response = await agent.GenerateReplyAsync(messages, options);
```

### Performance Monitoring

Built-in performance tracking and telemetry:

```csharp
// Performance metrics automatically collected
var metrics = performanceTracker.GetMetrics();
Console.WriteLine($"Average latency: {metrics.AverageLatency}ms");
Console.WriteLine($"Token throughput: {metrics.TokensPerSecond}");
```

## Configuration

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `ENABLE_USAGE_MIDDLEWARE` | `true` | Enable OpenRouter usage tracking |
| `OPENROUTER_API_KEY` | - | OpenRouter API key (required for usage tracking) |
| `USAGE_CACHE_TTL_SEC` | `300` | Usage cache TTL in seconds |
| `ENABLE_INLINE_USAGE` | `true` | Prefer inline usage over fallback |

### Dependency Injection

```csharp
// In Program.cs or Startup.cs
services.AddLmDotnet(configuration);
services.ValidateOpenRouterUsageConfiguration(configuration);
```

## Testing

Comprehensive testing utilities included:

```csharp
// Mock HTTP responses
var handler = FakeHttpMessageHandler.CreateOpenAIResponseHandler("Hello!");
var httpClient = new HttpClient(handler);

// Mock streaming responses  
var handler = FakeHttpMessageHandler.CreateSseStreamHandler(events);

// Performance testing
var agent = new TestAgent();
var metrics = await PerformanceTestHelper.MeasureLatency(agent, messages);
```

## Contributing

1. Fork the repository
2. Create a feature branch
3. Add tests for new functionality
4. Ensure all tests pass
5. Submit a pull request

## Documentation

- **[OpenRouter Usage Tracking](src/OpenAIProvider/Configuration/README.md)** - Complete usage tracking guide
- **[Testing Utilities](src/LmTestUtils/README-SSE.md)** - SSE testing documentation  
- **[Architecture](docs/)** - System architecture and design decisions

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Support

- **Issues**: [GitHub Issues](https://github.com/your-org/LmDotnet/issues)
- **Discussions**: [GitHub Discussions](https://github.com/your-org/LmDotnet/discussions)  
- **OpenRouter Support**: [OpenRouter Help](https://openrouter.ai/support)

---

**Built with ❤️ for the .NET community** 