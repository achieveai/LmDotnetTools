namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

/// <summary>
/// What a deferred-tool provider does to the registry: it decides which contracts a request advertises,
/// never which handlers exist, and without one the registry answers exactly as it did before.
/// </summary>
public class FunctionRegistryDeferredToolTests
{
    [Fact]
    public void WithoutADeferredProviderBuildContractsIsBuildsContractsInTheSameOrder()
    {
        var registry = new FunctionRegistry();
        _ = registry.AddProvider(new NamedProvider("p", ["gamma", "alpha", "beta"]));
        _ = registry.AddFunction(FunctionRegistryTests.CreateTestContract("delta"), Handler);

        var (built, _) = registry.Build();

        // Order matters: the tools[] a request carries, and so its prompt-cache prefix, must not move.
        Assert.Equal(built.Select(c => c.Name), registry.BuildContracts().Select(c => c.Name));
    }

    [Fact]
    public void EveryDeferredProviderMustAdvertiseAContractAndNoneRemovesAHandler()
    {
        var registry = new FunctionRegistry();
        _ = registry.AddProvider(new NamedProvider("p", ["Read", "Bash", "Target"]));
        _ = registry.AddProvider(new Hiding("hides-bash", "Bash"));
        _ = registry.AddProvider(new Hiding("hides-target", "Target"));

        string[] advertised = ["Read"];
        Assert.Equal(advertised, registry.BuildContracts().Select(c => c.Name));

        var (contracts, handlers) = registry.Build();
        string[] registered = ["Bash", "Read", "Target"];
        Assert.Equal(registered, contracts.Select(c => c.Name).Order());
        Assert.Equal(registered, handlers.Keys.Order());
    }

    [Theory]
    [InlineData("", "{}")]
    [InlineData("Target", "")]
    public void RunNeedsAToolAndItsArguments(string functionName, string argumentsJson)
    {
        _ = Assert.Throws<ArgumentException>(() => ToolCallUnwrap.Run(functionName, argumentsJson));
    }

    [Theory]
    [InlineData("", "tool_not_available")]
    [InlineData("""{"error":"no"}""", "")]
    public void RefuseNeedsAnErrorAndACode(string errorJson, string errorCode)
    {
        _ = Assert.Throws<ArgumentException>(() => ToolCallUnwrap.Refuse(errorJson, errorCode));
    }

    private static readonly ToolHandler Handler = FunctionRegistryTests.CreateTestHandler("ok");

    private sealed class NamedProvider(string name, string[] functionNames) : IFunctionProvider
    {
        public string ProviderName => name;

        public int Priority => 100;

        public IEnumerable<FunctionDescriptor> GetFunctions() =>
            functionNames.Select(f => new FunctionDescriptor
            {
                Contract = FunctionRegistryTests.CreateTestContract(f),
                Handler = Handler,
                ProviderName = name,
            });
    }

    /// <summary>A deferred provider with no functions of its own that hides one name.</summary>
    private sealed class Hiding(string name, string hidden) : IDeferredToolProvider
    {
        public string ProviderName => name;

        public int Priority => 1000;

        public IEnumerable<FunctionDescriptor> GetFunctions() => [];

        public bool IsAdvertised(string functionName) => functionName != hidden;

        public ToolCallUnwrap? TryUnwrap(string functionName, string argumentsJson) => null;
    }
}
