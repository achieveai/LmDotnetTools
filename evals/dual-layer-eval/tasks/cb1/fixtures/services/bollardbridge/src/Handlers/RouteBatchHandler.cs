using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Bollardbridge.Models;

namespace Quillfeather.Services.Bollardbridge.Handlers;

/// <summary>
/// Handles RouteBatch requests for bollardbridge. Called from the queue consumer.
/// </summary>
public sealed class RouteBatchHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public RouteBatchHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(RouteBatchRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("routeBatch received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("bollardbridge.routeBatch.skipped");
                continue;
            }

            await PersistAsync(line, ct).ConfigureAwait(false);
            await NormaliseAsync(line, ct).ConfigureAwait(false);
            await PublishAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("bollardbridge.routeBatch.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task PersistAsync(RouteBatchLine line, CancellationToken ct)
    {
        _log.Debug("persist line", line.Sku);
        return Task.CompletedTask;
    }

    private Task NormaliseAsync(RouteBatchLine line, CancellationToken ct)
    {
        _metrics.Increment("bollardbridge.routeBatch.normalise");
        return Task.CompletedTask;
    }

    private Task PublishAsync(RouteBatchLine line, CancellationToken ct)
    {
        _metrics.Increment("bollardbridge.routeBatch.publish");
        return Task.CompletedTask;
    }
}
