using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Berthyard.Models;

namespace Quillfeather.Services.Berthyard.Handlers;

/// <summary>
/// Handles ValidateBooking requests for berthyard. Safe to retry.
/// </summary>
public sealed class ValidateBookingHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public ValidateBookingHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(ValidateBookingRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("validateBooking received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("berthyard.validateBooking.skipped");
                continue;
            }

            await MeasureAsync(line, ct).ConfigureAwait(false);
            await EnrichAsync(line, ct).ConfigureAwait(false);
            await PersistAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("berthyard.validateBooking.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task MeasureAsync(ValidateBookingLine line, CancellationToken ct)
    {
        _metrics.Increment("berthyard.validateBooking.measure");
        return Task.CompletedTask;
    }

    private Task EnrichAsync(ValidateBookingLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("enrich note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task PersistAsync(ValidateBookingLine line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line.Sku))
        {
            throw new ArgumentException("sku is required", nameof(line));
        }

        return Task.CompletedTask;
    }
}
