using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.LmCore.Configuration;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpMiddleware;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

/// <summary>
/// Test suite for MCP-specific middleware wrapper classes ensuring backward compatibility
/// </summary>
public class McpMiddlewareTests
{
    private readonly Mock<ILogger> _mockLogger;

    public McpMiddlewareTests()
    {
        _mockLogger = new Mock<ILogger>();
    }

    #region McpToolFilter Tests

    [Fact]
    public void McpToolFilter_Constructor_CreatesInstanceWithNullConfig()
    {
        // Arrange & Act
#pragma warning disable CS0618 // Type or member is obsolete
        var filter = new McpToolFilter(null, null, _mockLogger.Object);
#pragma warning restore CS0618

        // Assert
        filter.Should().NotBeNull();
    }

    [Fact]
    public void McpToolFilter_WithGlobalConfig_WrapsCorrectly()
    {
        // Arrange
#pragma warning disable CS0618 // Type or member is obsolete
        var globalConfig = new McpToolFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = new List<string> { "dangerous_function" },
        };
        var serverConfigs = new Dictionary<string, McpServerFilterConfig>();

        var filter = new McpToolFilter(globalConfig, serverConfigs, _mockLogger.Object);
#pragma warning restore CS0618

        // Act
        var shouldFilter = filter.ShouldFilterTool(
            "testServer",
            "dangerous_function",
            "dangerous_function"
        );

        // Assert
        shouldFilter.Should().BeTrue();
    }

    [Fact]
    public void McpToolFilter_WithServerConfig_AppliesServerSpecificRules()
    {
        // Arrange
#pragma warning disable CS0618 // Type or member is obsolete
        var globalConfig = new McpToolFilterConfig { EnableFiltering = true };
        var serverConfigs = new Dictionary<string, McpServerFilterConfig>
        {
            ["blockedServer"] = new McpServerFilterConfig { Enabled = false },
        };

        var filter = new McpToolFilter(globalConfig, serverConfigs, _mockLogger.Object);
#pragma warning restore CS0618

        // Act
        var shouldFilter = filter.ShouldFilterTool("blockedServer", "anyFunction", "anyFunction");

        // Assert
        shouldFilter.Should().BeTrue();
    }

    [Fact]
    public void McpToolFilter_BackwardCompatible_WorksWithLegacyCode()
    {
        // This test ensures that existing code using McpToolFilter continues to work
        // Arrange
#pragma warning disable CS0618 // Type or member is obsolete
        var config = new McpToolFilterConfig
        {
            EnableFiltering = true,
            GlobalAllowedFunctions = new List<string> { "allowed_*" },
            GlobalBlockedFunctions = new List<string> { "blocked_*" },
        };

        var serverConfig = new McpServerFilterConfig
        {
            Enabled = true,
            AllowedFunctions = new List<string> { "server_allowed" },
            BlockedFunctions = new List<string> { "server_blocked" },
        };

        var serverConfigs = new Dictionary<string, McpServerFilterConfig>
        {
            ["testServer"] = serverConfig,
        };

        var filter = new McpToolFilter(config, serverConfigs, _mockLogger.Object);
#pragma warning restore CS0618

        // Act & Assert - Test various scenarios
        filter
            .ShouldFilterTool("testServer", "allowed_function", "allowed_function")
            .Should()
            .BeFalse();
        filter
            .ShouldFilterTool("testServer", "blocked_function", "blocked_function")
            .Should()
            .BeTrue();
        filter.ShouldFilterTool("testServer", "server_blocked", "server_blocked").Should().BeTrue();
        filter
            .ShouldFilterTool("testServer", "random_function", "random_function")
            .Should()
            .BeTrue(); // Not in allow list
    }

    [Fact]
    public void McpToolFilterConfig_InheritsFromFunctionFilterConfig()
    {
        // Arrange
#pragma warning disable CS0618 // Type or member is obsolete
        var config = new McpToolFilterConfig
        {
            EnableFiltering = true,
            UsePrefixOnlyForCollisions = false,
            GlobalAllowedFunctions = new List<string> { "test" },
        };
#pragma warning restore CS0618

        // Act & Assert - Verify it's actually a FunctionFilterConfig
        config.Should().BeAssignableTo<FunctionFilterConfig>();
        config.EnableFiltering.Should().BeTrue();
        config.UsePrefixOnlyForCollisions.Should().BeFalse();
        config.GlobalAllowedFunctions.Should().ContainSingle("test");
    }

    [Fact]
    public void McpServerFilterConfig_InheritsFromProviderFilterConfig()
    {
        // Arrange
#pragma warning disable CS0618 // Type or member is obsolete
        var config = new McpServerFilterConfig
        {
            Enabled = true,
            CustomPrefix = "mcp",
            AllowedFunctions = new List<string> { "func1" },
        };
#pragma warning restore CS0618

        // Act & Assert - Verify it's actually a ProviderFilterConfig
        config.Should().BeAssignableTo<ProviderFilterConfig>();
        config.Enabled.Should().BeTrue();
        config.CustomPrefix.Should().Be("mcp");
        config.AllowedFunctions.Should().ContainSingle("func1");
    }

    #endregion

    #region McpToolCollisionDetector Tests

    [Fact]
    public void McpToolCollisionDetector_Constructor_CreatesInstance()
    {
        // Arrange & Act
#pragma warning disable CS0618 // Type or member is obsolete
        var detector = new McpToolCollisionDetector(_mockLogger.Object);
#pragma warning restore CS0618

        // Assert
        detector.Should().NotBeNull();
    }

    [Fact]
    public void McpToolCollisionDetector_DetectAndResolveCollisions_HandlesEmptyInput()
    {
        // Arrange
#pragma warning disable CS0618 // Type or member is obsolete
        var detector = new McpToolCollisionDetector(_mockLogger.Object);
#pragma warning restore CS0618
        var toolsByServer = new Dictionary<string, List<McpClientTool>>();

        // Act
        var result = detector.DetectAndResolveCollisions(toolsByServer, true);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void McpToolCollisionDetector_WithNoCollisions_ReturnsOriginalNames()
    {
        // Arrange
#pragma warning disable CS0618 // Type or member is obsolete
        var detector = new McpToolCollisionDetector(_mockLogger.Object);
#pragma warning restore CS0618

        var toolsByServer = new Dictionary<string, List<McpClientTool>>
        {
            ["server1"] = new List<McpClientTool>
            {
                new McpClientTool { Name = "tool1", Description = "Tool 1" },
                new McpClientTool { Name = "tool2", Description = "Tool 2" },
            },
            ["server2"] = new List<McpClientTool>
            {
                new McpClientTool { Name = "tool3", Description = "Tool 3" },
                new McpClientTool { Name = "tool4", Description = "Tool 4" },
            },
        };

        // Act
        var result = detector.DetectAndResolveCollisions(toolsByServer, true);

        // Assert
        result.Should().HaveCount(4);
        result[("server1", "tool1")].Should().Be("tool1");
        result[("server1", "tool2")].Should().Be("tool2");
        result[("server2", "tool3")].Should().Be("tool3");
        result[("server2", "tool4")].Should().Be("tool4");
    }

    [Fact]
    public void McpToolCollisionDetector_WithCollisions_AppliesPrefixes()
    {
        // Arrange
#pragma warning disable CS0618 // Type or member is obsolete
        var detector = new McpToolCollisionDetector(_mockLogger.Object);
#pragma warning restore CS0618

        var toolsByServer = new Dictionary<string, List<McpClientTool>>
        {
            ["server1"] = new List<McpClientTool>
            {
                new McpClientTool { Name = "search", Description = "Search in server1" },
                new McpClientTool { Name = "unique1", Description = "Unique to server1" },
            },
            ["server2"] = new List<McpClientTool>
            {
                new McpClientTool { Name = "search", Description = "Search in server2" },
                new McpClientTool { Name = "unique2", Description = "Unique to server2" },
            },
        };

        // Act
        var result = detector.DetectAndResolveCollisions(toolsByServer, true);

        // Assert
        result.Should().HaveCount(4);

        // Colliding functions should have prefixes
        result[("server1", "search")].Should().Be("server1-search");
        result[("server2", "search")].Should().Be("server2-search");

        // Non-colliding functions should not have prefixes
        result[("server1", "unique1")].Should().Be("unique1");
        result[("server2", "unique2")].Should().Be("unique2");
    }

    [Fact]
    public void McpToolCollisionDetector_WithPrefixAll_PrefixesAllTools()
    {
        // Arrange
#pragma warning disable CS0618 // Type or member is obsolete
        var detector = new McpToolCollisionDetector(_mockLogger.Object);
#pragma warning restore CS0618

        var toolsByServer = new Dictionary<string, List<McpClientTool>>
        {
            ["server1"] = new List<McpClientTool>
            {
                new McpClientTool { Name = "tool1", Description = "Tool 1" },
            },
            ["server2"] = new List<McpClientTool>
            {
                new McpClientTool { Name = "tool2", Description = "Tool 2" },
            },
        };

        // Act - usePrefixOnlyForCollisions = false means prefix all
        var result = detector.DetectAndResolveCollisions(toolsByServer, false);

        // Assert
        result.Should().HaveCount(2);
        result[("server1", "tool1")].Should().Be("server1-tool1");
        result[("server2", "tool2")].Should().Be("server2-tool2");
    }

    [Fact]
    public void McpToolCollisionDetector_BackwardCompatible_WorksWithComplexScenario()
    {
        // This test ensures the MCP wrapper handles a realistic scenario
        // Arrange
#pragma warning disable CS0618 // Type or member is obsolete
        var detector = new McpToolCollisionDetector(_mockLogger.Object);
#pragma warning restore CS0618

        var toolsByServer = new Dictionary<string, List<McpClientTool>>
        {
            ["github"] = new List<McpClientTool>
            {
                new McpClientTool
                {
                    Name = "search_repos",
                    Description = "Search GitHub repositories",
                },
                new McpClientTool { Name = "create_issue", Description = "Create a GitHub issue" },
                new McpClientTool { Name = "list", Description = "List GitHub items" },
            },
            ["filesystem"] = new List<McpClientTool>
            {
                new McpClientTool { Name = "read_file", Description = "Read a file" },
                new McpClientTool { Name = "write_file", Description = "Write a file" },
                new McpClientTool { Name = "list", Description = "List files" },
            },
            ["database"] = new List<McpClientTool>
            {
                new McpClientTool { Name = "execute_query", Description = "Execute SQL query" },
                new McpClientTool { Name = "list", Description = "List database objects" },
            },
        };

        // Act
        var result = detector.DetectAndResolveCollisions(toolsByServer, true);

        // Assert
        result.Should().HaveCount(8);

        // Non-colliding tools should not have prefixes
        result[("github", "search_repos")].Should().Be("search_repos");
        result[("github", "create_issue")].Should().Be("create_issue");
        result[("filesystem", "read_file")].Should().Be("read_file");
        result[("filesystem", "write_file")].Should().Be("write_file");
        result[("database", "execute_query")].Should().Be("execute_query");

        // Colliding "list" tools should have prefixes
        result[("github", "list")].Should().Be("github-list");
        result[("filesystem", "list")].Should().Be("filesystem-list");
        result[("database", "list")].Should().Be("database-list");
    }

    #endregion

    #region Obsolete Attribute Tests

    [Fact]
    public void ObsoleteClasses_HaveCorrectAttributes()
    {
        // Verify that the MCP wrapper classes are marked as obsolete

#pragma warning disable CS0618 // Type or member is obsolete
        var mcpToolFilterType = typeof(McpToolFilter);
        var mcpToolFilterConfigType = typeof(McpToolFilterConfig);
        var mcpServerFilterConfigType = typeof(McpServerFilterConfig);
        var mcpToolCollisionDetectorType = typeof(McpToolCollisionDetector);
#pragma warning restore CS0618

        // Assert
        mcpToolFilterType
            .GetCustomAttributes(typeof(ObsoleteAttribute), false)
            .Should()
            .HaveCount(1, "McpToolFilter should be marked as obsolete");

        mcpToolFilterConfigType
            .GetCustomAttributes(typeof(ObsoleteAttribute), false)
            .Should()
            .HaveCount(1, "McpToolFilterConfig should be marked as obsolete");

        mcpServerFilterConfigType
            .GetCustomAttributes(typeof(ObsoleteAttribute), false)
            .Should()
            .HaveCount(1, "McpServerFilterConfig should be marked as obsolete");

        mcpToolCollisionDetectorType
            .GetCustomAttributes(typeof(ObsoleteAttribute), false)
            .Should()
            .HaveCount(1, "McpToolCollisionDetector should be marked as obsolete");
    }

    [Fact]
    public void ObsoleteAttributes_ContainCorrectMessages()
    {
        // Verify the obsolete messages point to the new classes

#pragma warning disable CS0618 // Type or member is obsolete
        var mcpToolFilterAttr =
            typeof(McpToolFilter)
                .GetCustomAttributes(typeof(ObsoleteAttribute), false)
                .FirstOrDefault() as ObsoleteAttribute;

        var mcpToolFilterConfigAttr =
            typeof(McpToolFilterConfig)
                .GetCustomAttributes(typeof(ObsoleteAttribute), false)
                .FirstOrDefault() as ObsoleteAttribute;
#pragma warning restore CS0618

        // Assert
        mcpToolFilterAttr?.Message.Should().Contain("FunctionFilter");
        mcpToolFilterConfigAttr?.Message.Should().Contain("FunctionFilterConfig");
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void McpWrappers_IntegrateWithGeneralizedClasses()
    {
        // Test that MCP wrappers correctly delegate to generalized classes
        // This ensures the refactoring maintains backward compatibility

        // Arrange
#pragma warning disable CS0618 // Type or member is obsolete
        var mcpConfig = new McpToolFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = new List<string> { "dangerous_*" },
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["testServer"] = new McpServerFilterConfig { Enabled = true, CustomPrefix = "ts" },
            },
        };

        var serverConfigs = new Dictionary<string, McpServerFilterConfig>
        {
            ["testServer"] = new McpServerFilterConfig
            {
                Enabled = true,
                BlockedFunctions = new List<string> { "specific_blocked" },
            },
        };

        var filter = new McpToolFilter(mcpConfig, serverConfigs, _mockLogger.Object);
#pragma warning restore CS0618

        // Act & Assert
        filter
            .ShouldFilterTool("testServer", "dangerous_function", "dangerous_function")
            .Should()
            .BeTrue();
        filter
            .ShouldFilterTool("testServer", "specific_blocked", "specific_blocked")
            .Should()
            .BeTrue();
        filter.ShouldFilterTool("testServer", "safe_function", "safe_function").Should().BeFalse();
        filter.ShouldFilterTool("unknownServer", "any_function", "any_function").Should().BeFalse();
    }

    #endregion
}
