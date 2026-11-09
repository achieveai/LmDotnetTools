using System.Net;

namespace AchieveAi.LmDotnetTools.LmCore.Http;

/// <summary>
///     One retry about to happen: what failed, and how long the client will wait before trying again.
/// </summary>
/// <param name="Attempt">The retry number, 1-based. The first retry is attempt 1.</param>
/// <param name="MaxAttempts">Total attempts allowed, the first request included.</param>
/// <param name="StatusCode">The failing HTTP status, or null for a transport failure.</param>
/// <param name="Delay">How long the client waits before the retry.</param>
/// <param name="FromRetryAfter">True when <paramref name="Delay" /> came from the server's Retry-After.</param>
public sealed record HttpRetryNotice(
    int Attempt,
    int MaxAttempts,
    HttpStatusCode? StatusCode,
    TimeSpan Delay,
    bool FromRetryAfter
);
