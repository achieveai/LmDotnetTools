using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Core.Internal;

internal class FailoverExecutor<TService> where TService : class
{
    private readonly TService _primary;
    private readonly TService _backup;
    private readonly FailoverOptions _options;
    private readonly FailoverStateController _stateController;
    private readonly ILogger _logger;
    private readonly string _serviceName;

    public FailoverExecutor(
        TService primary,
        TService backup,
        FailoverOptions options,
        ILogger logger,
        string serviceName)
    {
        _primary = primary;
        _backup = backup;
        _options = options;
        _logger = logger;
        _serviceName = serviceName;
        _stateController = new FailoverStateController(options.RecoveryInterval);
    }

    public async Task<T> ExecuteAsync<T>(
        Func<TService, CancellationToken, Task<T>> operation,
        CancellationToken callerToken)
    {
        if (!_stateController.ShouldUsePrimary())
        {
            _logger.LogDebug("Primary {ServiceName} marked unhealthy; routing request directly to backup.", _serviceName);
            try
            {
                return await operation(_backup, callerToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !callerToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Backup {ServiceName} failed while primary is in cooldown. Both services may be unhealthy.", _serviceName);
                throw;
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeoutCts.CancelAfter(_options.PrimaryRequestTimeout);

        try
        {
            var result = await operation(_primary, timeoutCts.Token).ConfigureAwait(false);
            _stateController.MarkPrimaryRecovered();
            _logger.LogDebug("Primary {ServiceName} request succeeded.", _serviceName);
            return result;
        }
        catch (Exception primaryEx) when (IsFailoverTrigger(primaryEx, callerToken))
        {
            _logger.LogWarning(primaryEx, "Primary {ServiceName} failed; failing over to backup.", _serviceName);
            _stateController.MarkPrimaryUnhealthy();
            try
            {
                return await operation(_backup, callerToken).ConfigureAwait(false);
            }
            // Let caller-initiated cancellation propagate unwrapped; wrap all other failures.
            catch (Exception backupEx) when (backupEx is not OperationCanceledException || !callerToken.IsCancellationRequested)
            {
                _logger.LogError(backupEx, "Backup {ServiceName} also failed after primary failure.", _serviceName);
                throw new PrimaryBackupFailoverException(
                    $"Both primary and backup {_serviceName}s failed.",
                    primaryEx,
                    backupEx);
            }
        }
        catch (Exception unexpectedEx) when (!callerToken.IsCancellationRequested)
        {
            // Ensure probe state is cleaned up for exceptions not in the failover trigger list
            // (e.g., JsonException, ArgumentException). Without this, _probeInProgress stays
            // true permanently, preventing all future recovery probes.
            _stateController.MarkPrimaryUnhealthy();
            _logger.LogError(
                unexpectedEx,
                "Primary {ServiceName} failed with unexpected error type {ErrorType}. Marking unhealthy but not failing over.",
                _serviceName,
                unexpectedEx.GetType().Name);
            throw;
        }
    }

    private bool IsFailoverTrigger(Exception ex, CancellationToken callerToken)
    {
        return !callerToken.IsCancellationRequested
            && (ex is OperationCanceledException or TimeoutException
                || (_options.FailoverOnHttpError && ex is HttpRequestException));
    }

    public void ResetToPrimary()
    {
        _logger.LogInformation("Manual reset: restoring primary {ServiceName}.", _serviceName);
        _stateController.ResetToPrimary();
    }
}
