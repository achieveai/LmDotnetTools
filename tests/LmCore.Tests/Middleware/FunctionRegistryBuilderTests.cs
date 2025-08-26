using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Configuration;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class FunctionRegistryBuilderTests
{
    [Fact]
    public void FunctionRegistry_ImplementsAllBuilderInterfaces()
    {
        // Arrange
        var registry = new FunctionRegistry();

        // Act & Assert
        Assert.IsAssignableFrom<IFunctionRegistryBuilder>(registry);
        Assert.IsAssignableFrom<IFunctionRegistryWithProviders>(registry);
        Assert.IsAssignableFrom<IConfiguredFunctionRegistry>(registry);
    }

    [Fact]
    public void AddProvider_ReturnsIFunctionRegistryWithProviders()
    {
        // Arrange
        IFunctionRegistryBuilder builder = new FunctionRegistry();
        var provider = CreateTestProvider("TestProvider");

        // Act
        var result = builder.AddProvider(provider);

        // Assert
        Assert.IsAssignableFrom<IFunctionRegistryWithProviders>(result);
    }

    [Fact]
    public void AddFunction_ReturnsIFunctionRegistryBuilder()
    {
        // Arrange
        IFunctionRegistryBuilder builder = new FunctionRegistry();
        var contract = CreateTestContract("testFunc");
        var handler = CreateTestHandler("result");

        // Act
        var result = builder.AddFunction(contract, handler);

        // Assert
        Assert.IsAssignableFrom<IFunctionRegistryBuilder>(result);
    }

    [Fact]
    public void WithLogger_ReturnsIFunctionRegistryBuilder()
    {
        // Arrange
        IFunctionRegistryBuilder builder = new FunctionRegistry();
        var logger = NullLogger.Instance;

        // Act
        var result = builder.WithLogger(logger);

        // Assert
        Assert.IsAssignableFrom<IFunctionRegistryBuilder>(result);
    }

    [Fact]
    public void WithConflictResolution_ReturnsIFunctionRegistryWithProviders()
    {
        // Arrange
        IFunctionRegistryWithProviders builder = new FunctionRegistry().AddProvider(
            CreateTestProvider("Provider1")
        );

        // Act
        var result = builder.WithConflictResolution(ConflictResolution.TakeFirst);

        // Assert
        Assert.IsAssignableFrom<IFunctionRegistryWithProviders>(result);
    }

    [Fact]
    public void WithConflictHandler_ReturnsIFunctionRegistryWithProviders()
    {
        // Arrange
        IFunctionRegistryWithProviders builder = new FunctionRegistry().AddProvider(
            CreateTestProvider("Provider1")
        );

        // Act
        var result = builder.WithConflictHandler((key, candidates) => candidates.First());

        // Assert
        Assert.IsAssignableFrom<IFunctionRegistryWithProviders>(result);
    }

    [Fact]
    public void WithFilterConfig_ReturnsIConfiguredFunctionRegistry()
    {
        // Arrange
        IFunctionRegistryWithProviders builder = new FunctionRegistry().AddProvider(
            CreateTestProvider("Provider1")
        );
        var config = new FunctionFilterConfig { EnableFiltering = true };

        // Act
        var result = builder.WithFilterConfig(config);

        // Assert
        Assert.IsAssignableFrom<IConfiguredFunctionRegistry>(result);
    }

    [Fact]
    public void Configure_ReturnsIConfiguredFunctionRegistry()
    {
        // Arrange
        IFunctionRegistryWithProviders builder = new FunctionRegistry().AddProvider(
            CreateTestProvider("Provider1")
        );

        // Act
        var result = builder.Configure();

        // Assert
        Assert.IsAssignableFrom<IConfiguredFunctionRegistry>(result);
    }

    [Fact]
    public void FluentChaining_WorksCorrectly()
    {
        // Arrange
        var provider1 = CreateTestProvider("Provider1", new[] { "func1" });
        var provider2 = CreateTestProvider("Provider2", new[] { "func2" });
        var contract = CreateTestContract("func3");
        var handler = CreateTestHandler("result3");

        // Act - demonstrate fluent chaining through all interfaces
        var configured = new FunctionRegistry()
            .WithLogger(NullLogger.Instance) // IFunctionRegistryBuilder method
            .AddFunction(contract, handler) // IFunctionRegistryBuilder method
            .AddProvider(provider1) // Returns IFunctionRegistryWithProviders
            .AddProvider(provider2) // IFunctionRegistryWithProviders method
            .WithConflictResolution(ConflictResolution.TakeFirst) // IFunctionRegistryWithProviders method
            .WithFilterConfig(
                new FunctionFilterConfig // Returns IConfiguredFunctionRegistry
                {
                    EnableFiltering = false,
                }
            );

        // Assert
        Assert.IsAssignableFrom<IConfiguredFunctionRegistry>(configured);

        // Verify we can build
        var (contracts, handlers) = configured.Build();
        Assert.Equal(3, contracts.Count());
        Assert.Equal(3, handlers.Count);
    }

    [Fact]
    public void ValidateConfiguration_WithValidConfig_ReturnsNoIssues()
    {
        // Arrange
        var registry =
            new FunctionRegistry()
                .AddProvider(CreateTestProvider("valid_provider"))
                .WithFilterConfig(
                    new FunctionFilterConfig
                    {
                        EnableFiltering = true,
                        ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
                        {
                            ["valid_provider"] = new ProviderFilterConfig
                            {
                                CustomPrefix = "valid_prefix",
                            },
                        },
                    }
                ) as IConfiguredFunctionRegistry;

        // Act
        var issues = registry.ValidateConfiguration();

        // Assert
        Assert.Empty(issues);
    }

    [Fact]
    public void ValidateConfiguration_WithInvalidPrefix_ReturnsIssue()
    {
        // Arrange
        var registry = new FunctionRegistry();

        // Force set an invalid configuration (simulating deserialization)
        var filterConfig = new FunctionFilterConfig
        {
            EnableFiltering = true,
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>(),
        };

        var providerConfig = new ProviderFilterConfig();
        // Use reflection to bypass validation
        var field = typeof(ProviderFilterConfig).GetField(
            "_customPrefix",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );
        field?.SetValue(providerConfig, "invalid prefix"); // Invalid due to space

        filterConfig.ProviderConfigs["TestProvider"] = providerConfig;
        registry.WithFilterConfig(filterConfig);

        // Act
        var issues = registry.ValidateConfiguration();

        // Assert
        Assert.NotEmpty(issues);
        Assert.Contains("TestProvider", issues.First());
        Assert.Contains("invalid characters", issues.First());
    }

    [Fact]
    public void ValidateConfiguration_WithInvalidProviderName_WhenPrefixingAll_ReturnsIssue()
    {
        // Arrange
        var registry =
            new FunctionRegistry()
                .AddProvider(CreateTestProvider("invalid provider")) // Space makes it invalid
                .WithFilterConfig(
                    new FunctionFilterConfig
                    {
                        EnableFiltering = true,
                        UsePrefixOnlyForCollisions = false, // This means all functions get prefixed
                    }
                ) as IConfiguredFunctionRegistry;

        // Act
        var issues = registry.ValidateConfiguration();

        // Assert
        Assert.NotEmpty(issues);
        Assert.Contains("invalid provider", issues.First());
        Assert.Contains("not a valid prefix", issues.First());
    }

    [Fact]
    public void GetProviders_ReturnsAllRegisteredProviders()
    {
        // Arrange
        var provider1 = CreateTestProvider("Provider1");
        var provider2 = CreateTestProvider("Provider2");

        var registry =
            new FunctionRegistry().AddProvider(provider1).AddProvider(provider2).Configure()
            as IConfiguredFunctionRegistry;

        // Act
        var providers = registry.GetProviders();

        // Assert
        Assert.Equal(2, providers.Count);
        Assert.Contains(provider1, providers);
        Assert.Contains(provider2, providers);
    }

    [Fact]
    public void GetMarkdownDocumentation_ReturnsDocumentation()
    {
        // Arrange
        var registry =
            new FunctionRegistry()
                .AddProvider(CreateTestProvider("Provider1", new[] { "func1" }))
                .Configure() as IConfiguredFunctionRegistry;

        // Act
        var markdown = registry.GetMarkdownDocumentation();

        // Assert
        Assert.NotNull(markdown);
        Assert.Contains("# Function Registry Documentation", markdown);
        Assert.Contains("Provider1", markdown);
        Assert.Contains("func1", markdown);
    }

    [Fact]
    public void BuildMiddleware_CreatesMiddleware()
    {
        // Arrange
        var registry =
            new FunctionRegistry()
                .AddProvider(CreateTestProvider("Provider1", new[] { "func1" }))
                .Configure() as IConfiguredFunctionRegistry;

        // Act
        var middleware = registry.BuildMiddleware("TestMiddleware");

        // Assert
        Assert.NotNull(middleware);
        Assert.Equal("TestMiddleware", middleware.Name);
    }

    [Fact]
    public async Task CompleteFluentWorkflow_ProducesExpectedResults()
    {
        // This test demonstrates a complete workflow using the fluent interface

        // Arrange
        var provider1 = CreateTestProvider("MCP", new[] { "search", "list" });
        var provider2 = CreateTestProvider("Weather", new[] { "getCurrentWeather", "getForecast" });

        // Act - Use fluent interface to configure and build
        var registry =
            new FunctionRegistry()
                .WithLogger(NullLogger.Instance)
                .AddProvider(provider1)
                .AddProvider(provider2)
                .WithConflictResolution(ConflictResolution.TakeFirst)
                .WithFilterConfig(
                    new FunctionFilterConfig
                    {
                        EnableFiltering = true,
                        GlobalAllowedFunctions = new List<string> { "search", "getCurrentWeather" },
                        ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
                        {
                            ["Weather"] = new ProviderFilterConfig { CustomPrefix = "weather" },
                        },
                    }
                ) as IConfiguredFunctionRegistry;

        // Validate configuration
        var validationIssues = registry.ValidateConfiguration();
        Assert.Empty(validationIssues);

        // Build
        var (contracts, handlers) = registry.Build();

        // Assert - Only allowed functions should be present
        Assert.Equal(2, contracts.Count());
        Assert.Contains(contracts, c => c.Name == "search");
        Assert.Contains(contracts, c => c.Name == "getCurrentWeather");
        Assert.DoesNotContain(contracts, c => c.Name == "list");
        Assert.DoesNotContain(contracts, c => c.Name == "getForecast");

        // Test handlers work
        var searchResult = await handlers["search"]("{}");
        Assert.Equal("MCP-result", searchResult);

        var weatherResult = await handlers["getCurrentWeather"]("{}");
        Assert.Equal("Weather-result", weatherResult);
    }

    // Helper methods
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type
    private static IFunctionProvider CreateTestProvider(string name, string[] functionNames = null)
#pragma warning restore CS8625
    {
        return new TestFunctionProvider(name, functionNames ?? Array.Empty<string>());
    }

    private static FunctionContract CreateTestContract(string name)
    {
        return new FunctionContract
        {
            Name = name,
            Description = $"Test function {name}",
            Parameters = new List<FunctionParameterContract>(),
        };
    }

    private static Func<string, Task<string>> CreateTestHandler(string result)
    {
        return _ => Task.FromResult(result);
    }

    private class TestFunctionProvider : IFunctionProvider
    {
        private readonly string[] _functionNames;

        public TestFunctionProvider(string name, string[] functionNames)
        {
            ProviderName = name;
            _functionNames = functionNames;
        }

        public string ProviderName { get; }
        public int Priority => 100;

        public IEnumerable<FunctionDescriptor> GetFunctions()
        {
            return _functionNames.Select(name => new FunctionDescriptor
            {
                Contract = new FunctionContract
                {
                    Name = name,
                    Description = $"Test function {name}",
                    Parameters = new List<FunctionParameterContract>(),
                },
                Handler = _ => Task.FromResult($"{ProviderName}-result"),
                ProviderName = ProviderName,
            });
        }
    }
}
