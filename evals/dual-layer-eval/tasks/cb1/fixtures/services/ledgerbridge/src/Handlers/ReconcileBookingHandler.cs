using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Ledgerbridge.Models;

namespace Quillfeather.Services.Ledgerbridge.Handlers;

/// <summary>
/// Handles ReconcileBooking requests for ledgerbridge. Called from the HTTP front door.
/// </summary>
public sealed class ReconcileBookingHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public ReconcileBookingHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(ReconcileBookingRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("reconcileBooking received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("ledgerbridge.reconcileBooking.skipped");
                continue;
            }

            await ValidateAsync(line, ct).ConfigureAwait(false);
            await PersistAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("ledgerbridge.reconcileBooking.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task ValidateAsync(ReconcileBookingLine line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line.Sku))
        {
            throw new ArgumentException("sku is required", nameof(line));
        }

        return Task.CompletedTask;
    }

    private Task PersistAsync(ReconcileBookingLine line, CancellationToken ct)
    {
        _metrics.Increment("ledgerbridge.reconcileBooking.persist");
        return Task.CompletedTask;
    }
}
