using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Tallyyard.Models;

namespace Quillfeather.Services.Tallyyard.Handlers;

/// <summary>
/// Handles AllocateSlot requests for tallyyard. Idempotent by request id.
/// </summary>
public sealed class AllocateSlotHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public AllocateSlotHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(AllocateSlotRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("allocateSlot received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("tallyyard.allocateSlot.skipped");
                continue;
            }

            await PublishAsync(line, ct).ConfigureAwait(false);
            await ValidateAsync(line, ct).ConfigureAwait(false);
            await MeasureAsync(line, ct).ConfigureAwait(false);
            await PersistAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("tallyyard.allocateSlot.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task PublishAsync(AllocateSlotLine line, CancellationToken ct)
    {
        _metrics.Increment("tallyyard.allocateSlot.publish");
        return Task.CompletedTask;
    }

    private Task ValidateAsync(AllocateSlotLine line, CancellationToken ct)
    {
        _metrics.Increment("tallyyard.allocateSlot.validate");
        return Task.CompletedTask;
    }

    private Task MeasureAsync(AllocateSlotLine line, CancellationToken ct)
    {
        _log.Debug("measure line", line.Sku);
        return Task.CompletedTask;
    }

    private Task PersistAsync(AllocateSlotLine line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line.Sku))
        {
            throw new ArgumentException("sku is required", nameof(line));
        }

        return Task.CompletedTask;
    }
}
