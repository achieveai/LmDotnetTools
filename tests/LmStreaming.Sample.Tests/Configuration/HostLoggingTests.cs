using System.Collections.Concurrent;
using LmStreaming.Sample.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Filters;

namespace LmStreaming.Sample.Tests.Configuration;

/// <summary>
/// Covers the host's two request-path logging decisions: request completion lines get no caller info, and a
/// 5xx the caller is told to retry is not logged as an Error.
/// </summary>
public class HostLoggingTests
{
    private sealed class CapturingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    // Stands in for caller info: marks every event the gate let through.
    private sealed class ProbeEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
            logEvent.AddPropertyIfAbsent(new LogEventProperty("Probed", new ScalarValue(true)));
    }

    [Fact]
    public async Task RequestLines_SkipCallerInfo_AndARetryableBusyIsInformation_WhileAppWarningsKeepIt()
    {
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.OnWarningsExceptRequestLines(enrich => enrich.With(new ProbeEnricher()))
            .WriteTo.Sink(sink)
            .CreateLogger();
        using var host = await new HostBuilder()
            .ConfigureWebHost(web =>
                web.UseTestServer()
                    .Configure(app =>
                    {
                        app.UseSerilogRequestLogging(options =>
                        {
                            options.Logger = logger;
                            options.GetLevel = HostLogging.RequestLevel;
                        });
                        app.Run(context =>
                        {
                            if (context.Request.Path == "/busy")
                            {
                                HostLogging.MarkExpectedRetry(context);
                            }

                            context.Response.StatusCode = context.Request.Path == "/busy" ? 503 : 500;
                            return Task.CompletedTask;
                        });
                    })
            )
            .UseSerilog(logger)
            .StartAsync();
        var client = host.GetTestClient();

        _ = await client.GetAsync(new Uri("/busy", UriKind.Relative));
        _ = await client.GetAsync(new Uri("/fault", UriKind.Relative));
        logger.Warning("An app warning");
        logger.Information("An app information line");

        var lines = sink.Events.ToList();
        var fromRequestLogging = Matching.FromSource("Serilog.AspNetCore.RequestLoggingMiddleware");
        LogEvent RequestLine(string status) =>
            lines.Single(e => fromRequestLogging(e) && e.Properties["StatusCode"].ToString() == status);
        var busy = RequestLine("503");
        var fault = RequestLine("500");
        var warning = lines.Single(e => e.MessageTemplate.Text == "An app warning");
        var information = lines.Single(e => e.MessageTemplate.Text == "An app information line");
        busy.Level.Should().Be(LogEventLevel.Information, "the caller was told to retry; the host did not fail");
        fault.Level.Should().Be(LogEventLevel.Error);
        busy.Properties.Should().NotContainKey("Probed");
        fault.Properties.Should().NotContainKey("Probed", "an Error request line still skips the stack walk");
        warning.Properties.Should().ContainKey("Probed", "app warnings keep their caller info");
        information.Properties.Should().NotContainKey("Probed");
    }

    [Theory]
    [InlineData(200, false, false, LogEventLevel.Information)]
    [InlineData(499, false, false, LogEventLevel.Information)]
    [InlineData(500, false, false, LogEventLevel.Error)]
    [InlineData(503, true, false, LogEventLevel.Information)]
    [InlineData(503, true, true, LogEventLevel.Error)]
    [InlineData(200, false, true, LogEventLevel.Error)]
    public void RequestLevel_KeepsSerilogsDefault_ExceptForAMarkedRetry(
        int status,
        bool markedRetry,
        bool threw,
        LogEventLevel expected
    )
    {
        var context = new DefaultHttpContext();
        context.Response.StatusCode = status;
        if (markedRetry)
        {
            HostLogging.MarkExpectedRetry(context);
        }

        HostLogging
            .RequestLevel(context, 1, threw ? new InvalidOperationException("boom") : null)
            .Should()
            .Be(expected);
    }
}
