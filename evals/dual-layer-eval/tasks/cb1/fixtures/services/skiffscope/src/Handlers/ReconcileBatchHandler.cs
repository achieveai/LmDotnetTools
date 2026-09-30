using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Skiffscope.Models;

namespace Quillfeather.Services.Skiffscope.Handlers;

/// <summary>
/// Handles ReconcileBatch requests for skiffscope. Safe to retry.
/// </summary>
public sealed class ReconcileBatchHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public ReconcileBatchHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(ReconcileBatchRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("reconcileBatch received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("skiffscope.reconcileBatch.skipped");
                continue;
            }

            await PersistAsync(line, ct).ConfigureAwait(false);
            await PublishAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("skiffscope.reconcileBatch.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task PersistAsync(ReconcileBatchLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("persist note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task PublishAsync(ReconcileBatchLine line, CancellationToken ct)
    {
        _log.Debug("publish line", line.Sku);
        return Task.CompletedTask;
    }
}
