using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpMiddleware;
using AchieveAi.LmDotnetTools.McpSampleServer;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests;

public class McpFunctionProviderTests
{
    [Fact]
    public void McpFunctionProvider_ShouldHaveCorrectProviderName()
    {
        // Arrange
        var assembly = typeof(CalculatorTool).Assembly;
        var provider = new McpFunctionProvider(assembly, "TestProvider");

        // Act & Assert
        Assert.Equal("TestProvider", provider.ProviderName);
        Assert.Equal(100, provider.Priority);
    }

    [Fact]
    public void McpFunctionProvider_ShouldDiscoverMcpFunctions()
    {
        // Arrange
        var assembly = typeof(CalculatorTool).Assembly;
        var provider = new McpFunctionProvider(assembly);

        // Act
        var functions = provider.GetFunctions().ToList();

        // Assert
        Assert.NotEmpty(functions);

        // Check that calculator functions are found
        var addFunction = functions.FirstOrDefault(f => f.Contract.Name == "Add");
        Assert.NotNull(addFunction);
        Assert.Equal("CalculatorTool", addFunction.Contract.ClassName);
        Assert.NotNull(addFunction.Handler);
    }

    [Fact]
    public async Task McpFunctionProvider_FunctionsShouldBeExecutable()
    {
        // Arrange
        var assembly = typeof(CalculatorTool).Assembly;
        var provider = new McpFunctionProvider(assembly);
        var functions = provider.GetFunctions().ToList();
        var addFunction = functions.FirstOrDefault(f => f.Contract.Name == "Add");

        // Act
        Assert.NotNull(addFunction);
        var result = await addFunction.Handler("{\"a\": 5, \"b\": 3}");

        // Assert
        Assert.Contains("8", result); // 5 + 3 = 8
    }

    [Fact]
    public void McpFunctionProvider_ShouldWorkWithFunctionRegistry()
    {
        // Arrange
        var assembly = typeof(CalculatorTool).Assembly;
        var provider = new McpFunctionProvider(assembly);
        var registry = new FunctionRegistry();

        // Act
        _ = registry.AddProvider(provider);
        var (contracts, handlers) = registry.Build();

        // Assert
        Assert.NotEmpty(contracts);
        Assert.NotEmpty(handlers);

        // Verify calculator functions are included
        Assert.Contains(contracts, c => c.Name == "Add");
        Assert.Contains(handlers.Keys, k => k == "CalculatorTool-Add");
    }

    [Fact]
    public void McpFunctionProvider_ShouldCreateWorkingMiddleware()
    {
        // Arrange
        var assembly = typeof(CalculatorTool).Assembly;
        var provider = new McpFunctionProvider(assembly);
        var registry = new FunctionRegistry();

        // Act
        _ = registry.AddProvider(provider);
        var middleware = registry.BuildMiddleware("McpTestMiddleware");

        // Assert
        Assert.NotNull(middleware);
        Assert.Equal("McpTestMiddleware", middleware.Name);
    }

    [Fact]
    public void McpFunctionProvider_WithNullAssembly_UsesCallingAssembly()
    {
        // Arrange & Act
        var provider = new McpFunctionProvider();

        // Assert
        Assert.NotNull(provider.ProviderName);
        Assert.Contains("McpIntegrationTests", provider.ProviderName);
    }

    [Fact]
    public void McpFunctionProvider_FunctionDescriptors_HaveCorrectProviderName()
    {
        // Arrange
        var assembly = typeof(CalculatorTool).Assembly;
        var provider = new McpFunctionProvider(assembly, "TestMcpProvider");

        // Act
        var functions = provider.GetFunctions().ToList();

        // Assert
        Assert.All(functions, f => Assert.Equal("TestMcpProvider", f.ProviderName));
    }
}
