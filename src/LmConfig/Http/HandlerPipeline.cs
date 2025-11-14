using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmConfig.Http;

/// <summary>
/// Abstraction for composing a chain of <see cref="HttpMessageHandler"/> instances.
/// Implementations should return the *outer-most* handler that ultimately delegates
/// to the provided inner handler.
/// </summary>
public interface IHttpHandlerBuilder
{
    HttpMessageHandler Build(HttpMessageHandler innerMost, ILogger? logger = null);
}

/// <summary>
/// Simple fluent implementation that lets callers register wrapper lambdas.
/// </summary>
public sealed class HandlerBuilder : IHttpHandlerBuilder
{
    private readonly IList<Func<HttpMessageHandler, ILogger?, HttpMessageHandler>> _wrappers = [];

    /// <summary>
    /// Registers a wrapper. The lambda receives the handler to wrap and must return the new outer handler.
    /// </summary>
    public HandlerBuilder Use(Func<HttpMessageHandler, ILogger?, HttpMessageHandler> wrapper)
    {
        _wrappers.Add(wrapper);
        return this;
    }

    public HttpMessageHandler Build(HttpMessageHandler innerMost, ILogger? logger = null)
    {
        var handler = innerMost;
        for (var i = _wrappers.Count - 1; i >= 0; i--)
        {
            handler = _wrappers[i](handler, logger);
        }

        return handler;
    }
}
