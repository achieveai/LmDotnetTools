using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmConfig.Http;

public static class LmConfigStandardWrappers
{
    public static Func<HttpMessageHandler, ILogger?, HttpMessageHandler> WithRetry(
        int maxAttempts = 3,
        TimeSpan? delay = null) =>
        (inner, _) => new RetryHandler(maxAttempts, delay) { InnerHandler = inner };
} 