using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Ledgerbridge.Models;

namespace Quillfeather.Services.Ledgerbridge.Handlers;

/// <summary>
/// Handles DispatchSlot requests for ledgerbridge. Called from the queue consumer.
/// </summary>
public sealed class DispatchSlotHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public DispatchSlotHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(DispatchSlotRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("dispatchSlot received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("ledgerbridge.dispatchSlot.skipped");
                continue;
            }

            await ValidateAsync(line, ct).ConfigureAwait(false);
            await PublishAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("ledgerbridge.dispatchSlot.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task ValidateAsync(DispatchSlotLine line, CancellationToken ct)
    {
        _metrics.Increment("ledgerbridge.dispatchSlot.validate");
        return Task.CompletedTask;
    }

    private Task PublishAsync(DispatchSlotLine line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line.Sku))
        {
            throw new ArgumentException("sku is required", nameof(line));
        }

        return Task.CompletedTask;
    }
}
