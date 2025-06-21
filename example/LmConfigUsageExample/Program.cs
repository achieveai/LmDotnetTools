using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace LmConfigUsageExample;

class Program
{
  static async Task Main(string[] args)
  {
    Console.WriteLine("=== LmConfig Enhanced Usage Examples ===\\n");

    RunFileBasedExample();
    RunEmbeddedResourceExample();
    RunStreamFactoryExample();
    RunIOptionsExample();
    await RunProviderAvailabilityExample();
    await RunModelIdResolutionExample();
  }

  /// <summary>
  /// Example 1: Traditional file-based configuration loading
  /// </summary>
  static void RunFileBasedExample()
  {
    Console.WriteLine("1. File-Based Configuration Loading");
    Console.WriteLine("==================================");

    try
    {
      var services = new ServiceCollection();
      services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

      // Create configuration from models.json file
      var configuration = new ConfigurationBuilder()
        .AddJsonFile("models.json", optional: false, reloadOnChange: false)
        .Build();

      // Add LmConfig with file-based configuration
      services.AddLmConfig(configuration);

      var serviceProvider = services.BuildServiceProvider();
      var agent = serviceProvider.GetRequiredService<IAgent>();

      Console.WriteLine("✓ Successfully loaded configuration from file");
      Console.WriteLine($"Agent type: {agent.GetType().Name}\n");
    }
    catch (Exception ex)
    {
      Console.WriteLine($"✗ File-based loading failed: {ex.Message}\n");
    }
  }

  /// <summary>
  /// Example 2: Embedded resource configuration loading
  /// </summary>
  static void RunEmbeddedResourceExample()
  {
    Console.WriteLine("2. Embedded Resource Configuration Loading");
    Console.WriteLine("==========================================");

    try
    {
      var services = new ServiceCollection();
      services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

      // Try to load from embedded resource (will fallback to file if not embedded)
      try
      {
        services.AddLmConfigFromEmbeddedResource("models.json");
        Console.WriteLine("✓ Successfully loaded configuration from embedded resource");
      }
      catch (InvalidOperationException)
      {
        // Fallback to file-based loading if embedded resource not found
        Console.WriteLine("! Embedded resource not found, falling back to file-based loading");
        
        var configuration = new ConfigurationBuilder()
          .AddJsonFile("models.json", optional: false, reloadOnChange: false)
          .Build();
        
        services.AddLmConfig(configuration);
        Console.WriteLine("✓ Successfully loaded configuration from file as fallback");
      }

      var serviceProvider = services.BuildServiceProvider();
      var agent = serviceProvider.GetRequiredService<IAgent>();

      Console.WriteLine($"Agent type: {agent.GetType().Name}\n");
    }
    catch (Exception ex)
    {
      Console.WriteLine($"✗ Embedded resource loading failed: {ex.Message}\n");
    }
  }

  /// <summary>
  /// Example 3: Stream factory configuration loading
  /// </summary>
  static void RunStreamFactoryExample()
  {
    Console.WriteLine("3. Stream Factory Configuration Loading");
    Console.WriteLine("=======================================");

    try
    {
      var services = new ServiceCollection();
      services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

             // Load configuration using stream factory
       services.AddLmConfigFromStream(() =>
       {
         // This could be from any source: HTTP, database, memory, etc.
         var configPath = Path.Combine("..", "..", "src", "LmConfig", "docs", "models.json");
         var configJson = File.ReadAllText(configPath);
         return new MemoryStream(Encoding.UTF8.GetBytes(configJson));
       });

      var serviceProvider = services.BuildServiceProvider();
      var agent = serviceProvider.GetRequiredService<IAgent>();

      Console.WriteLine("✓ Successfully loaded configuration from stream factory");
      Console.WriteLine($"Agent type: {agent.GetType().Name}\n");
    }
    catch (Exception ex)
    {
      Console.WriteLine($"✗ Stream factory loading failed: {ex.Message}\n");
    }
  }

  /// <summary>
  /// Example 4: IOptions pattern configuration loading
  /// </summary>
  static void RunIOptionsExample()
  {
    Console.WriteLine("4. IOptions Pattern Configuration Loading");
    Console.WriteLine("=========================================");

    try
    {
      var services = new ServiceCollection();
      services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

      // Create configuration using .NET's configuration system
      var configuration = new ConfigurationBuilder()
        .AddJsonFile("models.json", optional: false, reloadOnChange: true)
        .Build();

      // Use IOptions pattern with configuration section
      services.AddLmConfigWithOptions(configuration.GetSection(""));

      var serviceProvider = services.BuildServiceProvider();
      var agent = serviceProvider.GetRequiredService<IAgent>();

      Console.WriteLine("✓ Successfully loaded configuration using IOptions pattern");
      Console.WriteLine($"Agent type: {agent.GetType().Name}\n");
    }
    catch (Exception ex)
    {
      Console.WriteLine($"✗ IOptions loading failed: {ex.Message}\n");
    }
  }

  /// <summary>
  /// Example 5: Provider availability checking
  /// </summary>
  static async Task RunProviderAvailabilityExample()
  {
    Console.WriteLine("5. Provider Availability Checking");
    Console.WriteLine("==================================");

    try
    {
      var services = new ServiceCollection();
      services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

      var configuration = new ConfigurationBuilder()
        .AddJsonFile("models.json", optional: false, reloadOnChange: false)
        .Build();

      services.AddLmConfig(configuration);

      var serviceProvider = services.BuildServiceProvider();
      var modelResolver = serviceProvider.GetRequiredService<IModelResolver>();

      // Check which providers are actually available (have API keys)
      var providerNames = new[] { "OpenAI", "Anthropic", "OpenRouter", "Groq", "DeepSeek" };
      
      Console.WriteLine("Provider availability status:");
      foreach (var providerName in providerNames)
      {
        var isAvailable = await modelResolver.IsProviderAvailableAsync(providerName);
        var status = isAvailable ? "✓ Available" : "✗ Not available (missing API key or config)";
        Console.WriteLine($"  {providerName}: {status}");
      }

      Console.WriteLine();

      // Try to resolve a model and show which providers would be used
      var testModel = "gpt-4.1-mini";
      Console.WriteLine($"Attempting to resolve providers for model '{testModel}':");
      
             try
       {
         var resolution = await modelResolver.ResolveProviderAsync(testModel);
         if (resolution != null)
         {
           Console.WriteLine($"✓ Resolved to provider: {resolution.EffectiveProviderName}");
           Console.WriteLine($"  Model: {resolution.EffectiveModelName}");
           Console.WriteLine($"  Endpoint: {resolution.Connection.EndpointUrl}");
         }
         else
         {
           Console.WriteLine($"✗ No available providers found for model '{testModel}'");
         }
       }
      catch (Exception ex)
      {
        Console.WriteLine($"✗ Model resolution failed: {ex.Message}");
      }

      Console.WriteLine();
    }
    catch (Exception ex)
    {
      Console.WriteLine($"✗ Provider availability check failed: {ex.Message}\n");
    }
  }

  /// <summary>
  /// Example 6: ModelId Resolution and Translation
  /// </summary>
  static async Task RunModelIdResolutionExample()
  {
    Console.WriteLine("6. ModelId Resolution and Translation");
    Console.WriteLine("=====================================");

    try
    {
      var services = new ServiceCollection();
      services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

             var configuration = new ConfigurationBuilder()
         .AddJsonFile("models.json", optional: false, reloadOnChange: false)
         .Build();

       services.AddLmConfig(configuration);

      var serviceProvider = services.BuildServiceProvider();
      var unifiedAgent = serviceProvider.GetRequiredService<UnifiedAgent>();

      Console.WriteLine("Demonstrating ModelId translation from logical to provider-specific names:");
      Console.WriteLine();

      // Example 1: GPT-4.1 -> OpenRouter translation
      var gptOptions = new GenerateReplyOptions
      {
        ModelId = "gpt-4.1", // User specifies logical model ID
        Temperature = 0.7f
      };

      try
      {
        var gptResolution = await unifiedAgent.GetProviderResolutionAsync(gptOptions);
        Console.WriteLine("Example 1: GPT-4.1 Resolution");
        Console.WriteLine($"  User requested: {gptOptions.ModelId}");
        Console.WriteLine($"  Resolved provider: {gptResolution.EffectiveProviderName}");
        Console.WriteLine($"  Provider model name: {gptResolution.EffectiveModelName}");
        Console.WriteLine($"  ✓ UnifiedAgent will translate '{gptOptions.ModelId}' to '{gptResolution.EffectiveModelName}' when calling the provider");
        Console.WriteLine();
      }
      catch (Exception ex)
      {
        Console.WriteLine($"  ✗ GPT-4.1 resolution failed: {ex.Message}");
        Console.WriteLine();
      }

      // Example 2: Claude -> OpenRouter translation
      var claudeOptions = new GenerateReplyOptions
      {
        ModelId = "claude-3-sonnet",
        Temperature = 0.5f
      };

      try
      {
        var claudeResolution = await unifiedAgent.GetProviderResolutionAsync(claudeOptions);
        Console.WriteLine("Example 2: Claude-3-Sonnet Resolution");
        Console.WriteLine($"  User requested: {claudeOptions.ModelId}");
        Console.WriteLine($"  Resolved provider: {claudeResolution.EffectiveProviderName}");
        Console.WriteLine($"  Provider model name: {claudeResolution.EffectiveModelName}");
        Console.WriteLine($"  ✓ UnifiedAgent will translate '{claudeOptions.ModelId}' to '{claudeResolution.EffectiveModelName}' when calling the provider");
        Console.WriteLine();
      }
      catch (Exception ex)
      {
        Console.WriteLine($"  ✗ Claude resolution failed: {ex.Message}");
        Console.WriteLine();
      }

      // Example 3: DeepSeek -> Provider-specific name
      var deepSeekOptions = new GenerateReplyOptions
      {
        ModelId = "deepseek-r1"
      };

      try
      {
        var deepSeekResolution = await unifiedAgent.GetProviderResolutionAsync(deepSeekOptions);
        Console.WriteLine("Example 3: DeepSeek-R1 Resolution");
        Console.WriteLine($"  User requested: {deepSeekOptions.ModelId}");
        Console.WriteLine($"  Resolved provider: {deepSeekResolution.EffectiveProviderName}");
        Console.WriteLine($"  Provider model name: {deepSeekResolution.EffectiveModelName}");
        Console.WriteLine($"  ✓ UnifiedAgent will translate '{deepSeekOptions.ModelId}' to '{deepSeekResolution.EffectiveModelName}' when calling the provider");
        Console.WriteLine();
      }
      catch (Exception ex)
      {
        Console.WriteLine($"  ✗ DeepSeek resolution failed: {ex.Message}");
        Console.WriteLine();
      }

      Console.WriteLine("Key Benefits:");
      Console.WriteLine("  • Users can use logical model names like 'gpt-4.1' or 'claude-3-sonnet'");
      Console.WriteLine("  • UnifiedAgent automatically resolves to the best available provider");
      Console.WriteLine("  • Provider agents receive the correct provider-specific model names");
      Console.WriteLine("  • Supports complex routing through aggregators like OpenRouter");
      Console.WriteLine("  • Enables seamless model switching without code changes");
      Console.WriteLine();
    }
    catch (Exception ex)
    {
      Console.WriteLine($"✗ ModelId resolution example failed: {ex.Message}\n");
    }
  }
} 