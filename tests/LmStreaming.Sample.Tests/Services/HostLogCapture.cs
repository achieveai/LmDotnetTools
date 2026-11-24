using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Keeps every event the test host logs, and lets a test wait for one. The host's Serilog setup reads sinks from its
/// services, so <see cref="Install"/> is all a test host needs.
/// </summary>
internal sealed class HostLogCapture : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();
    private readonly ConcurrentQueue<(Func<LogEvent, bool> Match, TaskCompletionSource<LogEvent> Seen)> _waiters =
        new();

    public IReadOnlyList<LogEvent> Events => [.. _events];

    /// <summary>Adds a capture to the host's services.</summary>
    public static HostLogCapture Install(IServiceCollection services)
    {
        var capture = new HostLogCapture();
        services.AddSingleton<ILogEventSink>(capture);
        return capture;
    }

    public void Emit(LogEvent logEvent)
    {
        _events.Enqueue(logEvent);
        foreach (var (match, seen) in _waiters)
        {
            if (match(logEvent))
            {
                _ = seen.TrySetResult(logEvent);
            }
        }
    }

    /// <summary>The first event, logged from now on, whose template is <paramref name="template"/> and whose
    /// <c>ThreadId</c> is <paramref name="threadId"/>.</summary>
    public Task<LogEvent> WaitForAsync(string template, string threadId, TimeSpan patience) =>
        WaitForAsync(template, "ThreadId", threadId, patience);

    /// <summary>The first event, logged from now on, whose template is <paramref name="template"/> and whose
    /// <paramref name="property"/> is <paramref name="value"/>.</summary>
    public Task<LogEvent> WaitForAsync(string template, string property, string value, TimeSpan patience)
    {
        var seen = new TaskCompletionSource<LogEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Enqueue(
            (
                logEvent =>
                    logEvent.MessageTemplate.Text == template
                    && logEvent.Properties.TryGetValue(property, out var logged)
                    && logged is ScalarValue { Value: string id }
                    && id == value,
                seen
            )
        );
        return seen.Task.WaitAsync(patience);
    }
}
