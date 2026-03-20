using AchieveAi.LmDotnetTools.LmEmbeddings.Core;
using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Configuration;

/// <summary>
/// Extension methods for registering failover embedding and reranking services with dependency injection.
/// </summary>
public static class FailoverServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="FailoverEmbeddingService"/> as <see cref="IEmbeddingService"/>
    /// using configuration from an <see cref="IConfigurationSection"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="section">Configuration section containing Primary, Backup, and failover settings.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddFailoverEmbeddings(configuration.GetSection("Embeddings:Failover"));
    /// </code>
    /// </example>
    public static IServiceCollection AddFailoverEmbeddings(
        this IServiceCollection services,
        IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        var config = section.Get<FailoverEmbeddingConfiguration>()
            ?? throw new InvalidOperationException(
                $"Failed to bind configuration section '{section.Path}' to {nameof(FailoverEmbeddingConfiguration)}.");

        ValidateEmbeddingConfig(config, section.Path);

        _ = services.AddSingleton<IEmbeddingService>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();

            var primary = new ServerEmbeddings(
                endpoint: config.Primary.Endpoint,
                model: config.Primary.Model,
                embeddingSize: config.Primary.EmbeddingSize,
                apiKey: config.Primary.ApiKey,
                logger: loggerFactory?.CreateLogger<ServerEmbeddings>());

            var backup = new ServerEmbeddings(
                endpoint: config.Backup.Endpoint,
                model: config.Backup.Model,
                embeddingSize: config.Backup.EmbeddingSize,
                apiKey: config.Backup.ApiKey,
                logger: loggerFactory?.CreateLogger<ServerEmbeddings>());

            var options = BuildOptions(config.PrimaryRequestTimeoutSeconds, config.FailoverOnHttpError, config.RecoveryIntervalSeconds);

            return new FailoverEmbeddingService(
                primary,
                backup,
                options,
                loggerFactory?.CreateLogger<FailoverEmbeddingService>());
        });

        return services;
    }

    /// <summary>
    /// Registers a <see cref="FailoverEmbeddingService"/> as <see cref="IEmbeddingService"/>
    /// using pre-built service instances.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="primary">The primary embedding service.</param>
    /// <param name="backup">The backup embedding service.</param>
    /// <param name="options">Failover configuration options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddFailoverEmbeddings(
        this IServiceCollection services,
        IEmbeddingService primary,
        IEmbeddingService backup,
        FailoverOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(options);

        _ = services.AddSingleton<IEmbeddingService>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new FailoverEmbeddingService(
                primary,
                backup,
                options,
                loggerFactory?.CreateLogger<FailoverEmbeddingService>());
        });

        return services;
    }

    /// <summary>
    /// Registers a <see cref="FailoverRerankService"/> as <see cref="IRerankService"/>
    /// using configuration from an <see cref="IConfigurationSection"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="section">Configuration section containing Primary, Backup, and failover settings.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddFailoverReranking(configuration.GetSection("Reranking:Failover"));
    /// </code>
    /// </example>
    public static IServiceCollection AddFailoverReranking(
        this IServiceCollection services,
        IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        var config = section.Get<FailoverRerankConfiguration>()
            ?? throw new InvalidOperationException(
                $"Failed to bind configuration section '{section.Path}' to {nameof(FailoverRerankConfiguration)}.");

        ValidateRerankConfig(config, section.Path);

        _ = services.AddSingleton<IRerankService>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();

            var primary = new RerankingService(
                endpoint: config.Primary.Endpoint,
                model: config.Primary.Model,
                apiKey: config.Primary.ApiKey,
                logger: loggerFactory?.CreateLogger<RerankingService>());

            var backup = new RerankingService(
                endpoint: config.Backup.Endpoint,
                model: config.Backup.Model,
                apiKey: config.Backup.ApiKey,
                logger: loggerFactory?.CreateLogger<RerankingService>());

            var options = BuildOptions(config.PrimaryRequestTimeoutSeconds, config.FailoverOnHttpError, config.RecoveryIntervalSeconds);

            return new FailoverRerankService(
                primary,
                backup,
                options,
                loggerFactory?.CreateLogger<FailoverRerankService>());
        });

        return services;
    }

    /// <summary>
    /// Registers a <see cref="FailoverRerankService"/> as <see cref="IRerankService"/>
    /// using pre-built service instances.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="primary">The primary reranking service.</param>
    /// <param name="backup">The backup reranking service.</param>
    /// <param name="options">Failover configuration options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddFailoverReranking(
        this IServiceCollection services,
        IRerankService primary,
        IRerankService backup,
        FailoverOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(options);

        _ = services.AddSingleton<IRerankService>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new FailoverRerankService(
                primary,
                backup,
                options,
                loggerFactory?.CreateLogger<FailoverRerankService>());
        });

        return services;
    }

    private static FailoverOptions BuildOptions(
        double timeoutSeconds, bool failoverOnHttpError, double? recoveryIntervalSeconds)
    {
        return new FailoverOptions
        {
            PrimaryRequestTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            FailoverOnHttpError = failoverOnHttpError,
            RecoveryInterval = recoveryIntervalSeconds.HasValue
                ? TimeSpan.FromSeconds(recoveryIntervalSeconds.Value)
                : null
        };
    }

    private static void ValidateEndpoint(
        string endpoint, string apiKey, string model,
        string endpointLabel, string sectionPath)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException($"'{sectionPath}:{endpointLabel}:Endpoint' is required.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"'{sectionPath}:{endpointLabel}:ApiKey' is required.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException($"'{sectionPath}:{endpointLabel}:Model' is required.");
        }
    }

    private static void ValidateTimeout(double timeoutSeconds, string sectionPath)
    {
        if (timeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"'{sectionPath}:PrimaryRequestTimeoutSeconds' must be greater than 0.");
        }
    }

    private static void ValidateRecoveryInterval(double? recoveryIntervalSeconds, string sectionPath)
    {
        if (recoveryIntervalSeconds.HasValue && recoveryIntervalSeconds.Value <= 0)
        {
            throw new InvalidOperationException(
                $"'{sectionPath}:RecoveryIntervalSeconds' must be greater than 0 when specified.");
        }
    }

    private static void ValidateEmbeddingConfig(FailoverEmbeddingConfiguration config, string sectionPath)
    {
        ValidateEndpoint(config.Primary.Endpoint, config.Primary.ApiKey, config.Primary.Model, "Primary", sectionPath);
        ValidateEndpoint(config.Backup.Endpoint, config.Backup.ApiKey, config.Backup.Model, "Backup", sectionPath);
        ValidateTimeout(config.PrimaryRequestTimeoutSeconds, sectionPath);
        ValidateRecoveryInterval(config.RecoveryIntervalSeconds, sectionPath);

        if (config.Primary.EmbeddingSize != config.Backup.EmbeddingSize)
        {
            throw new InvalidOperationException(
                $"Primary and backup embedding sizes must match. " +
                $"Primary: {config.Primary.EmbeddingSize}, Backup: {config.Backup.EmbeddingSize}. " +
                $"Section: '{sectionPath}'.");
        }
    }

    private static void ValidateRerankConfig(FailoverRerankConfiguration config, string sectionPath)
    {
        ValidateEndpoint(config.Primary.Endpoint, config.Primary.ApiKey, config.Primary.Model, "Primary", sectionPath);
        ValidateEndpoint(config.Backup.Endpoint, config.Backup.ApiKey, config.Backup.Model, "Backup", sectionPath);
        ValidateTimeout(config.PrimaryRequestTimeoutSeconds, sectionPath);
        ValidateRecoveryInterval(config.RecoveryIntervalSeconds, sectionPath);
    }
}
