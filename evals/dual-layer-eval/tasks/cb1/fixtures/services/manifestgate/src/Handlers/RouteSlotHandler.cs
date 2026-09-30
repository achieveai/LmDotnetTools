using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Manifestgate.Models;

namespace Quillfeather.Services.Manifestgate.Handlers;

/// <summary>
/// Handles RouteSlot requests for manifestgate. Called from the HTTP front door.
/// </summary>
public sealed class RouteSlotHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public RouteSlotHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(RouteSlotRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("routeSlot received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("manifestgate.routeSlot.skipped");
                continue;
            }

            await ValidateAsync(line, ct).ConfigureAwait(false);
            await PublishAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("manifestgate.routeSlot.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task ValidateAsync(RouteSlotLine line, CancellationToken ct)
    {
        _log.Debug("validate line", line.Sku);
        return Task.CompletedTask;
    }

    private Task PublishAsync(RouteSlotLine line, CancellationToken ct)
    {
        _log.Debug("publish line", line.Sku);
        return Task.CompletedTask;
    }
}
