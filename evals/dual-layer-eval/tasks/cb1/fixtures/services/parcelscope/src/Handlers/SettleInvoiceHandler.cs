using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Parcelscope.Models;

namespace Quillfeather.Services.Parcelscope.Handlers;

/// <summary>
/// Handles SettleInvoice requests for parcelscope. Called from the queue consumer.
/// </summary>
public sealed class SettleInvoiceHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public SettleInvoiceHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(SettleInvoiceRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("settleInvoice received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("parcelscope.settleInvoice.skipped");
                continue;
            }

            await MeasureAsync(line, ct).ConfigureAwait(false);
            await PublishAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("parcelscope.settleInvoice.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task MeasureAsync(SettleInvoiceLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("measure note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task PublishAsync(SettleInvoiceLine line, CancellationToken ct)
    {
        _metrics.Increment("parcelscope.settleInvoice.publish");
        return Task.CompletedTask;
    }
}
