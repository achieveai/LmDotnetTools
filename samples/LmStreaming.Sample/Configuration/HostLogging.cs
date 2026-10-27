using Serilog;
using Serilog.Configuration;
using Serilog.Events;
using Serilog.Filters;

namespace LmStreaming.Sample.Configuration;

/// <summary>
/// The two logging decisions the request path depends on: which events get caller info, and the level a
/// request's completion line is written at.
/// </summary>
public static class HostLogging
{
    private const string RequestLogSource = "Serilog.AspNetCore.RequestLoggingMiddleware";

    private static readonly Func<LogEvent, bool> FromRequestLog = Matching.FromSource(RequestLogSource);

    private static readonly object ExpectedRetryKey = new();

    /// <summary>
    /// Applies <paramref name="configure"/> to Warning and above, except the request-logging middleware's
    /// completion lines. The host puts caller info here.
    /// </summary>
    /// <remarks>
    /// Caller info captures and demystifies a full stack trace per event. A request line's stack is the
    /// middleware's continuation, which names no caller worth reporting (the host's request lines never carried
    /// one), and on the review host each walk cost ~165 ms of CPU: polls answered 503 at ~9/s kept 1.5 cores
    /// busy (2026-10-27 profile).
    /// </remarks>
    public static LoggerConfiguration OnWarningsExceptRequestLines(
        this LoggerEnrichmentConfiguration enrich,
        Action<LoggerEnrichmentConfiguration> configure
    )
    {
        ArgumentNullException.ThrowIfNull(enrich);
        ArgumentNullException.ThrowIfNull(configure);
        return enrich.AtLevel(
            LogEventLevel.Warning,
            warnings => warnings.When(logEvent => !FromRequestLog(logEvent), configure)
        );
    }

    /// <summary>
    /// Marks this request's 5xx as a retry the caller is told to make, not a server fault, so its
    /// completion line is not an Error. A controller called outside a request has no context to mark.
    /// </summary>
    public static void MarkExpectedRetry(HttpContext? context)
    {
        if (context is not null)
        {
            context.Items[ExpectedRetryKey] = true;
        }
    }

    /// <summary>
    /// Serilog's default request level (Error for an exception or a 5xx, else Information), except that a
    /// 5xx marked by <see cref="MarkExpectedRetry"/> is Information.
    /// </summary>
    public static LogEventLevel RequestLevel(HttpContext context, double elapsedMs, Exception? exception)
    {
        ArgumentNullException.ThrowIfNull(context);
        return exception is null && (context.Response.StatusCode <= 499 || context.Items.ContainsKey(ExpectedRetryKey))
            ? LogEventLevel.Information
            : LogEventLevel.Error;
    }
}
