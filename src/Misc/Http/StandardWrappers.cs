using AchieveAi.LmDotnetTools.LmConfig.Http;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Utils;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.Misc.Http;

/// <summary>
/// Collection of common <see cref="IHttpHandlerBuilder"/> wrapper helpers.
/// </summary>
public static class StandardWrappers
{
    /// <summary>
    /// Wraps the pipeline with <see cref="CachingHttpMessageHandler"/> that uses the provided KV store.
    /// </summary>
    public static Func<HttpMessageHandler, ILogger?, HttpMessageHandler> WithKvCache(
        IKvStore store,
        LlmCacheOptions options
    ) => (inner, logger) => new CachingHttpMessageHandler(store, options, inner, logger);

    // Future: add a retry wrapper here or inside LmConfig depending on policy location.
}
