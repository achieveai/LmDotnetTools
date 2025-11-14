using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.LmCore.Configuration;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

/// <summary>
/// Integration tests for FunctionRegistry with filtering and collision detection
/// </summary>
public class FunctionRegistryFilteringTests
{
    private readonly Mock<ILogger> _mockLogger;
    private static readonly string[] expectation = new[]
    {
        "read_file",
        "list_directory",
        "list_repos",
        "list_tables",
        "get_file",
    };
    private static readonly string[] expectationArray = new[] { "func1", "func2" };

    public FunctionRegistryFilteringTests()
    {
        _mockLogger = new Mock<ILogger>();
    }

    #region Helper Methods

    private class TestFunctionProvider : IFunctionProvider
    {
        private readonly string _providerName;
        private readonly List<FunctionDescriptor> _functions;

        public TestFunctionProvider(string providerName, params string[] functionNames)
        {
            _providerName = providerName;
            _functions = functionNames
                .Select(name => new FunctionDescriptor
                {
                    Contract = new FunctionContract
                    {
                        Name = name,
                        Description = $"Test function {name} from {providerName}",
                    },
                    Handler = _ => Task.FromResult($"Result from {name}"),
                    ProviderName = _providerName,
                })
                .ToList();
        }

        public string ProviderName => _providerName;
        public int Priority => 100;

        public IEnumerable<FunctionDescriptor> GetFunctions()
        {
            return _functions;
        }
    }

    private FunctionRegistry CreateRegistryWithProviders()
    {
        var registry = new FunctionRegistry().WithLogger(_mockLogger.Object);

        registry.AddProvider(
            new TestFunctionProvider("GitHub", "search_repositories", "create_issue", "get_file", "list_repos")
        );

        registry.AddProvider(
            new TestFunctionProvider("FileSystem", "read_file", "write_file", "list_directory", "search")
        );

        registry.AddProvider(
            new TestFunctionProvider("Database", "execute_query", "list_tables", "search", "create_table")
        );

        return registry;
    }

    #endregion

    #region Basic Integration Tests

    [Fact]
    public void Build_WithNoFilterConfig_IncludesAllFunctions()
    {
        // Arrange
        var registry = CreateRegistryWithProviders();

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        contracts.Should().HaveCount(12);
        handlers.Should().HaveCount(12);

        // Verify collision handling - "search" function should have prefixes
        handlers.Keys.Should().Contain("FileSystem-search");
        handlers.Keys.Should().Contain("Database-search");
    }

    [Fact]
    public void Build_WithFilteringDisabled_IncludesAllFunctions()
    {
        // Arrange
        var registry = CreateRegistryWithProviders();
        var filterConfig = new FunctionFilterConfig
        {
            EnableFiltering = false,
            GlobalBlockedFunctions = new List<string> { "*" }, // Should be ignored
        };
        registry.WithFilterConfig(filterConfig);

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        contracts.Should().HaveCount(12);
        handlers.Should().HaveCount(12);
    }

    #endregion

    #region Global Filtering Tests

    [Fact]
    public void Build_WithGlobalBlockList_FiltersMatchingFunctions()
    {
        // Arrange
        var registry = CreateRegistryWithProviders();
        var filterConfig = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = new List<string> { "*search*", "create_*" },
        };
        registry.WithFilterConfig(filterConfig);

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        // Should filter out: search_repositories, search (x2), create_issue, create_table = 5 functions
        // Remaining: 12 - 5 = 7
        contracts.Should().HaveCount(7);
        handlers.Should().HaveCount(7);

        handlers.Keys.Should().NotContain(key => key.Contains("search"));
        handlers.Keys.Should().NotContain(key => key.StartsWith("create"));
    }

    [Fact]
    public void Build_WithGlobalAllowList_OnlyIncludesMatchingFunctions()
    {
        // Arrange
        var registry = CreateRegistryWithProviders();
        var filterConfig = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalAllowedFunctions = new List<string> { "read_*", "list_*", "get_*" },
        };
        registry.WithFilterConfig(filterConfig);

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        // Should include: read_file, list_directory, list_repos, list_tables, get_file = 5
        contracts.Should().HaveCount(5);
        handlers.Keys.Should().BeEquivalentTo(expectation);
    }

    #endregion

    #region Provider-Specific Filtering Tests

    [Fact]
    public void Build_WithProviderDisabled_ExcludesAllFunctionsFromProvider()
    {
        // Arrange
        var registry = CreateRegistryWithProviders();
        var filterConfig = new FunctionFilterConfig
        {
            EnableFiltering = true,
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["Database"] = new ProviderFilterConfig { Enabled = false },
            },
        };
        registry.WithFilterConfig(filterConfig);

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        // Database provider has 4 functions, so we should have 12 - 4 = 8
        contracts.Should().HaveCount(8);
        handlers.Keys.Should().NotContain(key => key.Contains("execute_query"));
        handlers.Keys.Should().NotContain(key => key.Contains("list_tables"));
        handlers.Keys.Should().NotContain(key => key.Contains("create_table"));
    }

    [Fact]
    public void Build_WithProviderSpecificFilters_AppliesCorrectly()
    {
        // Arrange
        var registry = CreateRegistryWithProviders();
        var filterConfig = new FunctionFilterConfig
        {
            EnableFiltering = true,
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["GitHub"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    AllowedFunctions = new List<string> { "search_repositories", "list_repos" },
                },
                ["FileSystem"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    BlockedFunctions = new List<string> { "write_file" },
                },
            },
        };
        registry.WithFilterConfig(filterConfig);

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        // GitHub: only 2 allowed (search_repositories, list_repos)
        // FileSystem: 3 functions (4 - 1 blocked)
        // Database: all 4 functions
        // Total: 2 + 3 + 4 = 9
        contracts.Should().HaveCount(9);

        // Verify specific functions
        handlers.Keys.Should().Contain("search_repositories");
        handlers.Keys.Should().Contain("list_repos");
        handlers.Keys.Should().NotContain("create_issue");
        handlers.Keys.Should().NotContain("get_file");
        handlers.Keys.Should().NotContain("write_file");
    }

    #endregion

    #region Collision Detection with Filtering Tests

    [Fact]
    public void Build_WithFilteringAndCollisions_HandlesCorrectly()
    {
        // Arrange
        var registry = new FunctionRegistry().WithLogger(_mockLogger.Object);

        // Add providers with colliding function names
        registry.AddProvider(new TestFunctionProvider("Provider1", "commonFunc", "unique1", "filtered"));
        registry.AddProvider(new TestFunctionProvider("Provider2", "commonFunc", "unique2", "filtered"));

        var filterConfig = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = new List<string> { "filtered" },
        };
        registry.WithFilterConfig(filterConfig);

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        // Should have 4 functions total (2 commonFunc with prefixes, 2 unique)
        // But "filtered" is blocked, so only 2 remain
        contracts.Should().HaveCount(4);
        handlers.Should().HaveCount(4);

        // CommonFunc should have prefixes due to collision
        handlers.Keys.Should().Contain("Provider1-commonFunc");
        handlers.Keys.Should().Contain("Provider2-commonFunc");

        // Unique functions should not have prefixes
        handlers.Keys.Should().Contain("unique1");
        handlers.Keys.Should().Contain("unique2");

        // Filtered functions should not be present
        handlers.Keys.Should().NotContain(key => key.Contains("filtered"));
    }

    [Fact]
    public void Build_WithCustomPrefixAndFiltering_AppliesBothCorrectly()
    {
        // Arrange
        var registry = new FunctionRegistry().WithLogger(_mockLogger.Object);

        registry.AddProvider(new TestFunctionProvider("VeryLongProviderName", "func1", "func2", "blockedFunc"));
        registry.AddProvider(new TestFunctionProvider("AnotherLongName", "func1", "func3", "blockedFunc"));

        var filterConfig = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = new List<string> { "blockedFunc" },
            UsePrefixOnlyForCollisions = true,
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["VeryLongProviderName"] = new ProviderFilterConfig { CustomPrefix = "VLP" },
                ["AnotherLongName"] = new ProviderFilterConfig { CustomPrefix = "ALN" },
            },
        };
        registry.WithFilterConfig(filterConfig);

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        contracts.Should().HaveCount(4); // 6 total - 2 blocked

        // func1 has collision, should use custom prefixes
        handlers.Keys.Should().Contain("VLP-func1");
        handlers.Keys.Should().Contain("ALN-func1");

        // func2 and func3 have no collision
        handlers.Keys.Should().Contain("func2");
        handlers.Keys.Should().Contain("func3");

        // blockedFunc should be filtered out
        handlers.Keys.Should().NotContain(key => key.Contains("blockedFunc"));
    }

    #endregion

    #region Fluent Builder Pattern Tests

    [Fact]
    public void FluentBuilder_ChainedConfiguration_WorksCorrectly()
    {
        // Arrange & Act
        var (contracts, handlers) = new FunctionRegistry()
            .WithLogger(_mockLogger.Object)
            .AddProvider(new TestFunctionProvider("Provider1", "func1", "func2"))
            .AddProvider(new TestFunctionProvider("Provider2", "func2", "func3"))
            .WithConflictResolution(ConflictResolution.TakeFirst)
            .WithFilterConfig(
                new FunctionFilterConfig
                {
                    EnableFiltering = true,
                    GlobalBlockedFunctions = new List<string> { "func3" },
                }
            )
            .Build();

        // Assert
        contracts.Should().HaveCount(2);
        handlers.Should().HaveCount(2);
        handlers.Keys.Should().BeEquivalentTo(expectationArray);
    }

    [Fact]
    public void FluentBuilder_WithExplicitFunction_OverridesProvider()
    {
        // Arrange & Act
        var registry = new FunctionRegistry()
            .WithLogger(_mockLogger.Object)
            .AddProvider(new TestFunctionProvider("Provider1", "func1", "func2"))
            .AddFunction(
                new FunctionContract { Name = "func1", Description = "Override" },
                _ => Task.FromResult("overridden result"),
                "ExplicitProvider"
            )
            .WithFilterConfig(new FunctionFilterConfig { EnableFiltering = false });

        var (contracts, handlers) = registry.Build();

        // Assert
        contracts.Should().HaveCount(2);
        var func1Contract = contracts.First(c => c.Name == "func1");
        func1Contract.Description.Should().Be("Override");
    }

    #endregion

    #region Backward Compatibility Tests

    [Fact]
    public void Build_WithNoConfiguration_MaintainsBackwardCompatibility()
    {
        // Arrange
        var registry = new FunctionRegistry();
        registry.AddProvider(new TestFunctionProvider("Provider", "func1", "func2"));

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        contracts.Should().HaveCount(2);
        handlers.Should().HaveCount(2);
        // Should work exactly as before without any filtering
    }

    [Fact]
    public void Build_WithNullFilterConfig_BehavesAsNoFiltering()
    {
        // Arrange
        var registry = CreateRegistryWithProviders().WithFilterConfig(null);

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        contracts.Should().HaveCount(12);
        handlers.Should().HaveCount(12);
    }

    #endregion

    #region Complex Real-World Scenarios

    [Fact]
    public void Build_WithComplexRealWorldConfiguration_HandlesCorrectly()
    {
        // Arrange - Simulate a real production scenario
        var registry = new FunctionRegistry().WithLogger(_mockLogger.Object);

        // Add multiple MCP servers
        registry.AddProvider(
            new TestFunctionProvider(
                "github",
                "search_repositories",
                "create_issue",
                "update_issue",
                "close_issue",
                "create_pull_request",
                "merge_pull_request",
                "list_workflows",
                "trigger_workflow"
            )
        );

        registry.AddProvider(
            new TestFunctionProvider(
                "filesystem",
                "read_file",
                "write_file",
                "delete_file",
                "list_directory",
                "create_directory",
                "move_file",
                "copy_file",
                "search"
            )
        );

        registry.AddProvider(
            new TestFunctionProvider(
                "database",
                "execute_query",
                "list_tables",
                "describe_table",
                "create_table",
                "drop_table",
                "backup_database",
                "restore_database",
                "search"
            )
        );

        registry.AddProvider(
            new TestFunctionProvider(
                "memory",
                "store_memory",
                "retrieve_memory",
                "search",
                "clear_memory",
                "list_memories",
                "update_memory",
                "delete_memory"
            )
        );

        // Configure complex filtering rules
        var filterConfig = new FunctionFilterConfig
        {
            EnableFiltering = true,
            // Block all delete/drop operations globally
            GlobalBlockedFunctions = new List<string> { "delete_*", "drop_*", "*_dangerous" },
            // Only allow read operations by default
            GlobalAllowedFunctions = new List<string>
            {
                "read_*",
                "list_*",
                "describe_*",
                "search*",
                "get_*",
                "retrieve_*",
                "create_*",
                "update_*",
                "store_*",
                "trigger_*",
                "merge_*",
            },
            UsePrefixOnlyForCollisions = true,
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["github"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    CustomPrefix = "gh",
                    // GitHub can have more permissions
                    AllowedFunctions = new List<string> { "*" },
                    BlockedFunctions = new List<string> { }, // Override global blocks for GitHub
                },
                ["filesystem"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    CustomPrefix = "fs",
                    // Filesystem is more restricted
                    BlockedFunctions = new List<string> { "write_*", "move_*", "copy_*" },
                },
                ["database"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    CustomPrefix = "db",
                    // Database operations are restricted
                    AllowedFunctions = new List<string> { "execute_query", "list_*", "describe_*", "search" },
                },
                ["memory"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    // No custom prefix for memory
                    AllowedFunctions = new List<string> { "*" }, // Memory operations are allowed
                },
            },
        };

        registry.WithFilterConfig(filterConfig);

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        // Count expected functions:
        // GitHub: 8 (all allowed, no global blocks apply due to provider override)
        // Filesystem: 3 (read_file, list_directory, search - others blocked)
        // Database: 4 (execute_query, list_tables, describe_table, search)
        // Memory: 6 (all except delete_memory which matches global block pattern)
        // Total: 8 + 3 + 4 + 6 = 21

        // But search has collisions, so we need to account for prefixes
        handlers.Keys.Should().Contain("fs-search");
        handlers.Keys.Should().Contain("db-search");
        handlers.Keys.Should().Contain("memory-search");

        // GitHub functions should not be blocked
        handlers.Keys.Should().Contain("close_issue"); // Even though it could match a delete pattern

        // Filesystem write operations should be blocked
        handlers.Keys.Should().NotContain("write_file");
        handlers.Keys.Should().NotContain("move_file");

        // Database dangerous operations should be blocked
        handlers.Keys.Should().NotContain(k => k.Contains("drop_table"));
        handlers.Keys.Should().NotContain(k => k.Contains("backup_database"));

        // Memory delete should be blocked by global rule
        handlers.Keys.Should().NotContain("delete_memory");

        // Verify custom prefixes are used for collisions
        var searchFunctions = handlers.Keys.Where(k => k.Contains("search")).ToList();
        searchFunctions.Should().Contain("fs-search");
        searchFunctions.Should().Contain("db-search");
        searchFunctions.Should().Contain("memory-search");
    }

    #endregion

    #region Performance Tests

    [Fact]
    public void Build_WithLargeNumberOfProviders_PerformsEfficiently()
    {
        // Arrange
        var registry = new FunctionRegistry().WithLogger(_mockLogger.Object);

        // Add 50 providers with 20 functions each = 1000 functions
        for (int i = 0; i < 50; i++)
        {
            var functions = Enumerable.Range(0, 20).Select(j => $"func_{i}_{j}").ToArray();
            registry.AddProvider(new TestFunctionProvider($"Provider{i}", functions));
        }

        var filterConfig = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = new List<string> { "*_5", "*_10", "*_15" }, // Block some
        };
        registry.WithFilterConfig(filterConfig);

        // Act
        var startTime = DateTime.UtcNow;
        var (contracts, handlers) = registry.Build();
        var elapsed = DateTime.UtcNow - startTime;

        // Assert
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "Build should be fast even with many functions");
        contracts.Should().HaveCount(850); // 1000 - 150 blocked (3 per provider * 50)
        handlers.Should().HaveCount(850);
    }

    #endregion
}
