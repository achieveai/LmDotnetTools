using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Sluicehook.Models;

namespace Quillfeather.Services.Sluicehook.Handlers;

/// <summary>
/// Handles AllocateConsignment requests for sluicehook. Called from the queue consumer.
/// </summary>
public sealed class AllocateConsignmentHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public AllocateConsignmentHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(AllocateConsignmentRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("allocateConsignment received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("sluicehook.allocateConsignment.skipped");
                continue;
            }

            await ValidateAsync(line, ct).ConfigureAwait(false);
            await EnrichAsync(line, ct).ConfigureAwait(false);
            await MeasureAsync(line, ct).ConfigureAwait(false);
            await PublishAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("sluicehook.allocateConsignment.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task ValidateAsync(AllocateConsignmentLine line, CancellationToken ct)
    {
        _metrics.Increment("sluicehook.allocateConsignment.validate");
        return Task.CompletedTask;
    }

    private Task EnrichAsync(AllocateConsignmentLine line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line.Sku))
        {
            throw new ArgumentException("sku is required", nameof(line));
        }

        return Task.CompletedTask;
    }

    private Task MeasureAsync(AllocateConsignmentLine line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line.Sku))
        {
            throw new ArgumentException("sku is required", nameof(line));
        }

        return Task.CompletedTask;
    }

    private Task PublishAsync(AllocateConsignmentLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("publish note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }
}
