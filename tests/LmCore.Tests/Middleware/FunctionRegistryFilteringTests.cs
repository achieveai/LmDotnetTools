using AchieveAi.LmDotnetTools.LmCore.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

/// <summary>
/// Integration tests for FunctionRegistry with filtering and collision detection
/// </summary>
public class FunctionRegistryFilteringTests
{
    private readonly Mock<ILogger> _mockLogger;
    private static readonly string[] expectation =
    [
        "read_file",
        "list_directory",
        "list_repos",
        "list_tables",
        "get_file",
    ];
    private static readonly string[] expectationArray = ["func1", "func2"];

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
            _functions = [.. functionNames
                .Select(name => new FunctionDescriptor
                {
                    Contract = new FunctionContract
                    {
                        Name = name,
                        Description = $"Test function {name} from {providerName}",
                    },
                    Handler = _ => Task.FromResult($"Result from {name}"),
                    ProviderName = _providerName,
                })];
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

        _ = registry.AddProvider(
            new TestFunctionProvider("GitHub", "search_repositories", "create_issue", "get_file", "list_repos")
        );

        _ = registry.AddProvider(
            new TestFunctionProvider("FileSystem", "read_file", "write_file", "list_directory", "search")
        );

        _ = registry.AddProvider(
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
        _ = contracts.Should().HaveCount(12);
        _ = handlers.Should().HaveCount(12);

        // Verify collision handling - "search" function should have prefixes
        _ = handlers.Keys.Should().Contain("FileSystem-search");
        _ = handlers.Keys.Should().Contain("Database-search");
    }

    [Fact]
    public void Build_WithFilteringDisabled_IncludesAllFunctions()
    {
        // Arrange
        var registry = CreateRegistryWithProviders();
        var filterConfig = new FunctionFilterConfig
        {
            EnableFiltering = false,
            GlobalBlockedFunctions = ["*"], // Should be ignored
        };
        _ = registry.WithFilterConfig(filterConfig);

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        _ = contracts.Should().HaveCount(12);
        _ = handlers.Should().HaveCount(12);
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
            GlobalBlockedFunctions = ["*search*", "create_*"],
        };
        _ = registry.WithFilterConfig(filterConfig);

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        // Should filter out: search_repositories, search (x2), create_issue, create_table = 5 functions
        // Remaining: 12 - 5 = 7
        _ = contracts.Should().HaveCount(7);
        _ = handlers.Should().HaveCount(7);

        _ = handlers.Keys.Should().NotContain(key => key.Contains("search"));
        _ = handlers.Keys.Should().NotContain(key => key.StartsWith("create"));
    }

    [Fact]
    public void Build_WithGlobalAllowList_OnlyIncludesMatchingFunctions()
    {
        // Arrange
        var registry = CreateRegistryWithProviders();
        var filterConfig = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalAllowedFunctions = ["read_*", "list_*", "get_*"],
        };
        _ = registry.WithFilterConfig(filterConfig);

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        // Should include: read_file, list_directory, list_repos, list_tables, get_file = 5
        _ = contracts.Should().HaveCount(5);
        _ = handlers.Keys.Should().BeEquivalentTo(expectation);
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
        _ = registry.WithFilterConfig(filterConfig);

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        // Database provider has 4 functions, so we should have 12 - 4 = 8
        _ = contracts.Should().HaveCount(8);
        _ = handlers.Keys.Should().NotContain(key => key.Contains("execute_query"));
        _ = handlers.Keys.Should().NotContain(key => key.Contains("list_tables"));
        _ = handlers.Keys.Should().NotContain(key => key.Contains("create_table"));
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
                    AllowedFunctions = ["search_repositories", "list_repos"],
                },
                ["FileSystem"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    BlockedFunctions = ["write_file"],
                },
            },
        };
        _ = registry.WithFilterConfig(filterConfig);

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        // GitHub: only 2 allowed (search_repositories, list_repos)
        // FileSystem: 3 functions (4 - 1 blocked)
        // Database: all 4 functions
        // Total: 2 + 3 + 4 = 9
        _ = contracts.Should().HaveCount(9);

        // Verify specific functions
        _ = handlers.Keys.Should().Contain("search_repositories");
        _ = handlers.Keys.Should().Contain("list_repos");
        _ = handlers.Keys.Should().NotContain("create_issue");
        _ = handlers.Keys.Should().NotContain("get_file");
        _ = handlers.Keys.Should().NotContain("write_file");
    }

    #endregion

    #region Collision Detection with Filtering Tests

    [Fact]
    public void Build_WithFilteringAndCollisions_HandlesCorrectly()
    {
        // Arrange
        var registry = new FunctionRegistry().WithLogger(_mockLogger.Object);

        // Add providers with colliding function names
        _ = registry.AddProvider(new TestFunctionProvider("Provider1", "commonFunc", "unique1", "filtered"));
        _ = registry.AddProvider(new TestFunctionProvider("Provider2", "commonFunc", "unique2", "filtered"));

        var filterConfig = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = ["filtered"],
        };
        _ = registry.WithFilterConfig(filterConfig);

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        // Should have 4 functions total (2 commonFunc with prefixes, 2 unique)
        // But "filtered" is blocked, so only 2 remain
        _ = contracts.Should().HaveCount(4);
        _ = handlers.Should().HaveCount(4);

        // CommonFunc should have prefixes due to collision
        _ = handlers.Keys.Should().Contain("Provider1-commonFunc");
        _ = handlers.Keys.Should().Contain("Provider2-commonFunc");

        // Unique functions should not have prefixes
        _ = handlers.Keys.Should().Contain("unique1");
        _ = handlers.Keys.Should().Contain("unique2");

        // Filtered functions should not be present
        _ = handlers.Keys.Should().NotContain(key => key.Contains("filtered"));
    }

    [Fact]
    public void Build_WithCustomPrefixAndFiltering_AppliesBothCorrectly()
    {
        // Arrange
        var registry = new FunctionRegistry().WithLogger(_mockLogger.Object);

        _ = registry.AddProvider(new TestFunctionProvider("VeryLongProviderName", "func1", "func2", "blockedFunc"));
        _ = registry.AddProvider(new TestFunctionProvider("AnotherLongName", "func1", "func3", "blockedFunc"));

        var filterConfig = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = ["blockedFunc"],
            UsePrefixOnlyForCollisions = true,
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["VeryLongProviderName"] = new ProviderFilterConfig { CustomPrefix = "VLP" },
                ["AnotherLongName"] = new ProviderFilterConfig { CustomPrefix = "ALN" },
            },
        };
        _ = registry.WithFilterConfig(filterConfig);

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        _ = contracts.Should().HaveCount(4); // 6 total - 2 blocked

        // func1 has collision, should use custom prefixes
        _ = handlers.Keys.Should().Contain("VLP-func1");
        _ = handlers.Keys.Should().Contain("ALN-func1");

        // func2 and func3 have no collision
        _ = handlers.Keys.Should().Contain("func2");
        _ = handlers.Keys.Should().Contain("func3");

        // blockedFunc should be filtered out
        _ = handlers.Keys.Should().NotContain(key => key.Contains("blockedFunc"));
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
                    GlobalBlockedFunctions = ["func3"],
                }
            )
            .Build();

        // Assert
        _ = contracts.Should().HaveCount(2);
        _ = handlers.Should().HaveCount(2);
        _ = handlers.Keys.Should().BeEquivalentTo(expectationArray);
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
        _ = contracts.Should().HaveCount(2);
        var func1Contract = contracts.First(c => c.Name == "func1");
        _ = func1Contract.Description.Should().Be("Override");
    }

    #endregion

    #region Backward Compatibility Tests

    [Fact]
    public void Build_WithNoConfiguration_MaintainsBackwardCompatibility()
    {
        // Arrange
        var registry = new FunctionRegistry();
        _ = registry.AddProvider(new TestFunctionProvider("Provider", "func1", "func2"));

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        _ = contracts.Should().HaveCount(2);
        _ = handlers.Should().HaveCount(2);
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
        _ = contracts.Should().HaveCount(12);
        _ = handlers.Should().HaveCount(12);
    }

    #endregion

    #region Complex Real-World Scenarios

    [Fact]
    public void Build_WithComplexRealWorldConfiguration_HandlesCorrectly()
    {
        // Arrange - Simulate a real production scenario
        var registry = new FunctionRegistry().WithLogger(_mockLogger.Object);

        // Add multiple MCP servers
        _ = registry.AddProvider(
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

        _ = registry.AddProvider(
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

        _ = registry.AddProvider(
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

        _ = registry.AddProvider(
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
            GlobalBlockedFunctions = ["delete_*", "drop_*", "*_dangerous"],
            // Only allow read operations by default
            GlobalAllowedFunctions =
            [
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
            ],
            UsePrefixOnlyForCollisions = true,
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["github"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    CustomPrefix = "gh",
                    // GitHub can have more permissions
                    AllowedFunctions = ["*"],
                    BlockedFunctions = [], // Override global blocks for GitHub
                },
                ["filesystem"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    CustomPrefix = "fs",
                    // Filesystem is more restricted
                    BlockedFunctions = ["write_*", "move_*", "copy_*"],
                },
                ["database"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    CustomPrefix = "db",
                    // Database operations are restricted
                    AllowedFunctions = ["execute_query", "list_*", "describe_*", "search"],
                },
                ["memory"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    // No custom prefix for memory
                    AllowedFunctions = ["*"], // Memory operations are allowed
                },
            },
        };

        _ = registry.WithFilterConfig(filterConfig);

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
        _ = handlers.Keys.Should().Contain("fs-search");
        _ = handlers.Keys.Should().Contain("db-search");
        _ = handlers.Keys.Should().Contain("memory-search");

        // GitHub functions should not be blocked
        _ = handlers.Keys.Should().Contain("close_issue"); // Even though it could match a delete pattern

        // Filesystem write operations should be blocked
        _ = handlers.Keys.Should().NotContain("write_file");
        _ = handlers.Keys.Should().NotContain("move_file");

        // Database dangerous operations should be blocked
        _ = handlers.Keys.Should().NotContain(k => k.Contains("drop_table"));
        _ = handlers.Keys.Should().NotContain(k => k.Contains("backup_database"));

        // Memory delete should be blocked by global rule
        _ = handlers.Keys.Should().NotContain("delete_memory");

        // Verify custom prefixes are used for collisions
        var searchFunctions = handlers.Keys.Where(k => k.Contains("search")).ToList();
        _ = searchFunctions.Should().Contain("fs-search");
        _ = searchFunctions.Should().Contain("db-search");
        _ = searchFunctions.Should().Contain("memory-search");
    }

    #endregion

    #region Performance Tests

    [Fact]
    public void Build_WithLargeNumberOfProviders_PerformsEfficiently()
    {
        // Arrange
        var registry = new FunctionRegistry().WithLogger(_mockLogger.Object);

        // Add 50 providers with 20 functions each = 1000 functions
        for (var i = 0; i < 50; i++)
        {
            var functions = Enumerable.Range(0, 20).Select(j => $"func_{i}_{j}").ToArray();
            _ = registry.AddProvider(new TestFunctionProvider($"Provider{i}", functions));
        }

        var filterConfig = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = ["*_5", "*_10", "*_15"], // Block some
        };
        _ = registry.WithFilterConfig(filterConfig);

        // Act
        var startTime = DateTime.UtcNow;
        var (contracts, handlers) = registry.Build();
        var elapsed = DateTime.UtcNow - startTime;

        // Assert
        _ = elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "Build should be fast even with many functions");
        _ = contracts.Should().HaveCount(850); // 1000 - 150 blocked (3 per provider * 50)
        _ = handlers.Should().HaveCount(850);
    }

    #endregion
}
