using System.Reflection;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Http;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AchieveAi.LmDotnetTools.LmConfig.Services;

/// <summary>
/// Extension methods for registering LmConfig services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds LmConfig services to the service collection, including the unified agent system.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The configuration containing model and provider settings.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddLmConfig(this IServiceCollection services, IConfiguration configuration)
    {
        return services.AddLmConfig(configuration.GetSection("LmConfig"));
    }

    /// <summary>
    /// Adds LmConfig services to the service collection, including the unified agent system.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configurationSection">The configuration section containing model and provider settings.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddLmConfig(
        this IServiceCollection services,
        IConfigurationSection configurationSection
    )
    {
        // Configure AppConfig from configuration
        _ = services.Configure<AppConfig>(configurationSection);

        // Register core services using shared helper
        return RegisterLmConfigServices(services, registerAsDefaultAgent: true);
    }

    /// <summary>
    /// Adds LmConfig services with a pre-configured AppConfig instance.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="appConfig">The pre-configured AppConfig instance.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddLmConfig(this IServiceCollection services, AppConfig appConfig)
    {
        ArgumentNullException.ThrowIfNull(appConfig);

        // Configure AppConfig as singleton
        _ = services.AddSingleton(Options.Create(appConfig));

        // Register core services using shared helper
        return RegisterLmConfigServices(services, registerAsDefaultAgent: true);
    }

    /// <summary>
    /// Adds LmConfig services by loading configuration from a JSON file.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configFilePath">Path to the JSON configuration file.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddLmConfigFromFile(this IServiceCollection services, string configFilePath)
    {
        ValidateStringParameter(configFilePath, nameof(configFilePath));

        if (!File.Exists(configFilePath))
        {
            throw new FileNotFoundException($"Configuration file not found: {configFilePath}");
        }

        // If the JSON has a root object matching AppConfig (e.g., models[] at root), deserialize directly.
        try
        {
            var json = File.ReadAllText(configFilePath);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };

            var appConfig = JsonSerializer.Deserialize<AppConfig>(json, options);
            if (appConfig != null && appConfig.Models != null && appConfig.Models.Any())
            {
                return services.AddLmConfig(appConfig);
            }
        }
        catch (JsonException)
        {
            // fall back to configuration section path below
        }

        // Otherwise treat file as standard configuration with "LmConfig" section
        var configBuilder = new ConfigurationBuilder().AddJsonFile(
            configFilePath,
            optional: false,
            reloadOnChange: true
        );

        var configuration = configBuilder.Build();

        return services.AddLmConfig(configuration);
    }

    /// <summary>
    /// Adds LmConfig services with advanced configuration options.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configureOptions">Action to configure advanced options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddLmConfig(
        this IServiceCollection services,
        Action<LmConfigOptions> configureOptions
    )
    {
        var options = new LmConfigOptions();
        configureOptions(options);

        // Configure AppConfig from options
        if (options.AppConfig != null)
        {
            _ = services.AddSingleton(Options.Create(options.AppConfig));
        }
        else if (options.ConfigurationSection != null)
        {
            _ = services.Configure<AppConfig>(options.ConfigurationSection);
        }
        else
        {
            throw new InvalidOperationException(
                "Either AppConfig or ConfigurationSection must be specified in LmConfigOptions"
            );
        }

        // Register core services using shared helper
        _ = RegisterLmConfigServices(services, options.RegisterAsDefaultAgent);

        // Configure HTTP clients for providers if specified
        if (options.ConfigureHttpClients != null)
        {
            options.ConfigureHttpClients(services);
        }

        return services;
    }

    /// <summary>
    /// Adds LmConfig services by loading configuration from an embedded resource.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="resourceName">Name of the embedded resource (e.g., "models.json").</param>
    /// <param name="assembly">Assembly containing the embedded resource. If null, uses calling assembly.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddLmConfigFromEmbeddedResource(
        this IServiceCollection services,
        string resourceName,
        Assembly? assembly = null
    )
    {
        ValidateStringParameter(resourceName, nameof(resourceName));

        assembly ??= Assembly.GetCallingAssembly();

        var appConfig = LoadConfigFromEmbeddedResource(resourceName, assembly);
        return services.AddLmConfig(appConfig);
    }

    /// <summary>
    /// Adds LmConfig services by loading configuration from a stream factory.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="streamFactory">Factory function that provides the configuration stream.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddLmConfigFromStream(this IServiceCollection services, Func<Stream> streamFactory)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);

        var appConfig = LoadConfigFromStream(streamFactory);
        return services.AddLmConfig(appConfig);
    }

    /// <summary>
    /// Adds LmConfig services by loading configuration from an async stream factory.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="streamFactory">Async factory function that provides the configuration stream.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddLmConfigFromStreamAsync(
        this IServiceCollection services,
        Func<Task<Stream>> streamFactory
    )
    {
        ArgumentNullException.ThrowIfNull(streamFactory);

        var appConfig = LoadConfigFromStreamAsync(streamFactory);
        return services.AddLmConfig(appConfig);
    }

    /// <summary>
    /// Shared method to register core LmConfig services, eliminating duplication.
    /// </summary>
    private static IServiceCollection RegisterLmConfigServices(IServiceCollection services, bool registerAsDefaultAgent)
    {
        // Register core services
        _ = services.AddSingleton<IModelResolver, ModelResolver>();
        _ = services.AddSingleton<IProviderAgentFactory, ProviderAgentFactory>();
        _ = services.AddSingleton<OpenRouterModelService>();
        // Ensure a single IHttpHandlerBuilder and attach the retry wrapper.
        var hbDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IHttpHandlerBuilder));

        if (hbDescriptor == null)
        {
            _ = services.AddSingleton<IHttpHandlerBuilder>(sp =>
            {
                var b = new HandlerBuilder();
                _ = b.Use(LmConfigStandardWrappers.WithRetry());
                return b;
            });
        }
        else
        {
            _ = services.Remove(hbDescriptor);
            _ = services.AddSingleton<IHttpHandlerBuilder>(sp =>
            {
                var inner =
                    (hbDescriptor.ImplementationInstance as HandlerBuilder)
                    ?? (hbDescriptor.ImplementationFactory?.Invoke(sp) as HandlerBuilder)
                    ?? new HandlerBuilder();

                _ = inner.Use(LmConfigStandardWrappers.WithRetry());
                return inner;
            });
        }

        // Register the unified agent
        _ = services.AddScoped<UnifiedAgent>();

        // Register as default agent if requested
        if (registerAsDefaultAgent)
        {
            _ = services.AddScoped<IAgent>(provider => provider.GetRequiredService<UnifiedAgent>());
            _ = services.AddScoped<IStreamingAgent>(provider => provider.GetRequiredService<UnifiedAgent>());
        }

        // Add HTTP client factory for provider connections
        _ = services.AddHttpClient();

        return services;
    }

    /// <summary>
    /// Shared validation for string parameters.
    /// </summary>
    private static void ValidateStringParameter(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} cannot be null or empty", parameterName);
        }
    }

    /// <summary>
    /// Loads configuration from async stream factory.
    /// </summary>
    private static AppConfig LoadConfigFromStreamAsync(Func<Task<Stream>> streamFactory)
    {
        try
        {
            using var stream = streamFactory().GetAwaiter().GetResult();
            return LoadConfigFromStreamInternal(stream);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load LmConfig from async stream: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Shared internal method for loading configuration from stream.
    /// </summary>
    private static AppConfig LoadConfigFromStreamInternal(Stream stream)
    {
        if (stream == null || stream.Length == 0)
        {
            throw new InvalidOperationException("Invalid or empty LmConfig stream");
        }

        try
        {
            var options = JsonSerializerOptionsFactory.CreateMinimal(namingPolicy: JsonNamingPolicy.CamelCase);
            options.PropertyNameCaseInsensitive = true;
            options.ReadCommentHandling = JsonCommentHandling.Skip;
            options.AllowTrailingCommas = true;

            var config = JsonSerializer.Deserialize<AppConfig>(stream, options);

            return config?.Models == null || !config.Models.Any()
                ? throw new InvalidOperationException("Configuration must contain at least one model")
                : config;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse LmConfig from stream: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Validates the LmConfig configuration and throws an exception if invalid.
    /// </summary>
    /// <param name="services">The service collection to validate.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown when configuration is invalid.</exception>
    public static IServiceCollection ValidateLmConfig(this IServiceCollection services)
    {
        // Build a temporary service provider to validate configuration
        using var provider = services.BuildServiceProvider();
        var modelResolver = provider.GetRequiredService<IModelResolver>();

        var validationTask = modelResolver.ValidateConfigurationAsync();
        var validation = validationTask.GetAwaiter().GetResult();

        if (!validation.IsValid)
        {
            var errors = string.Join(Environment.NewLine, validation.Errors);
            throw new InvalidOperationException($"LmConfig validation failed:{Environment.NewLine}{errors}");
        }

        if (validation.Warnings.Any())
        {
            var warnings = string.Join(Environment.NewLine, validation.Warnings);
            // Log warnings if logger is available, otherwise just continue
            Console.WriteLine($"LmConfig warnings:{Environment.NewLine}{warnings}");
        }

        return services;
    }

    #region Private Helper Methods

    private static AppConfig LoadConfigFromEmbeddedResource(string resourceName, Assembly assembly)
    {
        // Try to find the resource with various naming patterns
        var resourceNames = new[]
        {
            resourceName,
            $"{assembly.GetName().Name}.{resourceName}",
            $"{assembly.GetName().Name}.Resources.{resourceName}",
            $"{assembly.GetName().Name}.Config.{resourceName}",
        };

        Stream? resourceStream = null;
        string? foundResourceName = null;

        foreach (var name in resourceNames)
        {
            resourceStream = assembly.GetManifestResourceStream(name);
            if (resourceStream != null)
            {
                foundResourceName = name;
                break;
            }
        }

        if (resourceStream == null)
        {
            var availableResources = assembly.GetManifestResourceNames();
            throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found in assembly '{assembly.GetName().Name}'. "
                    + $"Available resources: {string.Join(", ", availableResources)}"
            );
        }

        try
        {
            using var reader = new StreamReader(resourceStream);
            var json = reader.ReadToEnd();

            var options = JsonSerializerOptionsFactory.CreateMinimal(namingPolicy: JsonNamingPolicy.CamelCase);
            options.PropertyNameCaseInsensitive = true;
            options.AllowTrailingCommas = true;
            options.ReadCommentHandling = JsonCommentHandling.Skip;

            var config = JsonSerializer.Deserialize<AppConfig>(json, options);

            return config?.Models?.Any() != true
                ? throw new InvalidOperationException(
                    $"Invalid or empty LmConfig resource '{foundResourceName}'. "
                        + "The configuration must contain at least one model."
                )
                : config;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse LmConfig from embedded resource '{foundResourceName}': {ex.Message}",
                ex
            );
        }
        finally
        {
            resourceStream?.Dispose();
        }
    }

    private static AppConfig LoadConfigFromStream(Func<Stream> streamFactory)
    {
        using var stream = streamFactory();
        using var reader = new StreamReader(stream);

        var json = reader.ReadToEnd();

        try
        {
            var options = JsonSerializerOptionsFactory.CreateMinimal(namingPolicy: JsonNamingPolicy.CamelCase);
            options.PropertyNameCaseInsensitive = true;
            options.AllowTrailingCommas = true;
            options.ReadCommentHandling = JsonCommentHandling.Skip;

            var config = JsonSerializer.Deserialize<AppConfig>(json, options);

            return config?.Models?.Any() != true
                ? throw new InvalidOperationException(
                    "Invalid or empty LmConfig stream. The configuration must contain at least one model."
                )
                : config;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse LmConfig from stream: {ex.Message}", ex);
        }
    }

    #endregion
}

/// <summary>
/// Advanced configuration options for LmConfig services.
/// </summary>
public class LmConfigOptions
{
    /// <summary>
    /// Pre-configured AppConfig instance.
    /// </summary>
    public AppConfig? AppConfig { get; set; }

    /// <summary>
    /// Configuration section containing AppConfig settings.
    /// </summary>
    public IConfigurationSection? ConfigurationSection { get; set; }

    /// <summary>
    /// Whether to register UnifiedAgent as the default IAgent and IStreamingAgent.
    /// Default is true.
    /// </summary>
    public bool RegisterAsDefaultAgent { get; set; } = true;

    /// <summary>
    /// Optional action to configure HTTP clients for providers.
    /// </summary>
    public Action<IServiceCollection>? ConfigureHttpClients { get; set; }
}
