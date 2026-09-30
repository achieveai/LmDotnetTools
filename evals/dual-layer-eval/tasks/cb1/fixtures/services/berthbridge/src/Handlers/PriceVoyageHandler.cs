using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Berthbridge.Models;

namespace Quillfeather.Services.Berthbridge.Handlers;

/// <summary>
/// Handles PriceVoyage requests for berthbridge. Called from the queue consumer.
/// </summary>
public sealed class PriceVoyageHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public PriceVoyageHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(PriceVoyageRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("priceVoyage received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("berthbridge.priceVoyage.skipped");
                continue;
            }

            await MeasureAsync(line, ct).ConfigureAwait(false);
            await EnrichAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("berthbridge.priceVoyage.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task MeasureAsync(PriceVoyageLine line, CancellationToken ct)
    {
        _metrics.Increment("berthbridge.priceVoyage.measure");
        return Task.CompletedTask;
    }

    private Task EnrichAsync(PriceVoyageLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("enrich note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }
}
