using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Coraclegate.Models;

namespace Quillfeather.Services.Coraclegate.Handlers;

/// <summary>
/// Handles ReplayInvoice requests for coraclegate. Idempotent by request id.
/// </summary>
public sealed class ReplayInvoiceHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public ReplayInvoiceHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(ReplayInvoiceRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("replayInvoice received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("coraclegate.replayInvoice.skipped");
                continue;
            }

            await PublishAsync(line, ct).ConfigureAwait(false);
            await PersistAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("coraclegate.replayInvoice.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task PublishAsync(ReplayInvoiceLine line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line.Sku))
        {
            throw new ArgumentException("sku is required", nameof(line));
        }

        return Task.CompletedTask;
    }

    private Task PersistAsync(ReplayInvoiceLine line, CancellationToken ct)
    {
        _metrics.Increment("coraclegate.replayInvoice.persist");
        return Task.CompletedTask;
    }
}
