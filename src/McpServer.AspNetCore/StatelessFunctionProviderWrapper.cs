using AchieveAi.LmDotnetTools.LmCore.Middleware;

namespace AchieveAi.LmDotnetTools.McpServer.AspNetCore;

/// <summary>
/// Wrapper that filters out stateful functions from a provider.
/// Only exposes functions where IsStateful is false.
/// </summary>
internal class StatelessFunctionProviderWrapper : IFunctionProvider
{
    private readonly IFunctionProvider _inner;

    public StatelessFunctionProviderWrapper(IFunctionProvider inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public string ProviderName => _inner.ProviderName;

    public int Priority => _inner.Priority;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        return _inner.GetFunctions().Where(f => !f.IsStateful);
    }
}
