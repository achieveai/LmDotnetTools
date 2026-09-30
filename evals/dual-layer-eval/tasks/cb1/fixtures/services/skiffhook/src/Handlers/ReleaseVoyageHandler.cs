using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Skiffhook.Models;

namespace Quillfeather.Services.Skiffhook.Handlers;

/// <summary>
/// Handles ReleaseVoyage requests for skiffhook. Called from the queue consumer.
/// </summary>
public sealed class ReleaseVoyageHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public ReleaseVoyageHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(ReleaseVoyageRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("releaseVoyage received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("skiffhook.releaseVoyage.skipped");
                continue;
            }

            await EnrichAsync(line, ct).ConfigureAwait(false);
            await NormaliseAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("skiffhook.releaseVoyage.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task EnrichAsync(ReleaseVoyageLine line, CancellationToken ct)
    {
        _log.Debug("enrich line", line.Sku);
        return Task.CompletedTask;
    }

    private Task NormaliseAsync(ReleaseVoyageLine line, CancellationToken ct)
    {
        _log.Debug("normalise line", line.Sku);
        return Task.CompletedTask;
    }
}
