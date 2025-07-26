using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Extensions.Logging;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.ModelConfigGenerator.Configuration;
using AchieveAi.LmDotnetTools.ModelConfigGenerator.Services;

namespace AchieveAi.LmDotnetTools.ModelConfigGenerator;

/// <summary>
/// Console application for generating Models.config files from OpenRouter data.
/// </summary>
public class Program
{
  public static async Task<int> Main(string[] args)
  {
    try
    {
      var options = ParseArguments(args);
      if (options == null) return 1;

      // Special handling for list families
      if (args.Contains("--list-families"))
      {
        Console.WriteLine("Supported model families:");
        foreach (var family in ModelConfigGeneratorService.GetSupportedFamilies().OrderBy(f => f))
        {
          Console.WriteLine($"  {family}");
        }
        return 0;
      }

      // Setup NLog and logging
      var logger = LogManager.GetCurrentClassLogger();
      var logLevel = options.Verbose ? Microsoft.Extensions.Logging.LogLevel.Debug : Microsoft.Extensions.Logging.LogLevel.Information;
      
      try
      {
        logger.Info("Starting ModelConfigGenerator with options: {@Options}", new 
        {
          options.OutputPath,
          options.ModelFamilies,
          options.Verbose,
          options.MaxModels,
          options.ReasoningOnly,
          options.MultimodalOnly,
          options.MinContextLength,
          options.MaxCostPerMillion
        });

        // Create host and services with NLog
        var host = Host.CreateDefaultBuilder()
          .ConfigureLogging(logging =>
          {
            logging.ClearProviders();
            logging.SetMinimumLevel(logLevel);
            logging.AddNLog("nlog.config");
          })
          .ConfigureServices(services =>
          {
            services.AddHttpClient();
            services.AddTransient<OpenRouterModelService>(provider =>
            {
              var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
              var httpClient = httpClientFactory.CreateClient();
              var serviceLogger = provider.GetRequiredService<ILogger<OpenRouterModelService>>();
              return new OpenRouterModelService(httpClient, serviceLogger);
            });
            services.AddTransient<ModelConfigGeneratorService>();
          })
          .Build();

        var generator = host.Services.GetRequiredService<ModelConfigGeneratorService>();
        var success = await generator.GenerateConfigAsync(options);
        
        if (success)
        {
          logger.Info("Successfully generated model configuration at {OutputPath}", options.OutputPath);
        }
        else
        {
          logger.Error("Failed to generate model configuration");
        }
        
        return success ? 0 : 1;
      }
      catch (Exception ex)
      {
        logger.Error(ex, "Fatal error during model configuration generation");
        return 1;
      }
      finally
      {
        LogManager.Shutdown();
      }
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"Fatal error: {ex.Message}");
      if (args.Contains("--verbose"))
      {
        Console.Error.WriteLine(ex.StackTrace);
      }
      return 1;
    }
  }

  private static GeneratorOptions? ParseArguments(string[] args)
  {
    var options = new GeneratorOptions();
    var families = new List<string>();

    for (int i = 0; i < args.Length; i++)
    {
      switch (args[i].ToLowerInvariant())
      {
        case "--help" or "-h":
          ShowHelp();
          return null;

        case "--output" or "-o":
          if (i + 1 >= args.Length)
          {
            Console.Error.WriteLine("Error: --output requires a value");
            return null;
          }
          options = options with { OutputPath = args[++i] };
          break;

        case "--families" or "-f":
          if (i + 1 >= args.Length)
          {
            Console.Error.WriteLine("Error: --families requires a value");
            return null;
          }
          families.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries));
          break;

        case "--verbose" or "-v":
          options = options with { Verbose = true };
          break;

        case "--max-models" or "-m":
          if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var maxModels) || maxModels < 0)
          {
            Console.Error.WriteLine("Error: --max-models requires a non-negative integer");
            return null;
          }
          options = options with { MaxModels = maxModels };
          i++;
          break;

        case "--reasoning-only" or "-r":
          options = options with { ReasoningOnly = true };
          break;

        case "--multimodal-only":
          options = options with { MultimodalOnly = true };
          break;

        case "--min-context":
          if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var minContext) || minContext < 0)
          {
            Console.Error.WriteLine("Error: --min-context requires a non-negative integer");
            return null;
          }
          options = options with { MinContextLength = minContext };
          i++;
          break;

        case "--max-cost":
          if (i + 1 >= args.Length || !decimal.TryParse(args[i + 1], out var maxCost) || maxCost < 0)
          {
            Console.Error.WriteLine("Error: --max-cost requires a non-negative decimal");
            return null;
          }
          options = options with { MaxCostPerMillion = maxCost };
          i++;
          break;

        case "--no-capabilities":
          options = options with { IncludeCapabilities = false };
          break;

        case "--compact":
          options = options with { FormatJson = false };
          break;

        case "--list-families":
          // Handled in main method
          break;

        default:
          Console.Error.WriteLine($"Error: Unknown option '{args[i]}'");
          return null;
      }
    }

    // Validate mutually exclusive options
    if (options.ReasoningOnly && options.MultimodalOnly)
    {
      Console.Error.WriteLine("Error: Cannot specify both --reasoning-only and --multimodal-only");
      return null;
    }

    options = options with { ModelFamilies = families };
    return options;
  }

  private static void ShowHelp()
  {
    Console.WriteLine("ModelConfigGenerator - Generate Models.config files from OpenRouter API");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  ModelConfigGenerator [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --output, -o <path>       Output file path (default: Models.config)");
    Console.WriteLine("  --families, -f <families> Comma-separated model families (e.g., llama,claude,gpt)");
    Console.WriteLine("  --verbose, -v             Enable verbose logging");
    Console.WriteLine("  --max-models, -m <count>  Maximum number of models to include");
    Console.WriteLine("  --reasoning-only, -r      Include only reasoning models");
    Console.WriteLine("  --multimodal-only         Include only multimodal models");
    Console.WriteLine("  --min-context <tokens>    Minimum context length required");
    Console.WriteLine("  --max-cost <cost>         Maximum cost per million tokens");
    Console.WriteLine("  --no-capabilities         Exclude detailed capabilities information");
    Console.WriteLine("  --compact                 Generate compact JSON without indentation");
    Console.WriteLine("  --list-families           List all supported model families");
    Console.WriteLine("  --help, -h                Show this help message");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  ModelConfigGenerator --output ./config/models.json --families llama,claude --verbose");
    Console.WriteLine("  ModelConfigGenerator --reasoning-only --max-models 10");
    Console.WriteLine("  ModelConfigGenerator --list-families");
  }
}
