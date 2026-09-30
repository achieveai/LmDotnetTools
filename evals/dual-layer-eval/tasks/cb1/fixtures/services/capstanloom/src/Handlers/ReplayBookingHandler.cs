using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Capstanloom.Models;

namespace Quillfeather.Services.Capstanloom.Handlers;

/// <summary>
/// Handles ReplayBooking requests for capstanloom. Called from the HTTP front door.
/// </summary>
public sealed class ReplayBookingHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public ReplayBookingHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(ReplayBookingRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("replayBooking received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("capstanloom.replayBooking.skipped");
                continue;
            }

            await EnrichAsync(line, ct).ConfigureAwait(false);
            await NormaliseAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("capstanloom.replayBooking.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task EnrichAsync(ReplayBookingLine line, CancellationToken ct)
    {
        _log.Debug("enrich line", line.Sku);
        return Task.CompletedTask;
    }

    private Task NormaliseAsync(ReplayBookingLine line, CancellationToken ct)
    {
        _metrics.Increment("capstanloom.replayBooking.normalise");
        return Task.CompletedTask;
    }
}
