using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Manifestbridge.Models;

namespace Quillfeather.Services.Manifestbridge.Handlers;

/// <summary>
/// Handles DispatchVoyage requests for manifestbridge. Called from the queue consumer.
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
                _metrics.Increment("manifestbridge.dispatchVoyage.skipped");
                continue;
            }

            await MeasureAsync(line, ct).ConfigureAwait(false);
            await PersistAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("manifestbridge.dispatchVoyage.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
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
