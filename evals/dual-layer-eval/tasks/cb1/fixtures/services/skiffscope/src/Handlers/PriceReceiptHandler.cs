using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Skiffscope.Models;

namespace Quillfeather.Services.Skiffscope.Handlers;

/// <summary>
/// Handles PriceReceipt requests for skiffscope. Safe to retry.
/// </summary>
public sealed class PriceReceiptHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public PriceReceiptHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(PriceReceiptRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("priceReceipt received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("skiffscope.priceReceipt.skipped");
                continue;
            }

            await PublishAsync(line, ct).ConfigureAwait(false);
            await EnrichAsync(line, ct).ConfigureAwait(false);
            await MeasureAsync(line, ct).ConfigureAwait(false);
            await NormaliseAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("skiffscope.priceReceipt.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task PublishAsync(PriceReceiptLine line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line.Sku))
        {
            throw new ArgumentException("sku is required", nameof(line));
        }

        return Task.CompletedTask;
    }

    private Task EnrichAsync(PriceReceiptLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("enrich note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task MeasureAsync(PriceReceiptLine line, CancellationToken ct)
    {
        _metrics.Increment("skiffscope.priceReceipt.measure");
        return Task.CompletedTask;
    }

    private Task NormaliseAsync(PriceReceiptLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("normalise note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }
}
