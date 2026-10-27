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
    /// Caller info captures and demystifies a full stack trace per event. A request line is written from the
    /// middleware's continuation, so the first matching frame is whichever awaited call last resumed the
    /// request, not the code that logged: on the review host 45% of Error request lines named the closing brace
    /// of <c>SqliteConversationStore.LoadMetadataAsync</c> and the rest named nothing. Each walk cost ~165 ms of
    /// CPU, and polls answered 503 at ~9/s kept 1.5 cores busy (2026-10-27 profile).
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
    /// Marks this request's 503 as a retry the caller is told to make, not a server fault, so its
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
    /// 503 marked by <see cref="MarkExpectedRetry"/> is Information. Any other marked status keeps the default.
    /// </summary>
    public static LogEventLevel RequestLevel(HttpContext context, double elapsedMs, Exception? exception)
    {
        ArgumentNullException.ThrowIfNull(context);
        var status = context.Response.StatusCode;
        var expectedRetry =
            status == StatusCodes.Status503ServiceUnavailable && context.Items.ContainsKey(ExpectedRetryKey);
        return exception is null && (status <= 499 || expectedRetry) ? LogEventLevel.Information : LogEventLevel.Error;
    }
}
