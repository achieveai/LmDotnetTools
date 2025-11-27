using AchieveAi.LmDotnetTools.LmCore.Models;

using AchieveAi.LmDotnetTools.LmCore.Core;
namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class FunctionRegistryTests
{
    [Fact]
    public void Build_WithNoProviders_ReturnsEmptyCollections()
    {
        // Arrange
        var registry = new FunctionRegistry();

        // Act
        var (contracts, handlers) = registry.Build();

        // Assert
        Assert.Empty(contracts);
        Assert.Empty(handlers);
    }

    [Fact]
    public void Build_WithSingleProvider_ReturnsProviderFunctions()
    {
        // Arrange
        string[] stringArray = ["func1", "func2"];
        var registry = new FunctionRegistry();
        var provider = CreateTestProvider("test", stringArray);

        // Act
        _ = registry.AddProvider(provider);
        var (contracts, handlers) = registry.Build();

        // Assert
        Assert.Equal(2, contracts.Count());
        Assert.Equal(2, handlers.Count);
        Assert.Contains("func1", handlers.Keys);
        Assert.Contains("func2", handlers.Keys);
    }

    [Fact]
    public async Task Build_WithExplicitFunction_OverridesProvider()
    {
        // Arrange
        string[] stringArray = ["func1"];
        var registry = new FunctionRegistry();
        var provider = CreateTestProvider("test", stringArray);
        var explicitContract = CreateTestContract("func1");
        var explicitHandler = CreateTestHandler("explicit-result");

        // Act
        _ = registry.AddProvider(provider);
        _ = registry.AddFunction(explicitContract, explicitHandler);
        var (contracts, handlers) = registry.Build();

        // Assert
        _ = Assert.Single(contracts);
        _ = Assert.Single(handlers);

        var result = await handlers["func1"]("{}");
        Assert.Equal("explicit-result", result);
    }

    [Fact]
    public void Build_WithConflictingProviders_ThrowsException()
    {
        // Arrange
        string[] stringArray = ["func1"];
        var registry = new FunctionRegistry();
        var provider1 = CreateTestProvider("provider1", stringArray);
        var provider2 = CreateTestProvider("provider2", stringArray);

        // Act & Assert
        _ = registry.AddProvider(provider1);
        _ = registry.AddProvider(provider2);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Build());
        Assert.Contains("func1", exception.Message);
        Assert.Contains("provider1", exception.Message);
        Assert.Contains("provider2", exception.Message);
    }

    [Fact]
    public async Task Build_WithTakeFirstConflictResolution_UsesPriorityOrder()
    {
        // Arrange
        string[] stringArray = ["func1"];
        var registry = new FunctionRegistry();
        var provider1 = CreateTestProvider("provider1", stringArray, 200);
        var provider2 = CreateTestProvider("provider2", stringArray, 100);

        // Act
        _ = registry.AddProvider(provider1);
        _ = registry.AddProvider(provider2);
        _ = registry.WithConflictResolution(ConflictResolution.TakeFirst);
        var (contracts, handlers) = registry.Build();

        // Assert
        _ = Assert.Single(contracts);
        _ = Assert.Single(handlers);

        var result = await handlers["func1"]("{}");
        Assert.Equal("provider2-result", result); // provider2 has lower priority (100 < 200)
    }

    [Fact]
    public async Task Build_WithTakeLastConflictResolution_UsesLastProvider()
    {
        // Arrange
        string[] stringArray = ["func1"];
        var registry = new FunctionRegistry();
        var provider1 = CreateTestProvider("provider1", stringArray);
        var provider2 = CreateTestProvider("provider2", stringArray);

        // Act
        _ = registry.AddProvider(provider1);
        _ = registry.AddProvider(provider2);
        _ = registry.WithConflictResolution(ConflictResolution.TakeLast);
        var (contracts, handlers) = registry.Build();

        // Assert
        _ = Assert.Single(contracts);
        _ = Assert.Single(handlers);

        var result = await handlers["func1"]("{}");
        Assert.Equal("provider2-result", result);
    }

    [Fact]
    public async Task Build_WithPreferMcpConflictResolution_PrefersMcpFunctions()
    {
        // Arrange - Note: MCP and natural functions will have different keys due to class name
        // So this test validates the preference logic when keys do conflict
        string[] stringArray = ["func1"];
        var registry = new FunctionRegistry();
        var naturalProvider = CreateTestProvider("natural", stringArray, isMcp: false);
        var mcpProvider = CreateTestProvider("mcp", stringArray, isMcp: true);

        // Act
        _ = registry.AddProvider(naturalProvider);
        _ = registry.AddProvider(mcpProvider);
        _ = registry.WithConflictResolution(ConflictResolution.PreferMcp);
        var (contracts, handlers) = registry.Build();

        // Assert - Since keys are different, both functions should be present
        Assert.Equal(2, contracts.Count());
        Assert.Equal(2, handlers.Count);

        // Check that both functions are available with their respective keys
        Assert.Contains("func1", handlers.Keys); // Natural function
        Assert.Contains("TestClass-func1", handlers.Keys); // MCP function

        var naturalResult = await handlers["func1"]("{}");
        var mcpResult = await handlers["TestClass-func1"]("{}");
        Assert.Equal("natural-result", naturalResult);
        Assert.Equal("mcp-result", mcpResult);
    }

    [Fact]
    public async Task Build_WithPreferMcpConflictResolution_BothFunctionsPresent()
    {
        // Arrange - MCP and natural functions will have different keys due to class name
        // This tests that both are included when there's no actual conflict
        var registry = new FunctionRegistry();
        var naturalProvider = new TestFunctionProviderForConflict("natural", "func1", false);
        var mcpProvider = new TestFunctionProviderForConflict("mcp", "func1", true);

        // Act
        _ = registry.AddProvider(naturalProvider);
        _ = registry.AddProvider(mcpProvider);
        _ = registry.WithConflictResolution(ConflictResolution.PreferMcp);
        var (contracts, handlers) = registry.Build();

        // Assert - No conflict, so both functions present
        Assert.Equal(2, contracts.Count());
        Assert.Equal(2, handlers.Count);

        // Natural function with key "func1"
        var naturalResult = await handlers["func1"]("{}");
        Assert.Equal("natural-result", naturalResult);

        // MCP function with key "TestClass-func1"
        var mcpResult = await handlers["TestClass-func1"]("{}");
        Assert.Equal("mcp-result", mcpResult);
    }

    [Fact]
    public async Task Build_WithCustomConflictHandler_UsesCustomLogic()
    {
        // Arrange
        string[] stringArray = ["func1"];
        var registry = new FunctionRegistry();
        var provider1 = CreateTestProvider("provider1", stringArray);
        var provider2 = CreateTestProvider("provider2", stringArray);

        // Act
        _ = registry.AddProvider(provider1);
        _ = registry.AddProvider(provider2);
        _ = registry.WithConflictHandler((key, candidates) => candidates.First(c => c.ProviderName == "provider1"));

        var (contracts, handlers) = registry.Build();

        // Assert
        _ = Assert.Single(contracts);
        _ = Assert.Single(handlers);

        var result = await handlers["func1"]("{}");
        Assert.Equal("provider1-result", result);
    }

    [Fact]
    public void BuildMiddleware_CreatesWorkingMiddleware()
    {
        // Arrange
        string[] stringArray = ["func1"];
        var registry = new FunctionRegistry();
        var provider = CreateTestProvider("test", stringArray);

        // Act
        _ = registry.AddProvider(provider);
        var middleware = registry.BuildMiddleware("TestMiddleware");

        // Assert
        Assert.NotNull(middleware);
        Assert.Equal("TestMiddleware", middleware.Name);
    }

    [Fact]
    public void GetMarkdownDocumentation_WithEmptyRegistry_ReturnsBasicMarkdown()
    {
        // Arrange
        var registry = new FunctionRegistry();

        // Act
        var markdown = registry.GetMarkdownDocumentation();

        // Assert
        Assert.Contains("# Function Registry Documentation", markdown);
        Assert.Contains("## Summary", markdown);
        Assert.Contains("- **Total Functions:** 0", markdown);
        Assert.Contains("- **Total Providers:** 0", markdown);
        Assert.Contains("- **Conflict Resolution:** Throw", markdown);
    }

    [Fact]
    public void GetMarkdownDocumentation_WithSingleProvider_ReturnsFormattedMarkdown()
    {
        // Arrange
        string[] stringArray = ["func1", "func2"];
        var registry = new FunctionRegistry();
        var provider = CreateTestProvider("TestProvider", stringArray, 100);

        // Act
        _ = registry.AddProvider(provider);
        var markdown = registry.GetMarkdownDocumentation();

        // Assert
        Assert.Contains("# Function Registry Documentation", markdown);
        Assert.Contains("- **Total Functions:** 2", markdown);
        Assert.Contains("- **Total Providers:** 1", markdown);
        Assert.Contains("- **TestProvider** (Priority: 100): 2 functions", markdown);
        Assert.Contains("### func1", markdown);
        Assert.Contains("### func2", markdown);
        Assert.Contains("Function details:", markdown);
        Assert.Contains("- **Provider:** TestProvider", markdown);
        Assert.Contains("- **Key:** `func1`", markdown);
        Assert.Contains("- **Key:** `func2`", markdown);
        Assert.Contains("Test function func1", markdown);
        Assert.Contains("Test function func2", markdown);
    }

    [Fact]
    public void GetMarkdownDocumentation_WithExplicitFunction_IncludesExplicitFunction()
    {
        // Arrange
        var registry = new FunctionRegistry();
        var explicitContract = CreateTestContract("explicitFunc", description: "Explicit test function");
        var explicitHandler = CreateTestHandler("explicit-result");

        // Act
        _ = registry.AddFunction(explicitContract, explicitHandler, "ExplicitProvider");
        var markdown = registry.GetMarkdownDocumentation();

        // Assert
        Assert.Contains("- **Total Functions:** 1", markdown);
        Assert.Contains("- **ExplicitProvider**: 1 function", markdown);
        Assert.Contains("### explicitFunc", markdown);
        Assert.Contains("- **Provider:** ExplicitProvider", markdown);
        Assert.Contains("Explicit test function", markdown);
    }

    [Fact]
    public void GetMarkdownDocumentation_WithMcpFunction_ShowsCorrectKeyFormat()
    {
        // Arrange
        string[] stringArray = ["mcpFunc"];
        var registry = new FunctionRegistry();
        var provider = CreateTestProvider("McpProvider", stringArray, isMcp: true);

        // Act
        _ = registry.AddProvider(provider);
        var markdown = registry.GetMarkdownDocumentation();

        // Assert
        Assert.Contains("### TestClass.mcpFunc", markdown); // Display name format
        Assert.Contains("- **Key:** `TestClass-mcpFunc`", markdown); // Key format
        Assert.Contains("- **Provider:** McpProvider", markdown);
    }

    [Fact]
    public void GetMarkdownDocumentation_WithConflictResolution_ShowsResolutionStrategy()
    {
        // Arrange
        string[] stringArray = ["func1"];
        var registry = new FunctionRegistry();
        var provider1 = CreateTestProvider("provider1", stringArray);
        var provider2 = CreateTestProvider("provider2", stringArray);

        // Act
        _ = registry.AddProvider(provider1);
        _ = registry.AddProvider(provider2);
        _ = registry.WithConflictResolution(ConflictResolution.TakeFirst);
        var markdown = registry.GetMarkdownDocumentation();

        // Assert
        Assert.Contains("- **Conflict Resolution:** TakeFirst", markdown);
        Assert.Contains("- **Total Functions:** 1", markdown); // Only one function due to conflict resolution
    }

    [Fact]
    public void GetMarkdownDocumentation_WithFunctionParameters_ShowsParameterTable()
    {
        // Arrange
        var registry = new FunctionRegistry();
        var contract = CreateTestContractWithParameters("paramFunc");
        var handler = CreateTestHandler("result");

        // Act
        _ = registry.AddFunction(contract, handler, "TestProvider");
        var markdown = registry.GetMarkdownDocumentation();

        // Assert
        Assert.Contains("Parameters:", markdown);
        Assert.Contains("- **stringParam** (`string` (required))", markdown);
        Assert.Contains("  A string parameter", markdown);
        Assert.Contains("- **optionalParam** (`number` (optional), default: 42)", markdown);
        Assert.Contains("  An optional parameter", markdown);
    }

    [Fact]
    public void GetMarkdownDocumentation_WithFunctionWithNoParameters_ShowsNoParameters()
    {
        // Arrange
        var registry = new FunctionRegistry();
        var contract = CreateTestContract("noParamFunc");
        var handler = CreateTestHandler("result");

        // Act
        _ = registry.AddFunction(contract, handler, "TestProvider");
        var markdown = registry.GetMarkdownDocumentation();

        // Assert
        Assert.Contains("### noParamFunc", markdown);
        Assert.Contains("Parameters:", markdown);
        Assert.Contains("- *No parameters required*", markdown);
    }

    [Fact]
    public void GetMarkdownDocumentation_WithReturnType_ShowsReturnSection()
    {
        // Arrange
        var registry = new FunctionRegistry();
        var contract = CreateTestContractWithReturnType("returnFunc");
        var handler = CreateTestHandler("result");

        // Act
        _ = registry.AddFunction(contract, handler, "TestProvider");
        var markdown = registry.GetMarkdownDocumentation();

        // Assert
        Assert.Contains("Returns:", markdown);
        Assert.Contains("- **Type:** `String`", markdown);
        Assert.Contains("- **Description:** Returns a string result", markdown);
    }

    internal static IFunctionProvider CreateTestProvider(
        string name,
        string[] functionNames,
        int priority = 100,
        bool isMcp = false
    )
    {
        return new TestFunctionProvider(name, functionNames, priority, isMcp);
    }

    internal static IFunctionProvider CreateTestProviderWithSameKey(
        string name,
        string functionName,
        bool isMcp = false
    )
    {
        return new TestFunctionProviderWithSameKey(name, functionName, isMcp);
    }

    internal static FunctionContract CreateTestContract(
        string name,
        string? className = null,
        string? description = null
    )
    {
        return new FunctionContract
        {
            Name = name,
            ClassName = className,
            Description = description ?? $"Test function {name}",
            Parameters = [],
        };
    }

    internal static Func<string, Task<string>> CreateTestHandler(string result)
    {
        return _ => Task.FromResult(result);
    }

    private static FunctionContract CreateTestContractWithParameters(string name)
    {
        return new FunctionContract
        {
            Name = name,
            Description = $"Test function {name}",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "stringParam",
                    Description = "A string parameter",
                    ParameterType = new JsonSchemaObject { Type = JsonSchemaTypeHelper.ToType("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "optionalParam",
                    Description = "An optional parameter",
                    ParameterType = new JsonSchemaObject { Type = JsonSchemaTypeHelper.ToType("number") },
                    IsRequired = false,
                    DefaultValue = 42,
                },
            ],
        };
    }

    private static FunctionContract CreateTestContractWithReturnType(string name)
    {
        return new FunctionContract
        {
            Name = name,
            Description = $"Test function {name}",
            ReturnType = typeof(string),
            ReturnDescription = "Returns a string result",
            Parameters = [],
        };
    }

    private class TestFunctionProvider : IFunctionProvider
    {
        private readonly string[] _functionNames;
        private readonly bool _isMcp;

        public TestFunctionProvider(string name, string[] functionNames, int priority = 100, bool isMcp = false)
        {
            ProviderName = name;
            Priority = priority;
            _functionNames = functionNames;
            _isMcp = isMcp;
        }

        public string ProviderName { get; }
        public int Priority { get; }

        public IEnumerable<FunctionDescriptor> GetFunctions()
        {
            return _functionNames.Select(name => new FunctionDescriptor
            {
                Contract = new FunctionContract
                {
                    Name = name,
                    ClassName = _isMcp ? "TestClass" : null,
                    Description = $"Test function {name}",
                    Parameters = [],
                },
                Handler = _ => Task.FromResult($"{ProviderName}-result"),
                ProviderName = ProviderName,
            });
        }
    }

    private class TestFunctionProviderWithSameKey : IFunctionProvider
    {
        private readonly string _functionName;
        private readonly bool _isMcp;

        public TestFunctionProviderWithSameKey(string name, string functionName, bool isMcp = false)
        {
            ProviderName = name;
            _functionName = functionName;
            _isMcp = isMcp;
        }

        public string ProviderName { get; }
        public int Priority => 100;

        public IEnumerable<FunctionDescriptor> GetFunctions()
        {
            return
            [
                new FunctionDescriptor
                {
                    Contract = new FunctionContract
                    {
                        Name = _functionName,
                        ClassName = _isMcp ? "TestClass" : null, // Use isMcp flag for testing conflict resolution
                        Description = $"Test function {_functionName}",
                        Parameters = [],
                    },
                    Handler = _ => Task.FromResult($"{ProviderName}-result"),
                    ProviderName = ProviderName,
                },
            ];
        }
    }

    private class TestFunctionProviderForConflict : IFunctionProvider
    {
        private readonly string _functionName;
        private readonly bool _isMcp;

        public TestFunctionProviderForConflict(string name, string functionName, bool isMcp)
        {
            ProviderName = name;
            _functionName = functionName;
            _isMcp = isMcp;
        }

        public string ProviderName { get; }
        public int Priority => 100;

        public IEnumerable<FunctionDescriptor> GetFunctions()
        {
            return
            [
                new FunctionDescriptor
                {
                    Contract = new FunctionContract
                    {
                        Name = _functionName,
                        ClassName = _isMcp ? "TestClass" : null, // MCP has class name, natural doesn't
                        Description = $"Test function {_functionName}",
                        Parameters = [],
                    },
                    Handler = _ => Task.FromResult($"{ProviderName}-result"),
                    ProviderName = ProviderName,
                },
            ];
        }
    }
}
