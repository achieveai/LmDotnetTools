using System;
using System.Linq;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Xunit;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class FunctionRegistryDebugTest
{
    private readonly ITestOutputHelper _output;
    private static readonly string[] functionNames = new[] { "func1" };

    public FunctionRegistryDebugTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Debug_Build_WithExplicitFunction()
    {
        // Arrange
        var registry = new FunctionRegistry();
        var provider = FunctionRegistryTests.CreateTestProvider("test", functionNames);
        var explicitContract = FunctionRegistryTests.CreateTestContract("func1");
        var explicitHandler = FunctionRegistryTests.CreateTestHandler("explicit-result");

        // Act
        registry.AddProvider(provider);
        registry.AddFunction(explicitContract, explicitHandler);
        var (contracts, handlers) = registry.Build();

        // Debug output
        _output.WriteLine($"Number of contracts: {contracts.Count()}");
        _output.WriteLine($"Number of handlers: {handlers.Count}");

        foreach (var contract in contracts)
        {
            _output.WriteLine($"Contract name: {contract.Name}");
        }

        foreach (var key in handlers.Keys)
        {
            _output.WriteLine($"Handler key: {key}");
        }

        // Assert
        Assert.True(handlers.Count > 0, "Should have at least one handler");
    }
}
