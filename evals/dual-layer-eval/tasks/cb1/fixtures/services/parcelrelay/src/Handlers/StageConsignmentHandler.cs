using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Parcelrelay.Models;

namespace Quillfeather.Services.Parcelrelay.Handlers;

/// <summary>
/// Handles StageConsignment requests for parcelrelay. Called from the queue consumer.
/// </summary>
public sealed class StageConsignmentHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public StageConsignmentHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(StageConsignmentRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("stageConsignment received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("parcelrelay.stageConsignment.skipped");
                continue;
            }

            await PublishAsync(line, ct).ConfigureAwait(false);
            await MeasureAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("parcelrelay.stageConsignment.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task PublishAsync(StageConsignmentLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("publish note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task MeasureAsync(StageConsignmentLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("measure note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }
}
