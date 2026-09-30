using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Skiffscope.Models;

namespace Quillfeather.Services.Skiffscope.Handlers;

/// <summary>
/// Handles MergeHold requests for skiffscope. Idempotent by request id.
/// </summary>
public sealed class MergeHoldHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public MergeHoldHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(MergeHoldRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("mergeHold received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("skiffscope.mergeHold.skipped");
                continue;
            }

            await ValidateAsync(line, ct).ConfigureAwait(false);
            await MeasureAsync(line, ct).ConfigureAwait(false);
            await PublishAsync(line, ct).ConfigureAwait(false);
            await NormaliseAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("skiffscope.mergeHold.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task ValidateAsync(MergeHoldLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("validate note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task MeasureAsync(MergeHoldLine line, CancellationToken ct)
    {
        _metrics.Increment("skiffscope.mergeHold.measure");
        return Task.CompletedTask;
    }

    private Task PublishAsync(MergeHoldLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("publish note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task NormaliseAsync(MergeHoldLine line, CancellationToken ct)
    {
        _log.Debug("normalise line", line.Sku);
        return Task.CompletedTask;
    }
}
