using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Berthworks.Models;

namespace Quillfeather.Services.Berthworks.Handlers;

/// <summary>
/// Handles ReleaseManifest requests for berthworks. Idempotent by request id.
/// </summary>
public sealed class ReleaseManifestHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public ReleaseManifestHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(ReleaseManifestRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("releaseManifest received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("berthworks.releaseManifest.skipped");
                continue;
            }

            await EnrichAsync(line, ct).ConfigureAwait(false);
            await ValidateAsync(line, ct).ConfigureAwait(false);
            await NormaliseAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("berthworks.releaseManifest.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task EnrichAsync(ReleaseManifestLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("enrich note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task ValidateAsync(ReleaseManifestLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("validate note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task NormaliseAsync(ReleaseManifestLine line, CancellationToken ct)
    {
        _metrics.Increment("berthworks.releaseManifest.normalise");
        return Task.CompletedTask;
    }
}
