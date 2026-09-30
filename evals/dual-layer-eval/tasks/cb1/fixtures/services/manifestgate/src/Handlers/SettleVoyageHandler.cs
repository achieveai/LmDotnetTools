using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Manifestgate.Models;

namespace Quillfeather.Services.Manifestgate.Handlers;

/// <summary>
/// Handles SettleVoyage requests for manifestgate. Called from the queue consumer.
/// </summary>
public sealed class SettleVoyageHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public SettleVoyageHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(SettleVoyageRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("settleVoyage received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("manifestgate.settleVoyage.skipped");
                continue;
            }

            await MeasureAsync(line, ct).ConfigureAwait(false);
            await ValidateAsync(line, ct).ConfigureAwait(false);
            await NormaliseAsync(line, ct).ConfigureAwait(false);
            await PublishAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("manifestgate.settleVoyage.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task MeasureAsync(SettleVoyageLine line, CancellationToken ct)
    {
        _metrics.Increment("manifestgate.settleVoyage.measure");
        return Task.CompletedTask;
    }

    private Task ValidateAsync(SettleVoyageLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("validate note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task NormaliseAsync(SettleVoyageLine line, CancellationToken ct)
    {
        _metrics.Increment("manifestgate.settleVoyage.normalise");
        return Task.CompletedTask;
    }

    private Task PublishAsync(SettleVoyageLine line, CancellationToken ct)
    {
        _metrics.Increment("manifestgate.settleVoyage.publish");
        return Task.CompletedTask;
    }
}
