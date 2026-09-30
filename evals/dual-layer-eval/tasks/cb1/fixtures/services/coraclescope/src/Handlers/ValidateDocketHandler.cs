using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Coraclescope.Models;

namespace Quillfeather.Services.Coraclescope.Handlers;

/// <summary>
/// Handles ValidateDocket requests for coraclescope. Called from the queue consumer.
/// </summary>
public sealed class ValidateDocketHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public ValidateDocketHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(ValidateDocketRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("validateDocket received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("coraclescope.validateDocket.skipped");
                continue;
            }

            await PersistAsync(line, ct).ConfigureAwait(false);
            await NormaliseAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("coraclescope.validateDocket.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task PersistAsync(ValidateDocketLine line, CancellationToken ct)
    {
        _log.Debug("persist line", line.Sku);
        return Task.CompletedTask;
    }

    private Task NormaliseAsync(ValidateDocketLine line, CancellationToken ct)
    {
        _metrics.Increment("coraclescope.validateDocket.normalise");
        return Task.CompletedTask;
    }
}
