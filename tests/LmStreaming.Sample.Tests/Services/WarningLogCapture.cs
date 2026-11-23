using System.Collections.Concurrent;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Records the host's warnings with their exceptions, so a test can pin that a refusal is logged with the
/// exception that caused it. The host's Serilog reads sinks from its services.
/// </summary>
internal sealed class WarningLogCapture : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public void AddTo(IServiceCollection services)
    {
        services.AddSingleton<ILogEventSink>(this);
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level >= LogEventLevel.Warning)
        {
            _events.Enqueue(logEvent);
        }
    }

    public void ShouldHoldTheRefusalFor(string threadId)
    {
        _events
            .Where(logEvent => IsRefusalFor(logEvent, threadId))
            .Should()
            .NotBeEmpty("the refusal is logged with the exception that caused it");
    }

    private static bool IsRefusalFor(LogEvent logEvent, string threadId) =>
        logEvent.MessageTemplate.Text.Contains(
            "refused: its mode is not allowed in a dedicated sandbox",
            StringComparison.Ordinal
        )
        && logEvent.Properties.TryGetValue("ThreadId", out var thread)
        && thread is ScalarValue { Value: string loggedThreadId }
        && loggedThreadId == threadId
        && logEvent.Exception is DedicatedSandboxModeUnsupportedException;
}
