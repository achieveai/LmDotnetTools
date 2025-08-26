using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.LmCore.Configuration;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

/// <summary>
/// Comprehensive test suite for FunctionCollisionDetector class
/// </summary>
public class FunctionCollisionDetectorTests
{
    private readonly Mock<ILogger> _mockLogger;

    public FunctionCollisionDetectorTests()
    {
        _mockLogger = new Mock<ILogger>();
    }

    #region Helper Methods

    private static FunctionDescriptor CreateTestDescriptor(
        string functionName,
        string providerName = "TestProvider"
    )
    {
        return new FunctionDescriptor
        {
            Contract = new FunctionContract
            {
                Name = functionName,
                Description = $"Test function {functionName}",
            },
            Handler = _ => Task.FromResult($"Result from {functionName}"),
            ProviderName = providerName,
        };
    }

    private static List<FunctionDescriptor> CreateDescriptorsWithCollisions()
    {
        return new List<FunctionDescriptor>
        {
            CreateTestDescriptor("getUser", "Provider1"),
            CreateTestDescriptor("getUser", "Provider2"),
            CreateTestDescriptor("listItems", "Provider1"),
            CreateTestDescriptor("createResource", "Provider2"),
            CreateTestDescriptor("createResource", "Provider3"),
        };
    }

    #endregion

    #region Basic Collision Detection Tests

    [Fact]
    public void DetectAndResolveCollisions_WithNoCollisions_ReturnsOriginalNames()
    {
        // Arrange
        var detector = new FunctionCollisionDetector(_mockLogger.Object);
        var functions = new List<FunctionDescriptor>
        {
            CreateTestDescriptor("func1", "Provider1"),
            CreateTestDescriptor("func2", "Provider2"),
            CreateTestDescriptor("func3", "Provider3"),
        };

        // Act
        var namingMap = detector.DetectAndResolveCollisions(functions);

        // Assert
        Assert.Equal(3, namingMap.Count);
        Assert.Equal(new[] { "func1", "func2", "func3" }, namingMap.Values.OrderBy(v => v));
    }

    [Fact]
    public void DetectAndResolveCollisions_WithCollisions_AppliesPrefixes()
    {
        // Arrange
        var detector = new FunctionCollisionDetector(_mockLogger.Object);
        var functions = CreateDescriptorsWithCollisions();

        // Act
        var namingMap = detector.DetectAndResolveCollisions(functions);

        // Assert
        Assert.Equal(5, namingMap.Count);

        // Functions with collisions should have prefixes
        var getUserProvider1 = functions.First(f =>
            f.Contract.Name == "getUser" && f.ProviderName == "Provider1"
        );
        Assert.Equal("Provider1-getUser", namingMap[getUserProvider1.Key]);

        var getUserProvider2 = functions.First(f =>
            f.Contract.Name == "getUser" && f.ProviderName == "Provider2"
        );
        Assert.Equal("Provider2-getUser", namingMap[getUserProvider2.Key]);

        // Function without collision should not have prefix (when UsePrefixOnlyForCollisions is true)
        var listItems = functions.First(f => f.Contract.Name == "listItems");
        Assert.Equal("listItems", namingMap[listItems.Key]);
    }

    #endregion

    #region Prefix Configuration Tests

    [Fact]
    public void DetectAndResolveCollisions_WithUsePrefixOnlyForCollisionsFalse_PrefixesAllFunctions()
    {
        // Arrange
        var detector = new FunctionCollisionDetector(_mockLogger.Object);
        var functions = new List<FunctionDescriptor>
        {
            CreateTestDescriptor("func1", "Provider1"),
            CreateTestDescriptor("func2", "Provider2"),
        };
        var config = new FunctionFilterConfig { UsePrefixOnlyForCollisions = false };

        // Act
        var namingMap = detector.DetectAndResolveCollisions(functions, config);

        // Assert
        Assert.Equal(2, namingMap.Count);
        Assert.Contains("Provider1-func1", namingMap.Values);
        Assert.Contains("Provider2-func2", namingMap.Values);
    }

    [Fact]
    public void DetectAndResolveCollisions_WithCustomPrefix_UsesCustomPrefixInsteadOfProviderName()
    {
        // Arrange
        var detector = new FunctionCollisionDetector(_mockLogger.Object);
        var functions = new List<FunctionDescriptor>
        {
            CreateTestDescriptor("func1", "LongProviderName"),
            CreateTestDescriptor("func1", "AnotherProvider"),
        };
        var config = new FunctionFilterConfig
        {
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["LongProviderName"] = new ProviderFilterConfig { CustomPrefix = "LP" },
            },
        };

        // Act
        var namingMap = detector.DetectAndResolveCollisions(functions, config);

        // Assert
        var longProviderFunc = functions.First(f => f.ProviderName == "LongProviderName");
        Assert.Equal("LP-func1", namingMap[longProviderFunc.Key]);

        var anotherProviderFunc = functions.First(f => f.ProviderName == "AnotherProvider");
        Assert.Equal("AnotherProvider-func1", namingMap[anotherProviderFunc.Key]);
    }

    #endregion

    #region Name Sanitization Tests

    [Fact]
    public void SanitizeName_WithValidName_ReturnsUnchanged()
    {
        // Arrange & Act
        var result = FunctionCollisionDetector.SanitizeName("valid_function-name123");

        // Assert
        Assert.Equal("valid_function-name123", result);
    }

    [Fact]
    public void SanitizeName_WithInvalidCharacters_ReplacesWithUnderscore()
    {
        // Arrange & Act
        var result = FunctionCollisionDetector.SanitizeName("func.with@special#chars!");

        // Assert
        Assert.Equal("func_with_special_chars_", result);
    }

    [Fact]
    public void SanitizeName_WithMultipleConsecutiveUnderscores_ReplacesWithSingle()
    {
        // Arrange & Act
        var result = FunctionCollisionDetector.SanitizeName("func___with____many_____underscores");

        // Assert
        Assert.Equal("func_with_many_underscores", result);
    }

    [Fact]
    public void SanitizeName_StartingWithNumber_PrependsUnderscore()
    {
        // Arrange & Act
        var result = FunctionCollisionDetector.SanitizeName("123function");

        // Assert
        Assert.Equal("_123function", result);
    }

    [Fact]
    public void SanitizeName_WithEmptyString_ReturnsDefault()
    {
        // Arrange & Act
        var result = FunctionCollisionDetector.SanitizeName("");

        // Assert
        Assert.Equal("unknown", result);
    }

    [Fact]
    public void SanitizeName_WithNull_ReturnsDefault()
    {
        // Arrange & Act
        var result = FunctionCollisionDetector.SanitizeName(null);

        // Assert
        Assert.Equal("unknown", result);
    }

    [Fact]
    public void SanitizeName_WithOnlyInvalidCharacters_ReturnsSanitizedDefault()
    {
        // Arrange & Act
        var result = FunctionCollisionDetector.SanitizeName("@#$%^&*()");

        // Assert
        Assert.Equal("sanitized_function", result);
    }

    #endregion

    #region CollisionAnalysisReport Tests

    [Fact]
    public void AnalyzeCollisions_WithNoCollisions_ReturnsEmptyCollisionsList()
    {
        // Arrange
        var detector = new FunctionCollisionDetector(_mockLogger.Object);
        var functions = new List<FunctionDescriptor>
        {
            CreateTestDescriptor("func1", "Provider1"),
            CreateTestDescriptor("func2", "Provider2"),
            CreateTestDescriptor("func3", "Provider3"),
        };

        // Act
        var report = detector.AnalyzeCollisions(functions);

        // Assert
        Assert.Equal(3, report.TotalFunctions);
        Assert.Equal(3, report.UniqueNames);
        Assert.Equal(0, report.CollisionCount);
        Assert.Empty(report.Collisions);
    }

    [Fact]
    public void AnalyzeCollisions_WithCollisions_ReturnsDetailedReport()
    {
        // Arrange
        var detector = new FunctionCollisionDetector(_mockLogger.Object);
        var functions = CreateDescriptorsWithCollisions();

        // Act
        var report = detector.AnalyzeCollisions(functions);

        // Assert
        Assert.Equal(5, report.TotalFunctions);
        Assert.Equal(3, report.UniqueNames);
        Assert.Equal(2, report.CollisionCount);
        Assert.Equal(2, report.Collisions.Count);

        var getUserCollision = report.Collisions.First(c => c.FunctionName == "getUser");
        Assert.Equal(2, getUserCollision.Count);
        Assert.Equal(
            new[] { "Provider1", "Provider2" },
            getUserCollision.Providers.OrderBy(p => p)
        );

        var createResourceCollision = report.Collisions.First(c =>
            c.FunctionName == "createResource"
        );
        Assert.Equal(2, createResourceCollision.Count);
        Assert.Equal(
            new[] { "Provider2", "Provider3" },
            createResourceCollision.Providers.OrderBy(p => p)
        );
    }

    [Fact]
    public void AnalyzeCollisions_WithMultipleFunctionsFromSameProvider_HandlesCorrectly()
    {
        // Arrange
        var detector = new FunctionCollisionDetector(_mockLogger.Object);
        var functions = new List<FunctionDescriptor>
        {
            CreateTestDescriptor("func1", "Provider1"),
            CreateTestDescriptor("func1", "Provider1"), // Duplicate from same provider
            CreateTestDescriptor("func1", "Provider2"),
        };

        // Act
        var report = detector.AnalyzeCollisions(functions);

        // Assert
        Assert.Equal(3, report.TotalFunctions);
        Assert.Equal(1, report.UniqueNames);
        Assert.Equal(1, report.CollisionCount);

        var collision = report.Collisions.Single();
        Assert.Equal("func1", collision.FunctionName);
        Assert.Equal(3, collision.Count);
        Assert.Equal(new[] { "Provider1", "Provider2" }, collision.Providers.OrderBy(p => p));
    }

    #endregion

    #region Edge Cases and Complex Scenarios

    [Fact]
    public void DetectAndResolveCollisions_WithEmptyFunctionList_ReturnsEmptyMap()
    {
        // Arrange
        var detector = new FunctionCollisionDetector(_mockLogger.Object);
        var functions = new List<FunctionDescriptor>();

        // Act
        var namingMap = detector.DetectAndResolveCollisions(functions);

        // Assert
        Assert.Empty(namingMap);
    }

    [Fact]
    public void DetectAndResolveCollisions_WithNullProviderName_HandlesGracefully()
    {
        // Arrange
        var detector = new FunctionCollisionDetector(_mockLogger.Object);
        var functions = new List<FunctionDescriptor>
        {
            new FunctionDescriptor
            {
                Contract = new FunctionContract { Name = "func1" },
                Handler = _ => Task.FromResult("result"),
                ProviderName = null,
            },
        };

        // Act
        var namingMap = detector.DetectAndResolveCollisions(functions);

        // Assert
        Assert.Single(namingMap);
        Assert.NotNull(namingMap.Values.First());
        Assert.NotEmpty(namingMap.Values.First());
    }

    [Fact]
    public void DetectAndResolveCollisions_WithSpecialCharactersInNames_SanitizesCorrectly()
    {
        // Arrange
        var detector = new FunctionCollisionDetector(_mockLogger.Object);
        var functions = new List<FunctionDescriptor>
        {
            CreateTestDescriptor("func.with.dots", "Provider@1"),
            CreateTestDescriptor("func.with.dots", "Provider#2"),
        };

        // Act
        var namingMap = detector.DetectAndResolveCollisions(functions);

        // Assert
        Assert.Equal(2, namingMap.Count);
        Assert.All(
            namingMap.Values,
            name => Assert.True(name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'))
        );
    }

    [Fact]
    public void DetectAndResolveCollisions_WithLargeNumberOfProviders_PerformsEfficiently()
    {
        // Arrange
        var detector = new FunctionCollisionDetector(_mockLogger.Object);
        var functions = new List<FunctionDescriptor>();

        // Create 100 providers with some collisions
        for (int i = 0; i < 100; i++)
        {
            functions.Add(CreateTestDescriptor("commonFunc", $"Provider{i}"));
            functions.Add(CreateTestDescriptor($"uniqueFunc{i}", $"Provider{i}"));
        }

        // Act
        var startTime = DateTime.UtcNow;
        var namingMap = detector.DetectAndResolveCollisions(functions);
        var elapsed = DateTime.UtcNow - startTime;

        // Assert
        Assert.Equal(200, namingMap.Count);
        Assert.True(elapsed < TimeSpan.FromSeconds(1));

        // All "commonFunc" should have prefixes
        var commonFuncs = functions.Where(f => f.Contract.Name == "commonFunc");
        foreach (var func in commonFuncs)
        {
            Assert.Contains("-commonFunc", namingMap[func.Key]);
        }

        // All unique functions should not have prefixes (with default config)
        var uniqueFuncs = functions.Where(f => f.Contract.Name.StartsWith("uniqueFunc"));
        foreach (var func in uniqueFuncs)
        {
            Assert.Equal(func.Contract.Name, namingMap[func.Key]);
        }
    }

    [Fact]
    public void DetectAndResolveCollisions_WithComplexConfiguration_AppliesAllSettingsCorrectly()
    {
        // Arrange
        var detector = new FunctionCollisionDetector(_mockLogger.Object);
        var functions = new List<FunctionDescriptor>
        {
            CreateTestDescriptor("sharedFunc", "VeryLongProviderName"),
            CreateTestDescriptor("sharedFunc", "AnotherLongProvider"),
            CreateTestDescriptor("uniqueFunc", "VeryLongProviderName"),
            CreateTestDescriptor("anotherUnique", "ShortName"),
        };

        var config = new FunctionFilterConfig
        {
            UsePrefixOnlyForCollisions = false, // Prefix all
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["VeryLongProviderName"] = new ProviderFilterConfig { CustomPrefix = "VLPN" },
                ["AnotherLongProvider"] = new ProviderFilterConfig { CustomPrefix = "ALP" },
            },
        };

        // Act
        var namingMap = detector.DetectAndResolveCollisions(functions, config);

        // Assert
        Assert.Equal(4, namingMap.Count);

        // Check custom prefixes are applied
        var vlpnShared = functions.First(f =>
            f.ProviderName == "VeryLongProviderName" && f.Contract.Name == "sharedFunc"
        );
        Assert.Equal("VLPN-sharedFunc", namingMap[vlpnShared.Key]);

        var vlpnUnique = functions.First(f =>
            f.ProviderName == "VeryLongProviderName" && f.Contract.Name == "uniqueFunc"
        );
        Assert.Equal("VLPN-uniqueFunc", namingMap[vlpnUnique.Key]);

        var alpShared = functions.First(f => f.ProviderName == "AnotherLongProvider");
        Assert.Equal("ALP-sharedFunc", namingMap[alpShared.Key]);

        // Provider without custom prefix uses provider name
        var shortNameFunc = functions.First(f => f.ProviderName == "ShortName");
        Assert.Equal("ShortName-anotherUnique", namingMap[shortNameFunc.Key]);
    }

    #endregion

    #region Integration Scenarios

    [Fact]
    public void DetectAndResolveCollisions_WithRealWorldScenario_HandlesComplexCase()
    {
        // Arrange - Simulate a real scenario with multiple MCP servers
        var detector = new FunctionCollisionDetector(_mockLogger.Object);
        var functions = new List<FunctionDescriptor>
        {
            // GitHub MCP Server
            CreateTestDescriptor("search_repositories", "github"),
            CreateTestDescriptor("get_file_contents", "github"),
            CreateTestDescriptor("create_issue", "github"),
            // Filesystem MCP Server
            CreateTestDescriptor("read_file", "filesystem"),
            CreateTestDescriptor("write_file", "filesystem"),
            CreateTestDescriptor("list_directory", "filesystem"),
            // Database MCP Server
            CreateTestDescriptor("execute_query", "database"),
            CreateTestDescriptor("list_tables", "database"),
            // Collision: Both GitHub and Filesystem have a search function
            CreateTestDescriptor("search", "github"),
            CreateTestDescriptor("search", "filesystem"),
            // Collision: Multiple providers have a list function
            CreateTestDescriptor("list", "github"),
            CreateTestDescriptor("list", "filesystem"),
            CreateTestDescriptor("list", "database"),
        };

        // Act
        var namingMap = detector.DetectAndResolveCollisions(functions);

        // Assert
        Assert.Equal(13, namingMap.Count);

        // Non-colliding functions should not have prefixes
        Assert.Equal(
            "search_repositories",
            namingMap[functions.First(f => f.Contract.Name == "search_repositories").Key]
        );
        Assert.Equal(
            "read_file",
            namingMap[functions.First(f => f.Contract.Name == "read_file").Key]
        );
        Assert.Equal(
            "execute_query",
            namingMap[functions.First(f => f.Contract.Name == "execute_query").Key]
        );

        // Colliding functions should have prefixes
        Assert.Equal(
            "github-search",
            namingMap[
                functions.First(f => f.Contract.Name == "search" && f.ProviderName == "github").Key
            ]
        );
        Assert.Equal(
            "filesystem-search",
            namingMap[
                functions
                    .First(f => f.Contract.Name == "search" && f.ProviderName == "filesystem")
                    .Key
            ]
        );

        // All "list" functions should have prefixes
        Assert.Equal(
            "github-list",
            namingMap[
                functions.First(f => f.Contract.Name == "list" && f.ProviderName == "github").Key
            ]
        );
        Assert.Equal(
            "filesystem-list",
            namingMap[
                functions
                    .First(f => f.Contract.Name == "list" && f.ProviderName == "filesystem")
                    .Key
            ]
        );
        Assert.Equal(
            "database-list",
            namingMap[
                functions.First(f => f.Contract.Name == "list" && f.ProviderName == "database").Key
            ]
        );
    }

    #endregion
}
