using System.Net.Http;
using AchieveAi.LmDotnetTools.Misc.Storage;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Http;
using AchieveAi.LmDotnetTools.LmConfig.Http;
using Microsoft.Extensions.Logging;
using AchieveAi.LmDotnetTools.Misc.Utils;

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
        LlmCacheOptions options) =>
        (inner, logger) => new CachingHttpMessageHandler(store, options, inner, logger);

    // Future: add a retry wrapper here or inside LmConfig depending on policy location.
} 