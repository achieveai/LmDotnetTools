using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Skiffworks.Models;

namespace Quillfeather.Services.Skiffworks.Handlers;

/// <summary>
/// Handles AllocateSlot requests for skiffworks. Safe to retry.
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
                _metrics.Increment("skiffworks.allocateSlot.skipped");
                continue;
            }

            await MeasureAsync(line, ct).ConfigureAwait(false);
            await PersistAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("skiffworks.allocateSlot.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task MeasureAsync(AllocateSlotLine line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line.Sku))
        {
            throw new ArgumentException("sku is required", nameof(line));
        }

        return Task.CompletedTask;
    }

    private Task PersistAsync(AllocateSlotLine line, CancellationToken ct)
    {
        _metrics.Increment("skiffworks.allocateSlot.persist");
        return Task.CompletedTask;
    }
}
