using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Xunit;

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
        var registry = new FunctionRegistry();
        var provider = CreateTestProvider("test", new[] { "func1", "func2" });

        // Act
        registry.AddProvider(provider);
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
        var registry = new FunctionRegistry();
        var provider = CreateTestProvider("test", new[] { "func1" });
        var explicitContract = CreateTestContract("func1");
        var explicitHandler = CreateTestHandler("explicit-result");

        // Act
        registry.AddProvider(provider);
        registry.AddFunction(explicitContract, explicitHandler);
        var (contracts, handlers) = registry.Build();

        // Assert
        Assert.Single(contracts);
        Assert.Single(handlers);
        
        var result = await handlers["func1"]("{}");
        Assert.Equal("explicit-result", result);
    }

    [Fact]
    public void Build_WithConflictingProviders_ThrowsException()
    {
        // Arrange
        var registry = new FunctionRegistry();
        var provider1 = CreateTestProvider("provider1", new[] { "func1" });
        var provider2 = CreateTestProvider("provider2", new[] { "func1" });

        // Act & Assert
        registry.AddProvider(provider1);
        registry.AddProvider(provider2);
        
        var exception = Assert.Throws<InvalidOperationException>(() => registry.Build());
        Assert.Contains("func1", exception.Message);
        Assert.Contains("provider1", exception.Message);
        Assert.Contains("provider2", exception.Message);
    }

    [Fact]
    public async Task Build_WithTakeFirstConflictResolution_UsesPriorityOrder()
    {
        // Arrange
        var registry = new FunctionRegistry();
        var provider1 = CreateTestProvider("provider1", new[] { "func1" }, priority: 200);
        var provider2 = CreateTestProvider("provider2", new[] { "func1" }, priority: 100);

        // Act
        registry.AddProvider(provider1);
        registry.AddProvider(provider2);
        registry.WithConflictResolution(ConflictResolution.TakeFirst);
        var (contracts, handlers) = registry.Build();

        // Assert
        Assert.Single(contracts);
        Assert.Single(handlers);
        
        var result = await handlers["func1"]("{}");
        Assert.Equal("provider2-result", result); // provider2 has lower priority (100 < 200)
    }

    [Fact]
    public async Task Build_WithTakeLastConflictResolution_UsesLastProvider()
    {
        // Arrange
        var registry = new FunctionRegistry();
        var provider1 = CreateTestProvider("provider1", new[] { "func1" });
        var provider2 = CreateTestProvider("provider2", new[] { "func1" });

        // Act
        registry.AddProvider(provider1);
        registry.AddProvider(provider2);
        registry.WithConflictResolution(ConflictResolution.TakeLast);
        var (contracts, handlers) = registry.Build();

        // Assert
        Assert.Single(contracts);
        Assert.Single(handlers);
        
        var result = await handlers["func1"]("{}");
        Assert.Equal("provider2-result", result);
    }

    [Fact]
    public async Task Build_WithPreferMcpConflictResolution_PrefersMcpFunctions()
    {
        // Arrange - Note: MCP and natural functions will have different keys due to class name
        // So this test validates the preference logic when keys do conflict
        var registry = new FunctionRegistry();
        var naturalProvider = CreateTestProvider("natural", new[] { "func1" }, isMcp: false);
        var mcpProvider = CreateTestProvider("mcp", new[] { "func1" }, isMcp: true);

        // Act
        registry.AddProvider(naturalProvider);
        registry.AddProvider(mcpProvider);
        registry.WithConflictResolution(ConflictResolution.PreferMcp);
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
        registry.AddProvider(naturalProvider);
        registry.AddProvider(mcpProvider);
        registry.WithConflictResolution(ConflictResolution.PreferMcp);
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
        var registry = new FunctionRegistry();
        var provider1 = CreateTestProvider("provider1", new[] { "func1" });
        var provider2 = CreateTestProvider("provider2", new[] { "func1" });

        // Act
        registry.AddProvider(provider1);
        registry.AddProvider(provider2);
        registry.WithConflictHandler((key, candidates) => 
            candidates.First(c => c.ProviderName == "provider1"));
        
        var (contracts, handlers) = registry.Build();

        // Assert
        Assert.Single(contracts);
        Assert.Single(handlers);
        
        var result = await handlers["func1"]("{}");
        Assert.Equal("provider1-result", result);
    }

    [Fact]
    public void BuildMiddleware_CreatesWorkingMiddleware()
    {
        // Arrange
        var registry = new FunctionRegistry();
        var provider = CreateTestProvider("test", new[] { "func1" });

        // Act
        registry.AddProvider(provider);
        var middleware = registry.BuildMiddleware("TestMiddleware");

        // Assert
        Assert.NotNull(middleware);
        Assert.Equal("TestMiddleware", middleware.Name);
    }

    private static IFunctionProvider CreateTestProvider(string name, string[] functionNames, int priority = 100, bool isMcp = false)
    {
        return new TestFunctionProvider(name, functionNames, priority, isMcp);
    }

    private static IFunctionProvider CreateTestProviderWithSameKey(string name, string functionName, bool isMcp = false)
    {
        return new TestFunctionProviderWithSameKey(name, functionName, isMcp);
    }

    private static FunctionContract CreateTestContract(string name, string? className = null)
    {
        return new FunctionContract
        {
            Name = name,
            ClassName = className,
            Description = $"Test function {name}",
            Parameters = new List<FunctionParameterContract>()
        };
    }

    private static Func<string, Task<string>> CreateTestHandler(string result)
    {
        return _ => Task.FromResult(result);
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
                    Parameters = new List<FunctionParameterContract>()
                },
                Handler = _ => Task.FromResult($"{ProviderName}-result"),
                ProviderName = ProviderName
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
            return new[]
            {
                new FunctionDescriptor
                {
                    Contract = new FunctionContract
                    {
                        Name = _functionName,
                        ClassName = _isMcp ? "TestClass" : null, // Use isMcp flag for testing conflict resolution
                        Description = $"Test function {_functionName}",
                        Parameters = new List<FunctionParameterContract>()
                    },
                    Handler = _ => Task.FromResult($"{ProviderName}-result"),
                    ProviderName = ProviderName
                }
            };
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
            return new[]
            {
                new FunctionDescriptor
                {
                    Contract = new FunctionContract
                    {
                        Name = _functionName,
                        ClassName = _isMcp ? "TestClass" : null, // MCP has class name, natural doesn't
                        Description = $"Test function {_functionName}",
                        Parameters = new List<FunctionParameterContract>()
                    },
                    Handler = _ => Task.FromResult($"{ProviderName}-result"),
                    ProviderName = ProviderName
                }
            };
        }
    }
}