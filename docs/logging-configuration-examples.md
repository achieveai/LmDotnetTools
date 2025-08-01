# Logging Configuration Examples

This document provides comprehensive examples for configuring logging in the LmDotnetTools library. The logging system supports multiple configuration approaches to suit different application architectures and deployment scenarios.

## Table of Contents

1. [Manual Logger Injection](#manual-logger-injection)
2. [Dependency Injection Setup](#dependency-injection-setup)
3. [Different Logging Providers](#different-logging-providers)
4. [Advanced Configuration](#advanced-configuration)
5. [Performance Considerations](#performance-considerations)

## Manual Logger Injection

### Basic Manual Configuration

For simple applications or when you want direct control over logger creation:

```csharp
using Microsoft.Extensions.Logging;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

// Create a logger factory
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(LogLevel.Information)
        .AddConsole()
        .AddDebug();
});

// Create typed loggers for each component
var unifiedAgentLogger = loggerFactory.CreateLogger<UnifiedAgent>();
var middlewareLogger = loggerFactory.CreateLogger<FunctionCallMiddleware>();

// Create components with loggers
var unifiedAgent = new UnifiedAgent(
    modelResolver, 
    agentFactory, 
    unifiedAgentLogger);

var middleware = new FunctionCallMiddleware(
    functions, 
    functionMap, 
    "MyMiddleware",
    middlewareLogger);
```

### Manual Configuration with Custom Log Levels

```csharp
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(LogLevel.Debug)
        .AddConsole(options =>
        {
            options.IncludeScopes = true;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        })
        // Set specific levels for different components
        .AddFilter("AchieveAi.LmDotnetTools.LmConfig.Agents.UnifiedAgent", LogLevel.Information)
        .AddFilter("AchieveAi.LmDotnetTools.LmCore.Middleware", LogLevel.Debug)
        .AddFilter("AchieveAi.LmDotnetTools.OpenAIProvider", LogLevel.Warning);
});

// Create loggers and components as above
```

### Manual Configuration with Structured Logging

```csharp
using Serilog;
using Serilog.Extensions.Logging;

// Configure Serilog for structured logging
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: 
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/lmdotnettools-.txt", 
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// Create Microsoft.Extensions.Logging compatible factory
using var loggerFactory = new SerilogLoggerFactory(Log.Logger);

var unifiedAgentLogger = loggerFactory.CreateLogger<UnifiedAgent>();
var unifiedAgent = new UnifiedAgent(modelResolver, agentFactory, unifiedAgentLogger);
```

## Dependency Injection Setup

### ASP.NET Core Integration

```csharp
// Program.cs or Startup.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Models;

var builder = WebApplication.CreateBuilder(args);

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Add category-specific log levels
builder.Logging.AddFilter("AchieveAi.LmDotnetTools", LogLevel.Debug);
builder.Logging.AddFilter("AchieveAi.LmDotnetTools.OpenAIProvider.Agents.OpenClient", LogLevel.Information);

// Register LmDotnetTools components
builder.Services.AddSingleton<IModelResolver, ModelResolver>();
builder.Services.AddSingleton<IProviderAgentFactory>(provider =>
    new ProviderAgentFactory(
        provider,
        provider.GetService<ILoggerFactory>()));

builder.Services.AddTransient<UnifiedAgent>(provider =>
    new UnifiedAgent(
        provider.GetRequiredService<IModelResolver>(),
        provider.GetRequiredService<IProviderAgentFactory>(),
        provider.GetService<ILogger<UnifiedAgent>>()));

var app = builder.Build();

// Use the configured agent
var unifiedAgent = app.Services.GetRequiredService<UnifiedAgent>();
```

### Console Application with DI

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Create host with DI container
var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Debug);
        
        // Configure structured logging format
        logging.AddSimpleConsole(options =>
        {
            options.IncludeScopes = true;
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
    })
    .ConfigureServices(services =>
    {
        // Register your components
        services.AddSingleton<IModelResolver, ModelResolver>();
        services.AddSingleton<IProviderAgentFactory>(provider =>
            new ProviderAgentFactory(
                provider,
                provider.GetService<ILoggerFactory>()));
        
        services.AddTransient<UnifiedAgent>();
        services.AddTransient<MyApplication>();
    })
    .Build();

// Run your application
var app = host.Services.GetRequiredService<MyApplication>();
await app.RunAsync();

public class MyApplication
{
    private readonly UnifiedAgent _agent;
    private readonly ILogger<MyApplication> _logger;

    public MyApplication(UnifiedAgent agent, ILogger<MyApplication> logger)
    {
        _agent = agent;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        _logger.LogInformation("Starting application");
        
        var messages = new[] { new TextMessage { Text = "Hello", Role = Role.User } };
        var options = new GenerateReplyOptions { ModelId = "gpt-4" };
        
        var response = await _agent.GenerateReplyAsync(messages, options);
        
        _logger.LogInformation("Application completed successfully");
    }
}
```

### Factory Pattern with Logger Propagation

```csharp
public class LmDotnetToolsFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;

    public LmDotnetToolsFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _loggerFactory = serviceProvider.GetService<ILoggerFactory>();
    }

    public UnifiedAgent CreateUnifiedAgent()
    {
        var modelResolver = _serviceProvider.GetRequiredService<IModelResolver>();
        var agentFactory = CreateProviderAgentFactory();
        var logger = _loggerFactory?.CreateLogger<UnifiedAgent>();
        
        return new UnifiedAgent(modelResolver, agentFactory, logger);
    }

    public IProviderAgentFactory CreateProviderAgentFactory()
    {
        return new ProviderAgentFactory(_serviceProvider, _loggerFactory);
    }

    public FunctionCallMiddleware CreateFunctionCallMiddleware(
        IEnumerable<FunctionContract> functions,
        IDictionary<string, Func<string, Task<string>>> functionMap,
        string? name = null)
    {
        var logger = _loggerFactory?.CreateLogger<FunctionCallMiddleware>();
        return new FunctionCallMiddleware(functions, functionMap, name, logger);
    }
}

// Usage
var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
services.AddSingleton<IModelResolver, ModelResolver>();
services.AddSingleton<LmDotnetToolsFactory>();

var serviceProvider = services.BuildServiceProvider();
var factory = serviceProvider.GetRequiredService<LmDotnetToolsFactory>();

var unifiedAgent = factory.CreateUnifiedAgent();
var middleware = factory.CreateFunctionCallMiddleware(functions, functionMap);
```

## Different Logging Providers

### Console Logging

```csharp
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .AddConsole(options =>
        {
            options.IncludeScopes = true;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            options.UseUtcTimestamp = true;
        })
        .SetMinimumLevel(LogLevel.Information);
});
```

### File Logging with Serilog

```csharp
using Serilog;
using Serilog.Extensions.Logging;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogLevel.Warning)
    .MinimumLevel.Override("System", LogLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: 
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.File("logs/lmdotnettools-.txt",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj} {Properties:j}{NewLine}{Exception}")
    .CreateLogger();

using var loggerFactory = new SerilogLoggerFactory(Log.Logger);
```

### JSON Structured Logging

```csharp
using Serilog;
using Serilog.Formatting.Json;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(new JsonFormatter())
    .WriteTo.File(new JsonFormatter(), "logs/lmdotnettools-.json",
        rollingInterval: RollingInterval.Day)
    .CreateLogger();

using var loggerFactory = new SerilogLoggerFactory(Log.Logger);
```

### Application Insights Integration

```csharp
// In ASP.NET Core
builder.Services.AddApplicationInsightsTelemetry();
builder.Logging.AddApplicationInsights();

// Configure specific categories
builder.Logging.AddFilter<ApplicationInsightsLoggerProvider>(
    "AchieveAi.LmDotnetTools", LogLevel.Information);
```

### Custom Logging Provider

```csharp
public class CustomLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        return new CustomLogger(categoryName);
    }

    public void Dispose() { }
}

public class CustomLogger : ILogger
{
    private readonly string _categoryName;

    public CustomLogger(string categoryName)
    {
        _categoryName = categoryName;
    }

    public IDisposable BeginScope<TState>(TState state) => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, 
        Exception exception, Func<TState, Exception, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        
        // Custom logging logic here
        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] [{logLevel}] {_categoryName}: {message}");
        
        if (exception != null)
        {
            Console.WriteLine($"Exception: {exception}");
        }
    }
}

// Usage
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddProvider(new CustomLoggerProvider());
});
```

## Advanced Configuration

### Environment-Specific Configuration

```csharp
public static class LoggingConfiguration
{
    public static ILoggingBuilder ConfigureLogging(this ILoggingBuilder builder, IConfiguration configuration)
    {
        var environment = configuration["Environment"] ?? "Development";
        
        builder.ClearProviders();
        
        switch (environment.ToLower())
        {
            case "development":
                builder
                    .AddConsole()
                    .AddDebug()
                    .SetMinimumLevel(LogLevel.Debug)
                    .AddFilter("AchieveAi.LmDotnetTools", LogLevel.Trace);
                break;
                
            case "staging":
                builder
                    .AddConsole()
                    .SetMinimumLevel(LogLevel.Information)
                    .AddFilter("AchieveAi.LmDotnetTools", LogLevel.Debug);
                break;
                
            case "production":
                builder
                    .AddConsole()
                    .SetMinimumLevel(LogLevel.Warning)
                    .AddFilter("AchieveAi.LmDotnetTools", LogLevel.Information);
                break;
        }
        
        return builder;
    }
}

// Usage in Program.cs
builder.Logging.ConfigureLogging(builder.Configuration);
```

### Configuration from appsettings.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information",
      "AchieveAi.LmDotnetTools": "Debug",
      "AchieveAi.LmDotnetTools.LmConfig.Agents.UnifiedAgent": "Information",
      "AchieveAi.LmDotnetTools.LmCore.Middleware": "Debug",
      "AchieveAi.LmDotnetTools.OpenAIProvider.Agents.OpenClient": "Information"
    },
    "Console": {
      "IncludeScopes": true,
      "TimestampFormat": "yyyy-MM-dd HH:mm:ss "
    }
  }
}
```

```csharp
// In Program.cs
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
```

### Conditional Logging with Scopes

```csharp
public class MyService
{
    private readonly ILogger<MyService> _logger;

    public MyService(ILogger<MyService> logger)
    {
        _logger = logger;
    }

    public async Task ProcessRequestAsync(string requestId, string modelId)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["RequestId"] = requestId,
            ["ModelId"] = modelId,
            ["Operation"] = "ProcessRequest"
        });

        _logger.LogInformation("Starting request processing");

        try
        {
            var unifiedAgent = CreateUnifiedAgent();
            var result = await unifiedAgent.GenerateReplyAsync(messages, options);
            
            _logger.LogInformation("Request processed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request processing failed");
            throw;
        }
    }
}
```

## Performance Considerations

### Efficient Logging Patterns

```csharp
public class PerformantLoggingExample
{
    private readonly ILogger<PerformantLoggingExample> _logger;

    public PerformantLoggingExample(ILogger<PerformantLoggingExample> logger)
    {
        _logger = logger;
    }

    public void EfficientLogging()
    {
        // ✅ Good: Check if logging is enabled before expensive operations
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var expensiveData = GenerateExpensiveDebugData();
            _logger.LogDebug("Debug data: {Data}", expensiveData);
        }

        // ✅ Good: Use structured logging with parameters
        _logger.LogInformation("Processing request for model {ModelId} with {MessageCount} messages", 
            "gpt-4", 5);

        // ❌ Avoid: String concatenation in log messages
        // _logger.LogInformation($"Processing request for model {modelId} with {messageCount} messages");
    }

    private string GenerateExpensiveDebugData()
    {
        // Simulate expensive operation
        return "Complex debug information";
    }
}
```

### High-Performance Logging Configuration

```csharp
// For high-throughput scenarios
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .AddConsole(options =>
        {
            options.IncludeScopes = false; // Reduces overhead
            options.SingleLine = true;     // Faster formatting
        })
        .SetMinimumLevel(LogLevel.Information) // Reduce log volume
        .AddFilter("AchieveAi.LmDotnetTools.LmCore.Middleware.FunctionCallMiddleware", LogLevel.Warning); // Reduce chatty components
});
```

### Async Logging with Serilog

```csharp
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Async(a => a.Console()) // Async console writing
    .WriteTo.Async(a => a.File("logs/app-.txt", rollingInterval: RollingInterval.Day))
    .CreateLogger();
```

## Best Practices Summary

1. **Use structured logging** with named parameters instead of string concatenation
2. **Check log levels** before expensive operations using `IsEnabled()`
3. **Configure appropriate log levels** for different environments
4. **Use scopes** to add context to related log entries
5. **Avoid logging sensitive information** like API keys or personal data
6. **Use async logging providers** for high-throughput scenarios
7. **Configure log rotation** to prevent disk space issues
8. **Test logging configuration** in different environments
9. **Monitor log volume** and adjust levels as needed
10. **Use correlation IDs** to trace requests across components

## Troubleshooting

### Common Issues

1. **No logs appearing**: Check minimum log level configuration
2. **Too many logs**: Adjust log levels for specific categories
3. **Performance issues**: Use async providers and check for expensive logging operations
4. **Missing structured data**: Ensure you're using named parameters in log messages
5. **Logs not formatted correctly**: Verify output template configuration

### Debug Logging Configuration

```csharp
// Add this to see what's happening with logging configuration
builder.Logging.AddDebug();
builder.Logging.AddEventLog(); // Windows only

// Or use this to see internal logging framework messages
builder.Logging.AddFilter("Microsoft.Extensions.Logging", LogLevel.Debug);
```