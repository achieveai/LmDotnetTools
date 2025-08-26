namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Factory for creating FunctionCallMiddleware with registered providers
/// </summary>
public interface IFunctionCallMiddlewareFactory
{
    /// <summary>
    /// Create FunctionCallMiddleware using all registered providers
    /// </summary>
    /// <param name="name">Optional name for the middleware</param>
    /// <param name="configure">Optional configuration for the function registry</param>
    /// <returns>Configured FunctionCallMiddleware</returns>
    FunctionCallMiddleware Create(string? name = null, Action<FunctionRegistry>? configure = null);

    /// <summary>
    /// Create FunctionCallMiddleware with a result callback
    /// </summary>
    /// <param name="resultCallback">Callback to notify when tool results are available</param>
    /// <param name="name">Optional name for the middleware</param>
    /// <param name="configure">Optional configuration for the function registry</param>
    /// <returns>Configured FunctionCallMiddleware with callback</returns>
    FunctionCallMiddleware Create(
        IToolResultCallback? resultCallback,
        string? name = null,
        Action<FunctionRegistry>? configure = null
    );

    /// <summary>
    /// Create a FunctionRegistry with all registered providers
    /// </summary>
    /// <returns>FunctionRegistry configured with all providers</returns>
    FunctionRegistry CreateRegistry();
}
