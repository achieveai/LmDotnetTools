using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Coraclegate.Models;

namespace Quillfeather.Services.Coraclegate.Handlers;

/// <summary>
/// Handles PriceHold requests for coraclegate. Idempotent by request id.
/// </summary>
public sealed class PriceHoldHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public PriceHoldHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(PriceHoldRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("priceHold received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("coraclegate.priceHold.skipped");
                continue;
            }

            await NormaliseAsync(line, ct).ConfigureAwait(false);
            await MeasureAsync(line, ct).ConfigureAwait(false);
            await EnrichAsync(line, ct).ConfigureAwait(false);
            await PublishAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("coraclegate.priceHold.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task NormaliseAsync(PriceHoldLine line, CancellationToken ct)
    {
        _log.Debug("normalise line", line.Sku);
        return Task.CompletedTask;
    }

    private Task MeasureAsync(PriceHoldLine line, CancellationToken ct)
    {
        _metrics.Increment("coraclegate.priceHold.measure");
        return Task.CompletedTask;
    }

    private Task EnrichAsync(PriceHoldLine line, CancellationToken ct)
    {
        _log.Debug("enrich line", line.Sku);
        return Task.CompletedTask;
    }

    private Task PublishAsync(PriceHoldLine line, CancellationToken ct)
    {
        _log.Debug("publish line", line.Sku);
        return Task.CompletedTask;
    }
}
