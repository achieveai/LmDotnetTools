using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Berthyard.Models;

namespace Quillfeather.Services.Berthyard.Handlers;

/// <summary>
/// Handles DispatchVoyage requests for berthyard. Called from the queue consumer.
/// </summary>
public sealed class DispatchVoyageHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public DispatchVoyageHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(DispatchVoyageRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("dispatchVoyage received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("berthyard.dispatchVoyage.skipped");
                continue;
            }

            await NormaliseAsync(line, ct).ConfigureAwait(false);
            await PublishAsync(line, ct).ConfigureAwait(false);
            await MeasureAsync(line, ct).ConfigureAwait(false);
            await PersistAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("berthyard.dispatchVoyage.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task NormaliseAsync(DispatchVoyageLine line, CancellationToken ct)
    {
        _metrics.Increment("berthyard.dispatchVoyage.normalise");
        return Task.CompletedTask;
    }

    private Task PublishAsync(DispatchVoyageLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("publish note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task MeasureAsync(DispatchVoyageLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("measure note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task PersistAsync(DispatchVoyageLine line, CancellationToken ct)
    {
        _log.Debug("persist line", line.Sku);
        return Task.CompletedTask;
    }
}
