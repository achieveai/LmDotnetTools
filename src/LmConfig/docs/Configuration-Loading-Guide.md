# LmConfig Configuration Loading Guide

This guide covers all the available methods for loading configuration into the LmConfig system, including the enhanced loading capabilities for embedded resources, stream factories, and various configuration sources.

## Overview

The LmConfig system supports multiple configuration loading methods to accommodate different deployment scenarios and architectural patterns:

1. **File-based loading** - Traditional JSON file loading
2. **Configuration system integration** - .NET IConfiguration integration
3. **Embedded resource loading** - Load from assembly embedded resources
4. **Stream factory loading** - Load from any stream source (HTTP, database, memory, etc.)
5. **IOptions pattern integration** - Full .NET options pattern support
6. **Direct configuration objects** - Use pre-built configuration objects

## Configuration Loading Methods

### 1. File-Based Configuration Loading

Load configuration from a JSON file on disk:

```csharp
services.AddLmConfig("path/to/models.json");
// or
services.AddLmConfigFromFile("path/to/models.json");
```

**Use cases:**
- Local development with configuration files
- Simple deployment scenarios
- Configuration files in known locations

### 2. .NET Configuration System Integration

Integrate with the .NET configuration system (appsettings.json, environment variables, etc.):

```csharp
// From IConfiguration
services.AddLmConfig(configuration);

// From specific configuration section  
services.AddLmConfig(configuration.GetSection("LmConfig"));
```

**Use cases:**
- Integration with existing .NET configuration
- Environment-specific configurations
- Configuration from multiple sources (JSON, environment variables, Azure Key Vault, etc.)

### 3. Embedded Resource Loading

Load configuration from embedded assembly resources:

```csharp
// Load from current assembly
services.AddLmConfigFromEmbeddedResource("models.json");

// Load from specific assembly
services.AddLmConfigFromEmbeddedResource("MyApp.Config.models.json", typeof(MyClass).Assembly);
```

**Resource naming patterns automatically checked:**
- `models.json`
- `AssemblyName.models.json`
- `AssemblyName.Resources.models.json`
- `AssemblyName.Config.models.json`

**Use cases:**
- Packaging configuration with the application
- Ensuring configuration is always available
- Simplifying deployment by embedding configuration

### 4. Stream Factory Loading

Load configuration from any stream source using factory functions:

```csharp
// Synchronous stream factory
services.AddLmConfigFromStream(() => 
{
    // Load from HTTP
    var client = new HttpClient();
    var response = client.GetAsync("https://config.example.com/models.json").Result;
    return response.Content.ReadAsStreamAsync().Result;
});

// Asynchronous stream factory
services.AddLmConfigFromStreamAsync(async () =>
{
    // Load from database
    var connectionString = "Server=...";
    using var connection = new SqlConnection(connectionString);
    var command = new SqlCommand("SELECT ConfigJson FROM Configurations WHERE Name = 'models'", connection);
    var json = (string)await command.ExecuteScalarAsync();
    return new MemoryStream(Encoding.UTF8.GetBytes(json));
});

// Load from memory
services.AddLmConfigFromStream(() =>
{
    var configJson = GetConfigurationFromSomewhere();
    return new MemoryStream(Encoding.UTF8.GetBytes(configJson));
});
```

**Use cases:**
- Loading configuration from HTTP endpoints
- Database-stored configuration
- Dynamic configuration generation
- Configuration from cloud storage
- In-memory configuration for testing

### 5. IOptions Pattern Integration

Full integration with .NET's IOptions pattern:

```csharp
// Standard options pattern
services.AddLmConfigWithOptions(configuration.GetSection("LmConfig"));

// Named options pattern
services.AddLmConfigWithNamedOptions(configuration.GetSection("LmConfig"), "Production");

// Access named options
var optionsSnapshot = serviceProvider.GetRequiredService<IOptionsSnapshot<AppConfig>>();
var config = optionsSnapshot.Get("Production");
```

**Use cases:**
- Advanced options scenarios with validation
- Multiple configuration instances
- Options monitoring and reloading
- Integration with options validation

### 6. Direct Configuration Objects

Use pre-built configuration objects:

```csharp
var appConfig = new AppConfig
{
    Models = new[]
    {
        new ModelConfig
        {
            Id = "gpt-4",
            Providers = new[] { /* provider configs */ }
        }
    },
    ProviderRegistry = new Dictionary<string, ProviderConnectionInfo>
    {
        ["OpenAI"] = new ProviderConnectionInfo
        {
            EndpointUrl = "https://api.openai.com/v1",
            ApiKeyEnvironmentVariable = "OPENAI_API_KEY"
        }
    }
};

services.AddLmConfig(appConfig);
```

**Use cases:**
- Programmatic configuration
- Configuration builders
- Testing scenarios
- Dynamic configuration construction

## Advanced Configuration Features

### JSON Parsing with Comments and Trailing Commas

All loading methods support JSON with comments and trailing commas:

```json
{
  // This is a comment
  "models": [
    {
      "id": "gpt-4", // Model identifier
      "capabilities": {
        "max_tokens": 4096, // Trailing comma is allowed
      },
    }
  ],
}
```

### Provider Availability Checking

The system automatically validates provider availability by checking API keys:

```csharp
// The system will check these environment variables
Environment.SetEnvironmentVariable("OPENAI_API_KEY", "your-key");
Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "your-key");

// Only providers with valid API keys will be considered available
var modelResolver = serviceProvider.GetRequiredService<IModelResolver>();
var isAvailable = await modelResolver.IsProviderAvailableAsync("OpenAI"); // true if key is set
```

### Configuration Validation

All loading methods include comprehensive validation:

```csharp
services.AddLmConfig("models.json")
    .ValidateLmConfig(); // Optional explicit validation

// Validation errors are thrown with detailed messages:
// - Missing models
// - Invalid provider configurations  
// - Missing API key environment variables
// - Capability mismatches
```

## Configuration File Structure

### Basic Structure

```json
{
  "models": [
    {
      "id": "model-identifier",
      "capabilities": {
        "token_limits": {
          "max_context_tokens": 4096,
          "max_output_tokens": 1024
        },
        "supports_streaming": true,
        "function_calling": {
          "supports_tools": true,
          "supports_parallel_calls": true
        }
      },
      "providers": [
        {
          "name": "ProviderName",
          "model_name": "provider-specific-model-name",
          "priority": 1,
          "pricing": {
            "prompt_per_million": 1.0,
            "completion_per_million": 2.0
          },
          "tags": ["fast", "cost-effective"]
        }
      ]
    }
  ],
  "provider_registry": {
    "ProviderName": {
      "endpoint_url": "https://api.provider.com/v1",
      "api_key_environment_variable": "PROVIDER_API_KEY",
      "compatibility": "OpenAI",
      "timeout": "00:01:00",
      "max_retries": 3
    }
  }
}
```

### Complete Example

See `src/LmConfig/docs/models.json` for a complete example with:
- Multiple models (GPT-4.1-mini, Claude-3-Sonnet)
- Full capability definitions
- Provider configurations with pricing
- Tag-based categorization
- Sub-provider configurations

## Error Handling

### Common Error Scenarios

1. **File not found**
```csharp
// Throws InvalidOperationException with clear message
services.AddLmConfig("nonexistent.json");
```

2. **Invalid JSON**
```csharp
// Throws InvalidOperationException with JSON parsing details
services.AddLmConfigFromStream(() => new MemoryStream(Encoding.UTF8.GetBytes("invalid json")));
```

3. **Missing embedded resource**
```csharp
// Throws InvalidOperationException with available resource list
services.AddLmConfigFromEmbeddedResource("missing.json");
```

4. **Empty configuration**
```csharp
// Throws InvalidOperationException if no models are defined
services.AddLmConfigFromStream(() => new MemoryStream(Encoding.UTF8.GetBytes("{\"models\":[]}")));
```

### Error Messages

The system provides detailed error messages:
- File paths and resource names
- JSON parsing errors with line numbers
- Available resources in assemblies
- Configuration validation details
- Missing API key information

## Best Practices

### 1. Configuration Organization

```csharp
// Organize by environment
services.AddLmConfig($"models.{environment}.json");

// Use configuration sections
services.AddLmConfig(configuration.GetSection("LmConfig"));
```

### 2. API Key Management

```csharp
// Use environment variables for API keys
Environment.SetEnvironmentVariable("OPENAI_API_KEY", apiKey);

// Check availability before use
var resolver = serviceProvider.GetRequiredService<IModelResolver>();
if (await resolver.IsProviderAvailableAsync("OpenAI"))
{
    // Provider is available
}
```

### 3. Testing Configurations

```csharp
// Use in-memory configurations for testing
services.AddLmConfigFromStream(() => new MemoryStream(Encoding.UTF8.GetBytes(testConfig)));

// Or use direct configuration objects
services.AddLmConfig(CreateTestConfiguration());
```

### 4. Validation

```csharp
// Always validate configuration in production
services.AddLmConfig("models.json")
    .ValidateLmConfig();
```

### 5. Logging

```csharp
// Enable logging to see configuration loading details
services.AddLogging(builder => 
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});
```

## Integration Examples

### ASP.NET Core Integration

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

// Load from configuration
builder.Services.AddLmConfig(builder.Configuration.GetSection("LmConfig"));

// Or load from embedded resource
builder.Services.AddLmConfigFromEmbeddedResource("appsettings.models.json");

var app = builder.Build();
```

### Console Application

```csharp
// Program.cs
var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole());

// Load configuration
services.AddLmConfig("models.json");

var serviceProvider = services.BuildServiceProvider();
var agent = serviceProvider.GetRequiredService<IAgent>();
```

### Azure Functions

```csharp
// Startup.cs
public void ConfigureServices(IServiceCollection services)
{
    // Load from Azure configuration
    services.AddLmConfig(Configuration);
    
    // Or load from Azure Blob Storage
    services.AddLmConfigFromStreamAsync(async () =>
    {
        var blobClient = new BlobClient(connectionString, "config", "models.json");
        var response = await blobClient.DownloadStreamingAsync();
        return response.Value.Content;
    });
}
```

This comprehensive configuration loading system provides maximum flexibility while maintaining simplicity for common scenarios. Choose the method that best fits your application architecture and deployment requirements. 