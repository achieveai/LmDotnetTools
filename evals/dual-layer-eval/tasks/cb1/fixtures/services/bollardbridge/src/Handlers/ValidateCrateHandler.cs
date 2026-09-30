using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Bollardbridge.Models;

namespace Quillfeather.Services.Bollardbridge.Handlers;

/// <summary>
/// Handles ValidateCrate requests for bollardbridge. Idempotent by request id.
/// </summary>
public sealed class ValidateCrateHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public ValidateCrateHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(ValidateCrateRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("validateCrate received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("bollardbridge.validateCrate.skipped");
                continue;
            }

            await MeasureAsync(line, ct).ConfigureAwait(false);
            await PersistAsync(line, ct).ConfigureAwait(false);
            await ValidateAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("bollardbridge.validateCrate.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task MeasureAsync(ValidateCrateLine line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line.Sku))
        {
            throw new ArgumentException("sku is required", nameof(line));
        }

        return Task.CompletedTask;
    }

    private Task PersistAsync(ValidateCrateLine line, CancellationToken ct)
    {
        _metrics.Increment("bollardbridge.validateCrate.persist");
        return Task.CompletedTask;
    }

    private Task ValidateAsync(ValidateCrateLine line, CancellationToken ct)
    {
        _log.Debug("validate line", line.Sku);
        return Task.CompletedTask;
    }
}
