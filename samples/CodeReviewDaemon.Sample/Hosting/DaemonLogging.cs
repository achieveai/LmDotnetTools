using Serilog;
using Serilog.Formatting.Compact;

namespace CodeReviewDaemon.Sample.Hosting;

/// <summary>
/// Opt-in structured logging for the daemon. When <c>CodeReviewDaemon:LogFilePath</c> is set, the daemon's
/// own logs are ALSO written as JSONL (Serilog <see cref="CompactJsonFormatter"/>, daily-rolled) alongside
/// the console logger, so they are queryable with DuckDB for later review — the console output (redirected
/// to the process log) is unchanged.
/// </summary>
internal static class DaemonLogging
{
    /// <summary>
    /// Adds a Serilog CompactJsonFormatter (JSONL) file sink to <paramref name="logging"/> at
    /// <paramref name="path"/> (daily-rolled), enriched with the ambient <c>LogContext</c>. Canonical fields:
    /// <c>@t</c> (ISO-8601 UTC), <c>@l</c> (level), <c>@m</c> (message), <c>@mt</c> (template),
    /// <c>SourceContext</c> (component), plus any structured properties as top-level fields.
    /// </summary>
    public static void AddJsonlFileSink(ILoggingBuilder logging, string path)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(new CompactJsonFormatter(), path, rollingInterval: RollingInterval.Day)
            .CreateLogger();

        // Added alongside (not replacing) the default console provider, so the existing process log stays.
        _ = logging.AddSerilog(logger, dispose: true);
    }
}
