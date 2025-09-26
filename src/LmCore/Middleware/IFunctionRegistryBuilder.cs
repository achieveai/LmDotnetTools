using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Configuration;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Base interface for the function registry builder.
/// Provides the foundation for building a function registry with fluent configuration.
/// </summary>
public interface IFunctionRegistryBuilder
{
    /// <summary>
    /// Adds a function provider to the registry.
    /// </summary>
    /// <param name="provider">The function provider to add</param>
    /// <returns>An interface with provider methods available</returns>
    IFunctionRegistryWithProviders AddProvider(IFunctionProvider provider);

    /// <summary>
    /// Adds a single function explicitly to the registry.
    /// </summary>
    /// <param name="contract">The function contract</param>
    /// <param name="handler">The function handler</param>
    /// <param name="providerName">Optional provider name for the function</param>
    /// <returns>The builder for method chaining</returns>
    IFunctionRegistryBuilder AddFunction(
        FunctionContract contract,
        Func<string, Task<string>> handler,
        string? providerName = null
    );

    /// <summary>
    /// Sets the logger for the registry.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <returns>The builder for method chaining</returns>
    IFunctionRegistryBuilder WithLogger(ILogger? logger);
}

/// <summary>
/// Interface for the registry builder after providers have been added.
/// Provides additional configuration options that become relevant once providers are present.
/// </summary>
public interface IFunctionRegistryWithProviders : IFunctionRegistryBuilder
{
    /// <summary>
    /// Configures the conflict resolution strategy for handling duplicate function names.
    /// </summary>
    /// <param name="strategy">The conflict resolution strategy to use</param>
    /// <returns>The builder for method chaining</returns>
    IFunctionRegistryWithProviders WithConflictResolution(ConflictResolution strategy);

    /// <summary>
    /// Sets a custom conflict resolution handler for complex resolution logic.
    /// </summary>
    /// <param name="handler">The custom conflict handler function</param>
    /// <returns>The builder for method chaining</returns>
    IFunctionRegistryWithProviders WithConflictHandler(
        Func<string, IEnumerable<FunctionDescriptor>, FunctionDescriptor> handler
    );

    /// <summary>
    /// Configures function filtering for the registry.
    /// </summary>
    /// <param name="filterConfig">The filter configuration</param>
    /// <returns>A configured registry ready for building</returns>
    IConfiguredFunctionRegistry WithFilterConfig(FunctionFilterConfig? filterConfig);

    /// <summary>
    /// Proceeds to build without additional filtering configuration.
    /// </summary>
    /// <returns>A configured registry ready for building</returns>
    IConfiguredFunctionRegistry Configure();
}

/// <summary>
/// Interface for a fully configured function registry ready to be built.
/// Represents the final stage of the builder pattern.
/// </summary>
public interface IConfiguredFunctionRegistry
{
    /// <summary>
    /// Builds the final function collections from the configured registry.
    /// </summary>
    /// <returns>A tuple containing the function contracts and their handlers</returns>
    (IEnumerable<FunctionContract>, IDictionary<string, Func<string, Task<string>>>) Build();

    /// <summary>
    /// Builds and creates a FunctionCallMiddleware instance directly.
    /// </summary>
    /// <param name="name">Optional name for the middleware</param>
    /// <param name="logger">Optional logger for the middleware</param>
    /// <param name="resultCallback">Optional callback for tool results</param>
    /// <returns>A configured FunctionCallMiddleware instance</returns>
    FunctionCallMiddleware BuildMiddleware(
        string? name = null,
        ILogger<FunctionCallMiddleware>? logger = null,
        IToolResultCallback? resultCallback = null
    );

    /// <summary>
    /// Generates comprehensive markdown documentation for all registered functions.
    /// </summary>
    /// <returns>Markdown-formatted documentation string</returns>
    string GetMarkdownDocumentation();

    /// <summary>
    /// Gets a read-only list of all registered providers for inspection.
    /// </summary>
    /// <returns>Read-only collection of function providers</returns>
    IReadOnlyList<IFunctionProvider> GetProviders();

    /// <summary>
    /// Validates the current configuration and returns any issues found.
    /// </summary>
    /// <returns>A collection of validation issues, empty if configuration is valid</returns>
    IEnumerable<string> ValidateConfiguration();
}
