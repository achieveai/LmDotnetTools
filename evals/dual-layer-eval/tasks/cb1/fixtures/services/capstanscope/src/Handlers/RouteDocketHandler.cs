using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Capstanscope.Models;

namespace Quillfeather.Services.Capstanscope.Handlers;

/// <summary>
/// Handles RouteDocket requests for capstanscope. Idempotent by request id.
/// </summary>
public sealed class RouteDocketHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public RouteDocketHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(RouteDocketRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("routeDocket received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("capstanscope.routeDocket.skipped");
                continue;
            }

            await PublishAsync(line, ct).ConfigureAwait(false);
            await PersistAsync(line, ct).ConfigureAwait(false);
            await EnrichAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("capstanscope.routeDocket.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task PublishAsync(RouteDocketLine line, CancellationToken ct)
    {
        _log.Debug("publish line", line.Sku);
        return Task.CompletedTask;
    }

    private Task PersistAsync(RouteDocketLine line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line.Sku))
        {
            throw new ArgumentException("sku is required", nameof(line));
        }

        return Task.CompletedTask;
    }

    private Task EnrichAsync(RouteDocketLine line, CancellationToken ct)
    {
        _log.Debug("enrich line", line.Sku);
        return Task.CompletedTask;
    }
}
