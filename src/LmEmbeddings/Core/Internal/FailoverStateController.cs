namespace AchieveAi.LmDotnetTools.LmEmbeddings.Core.Internal;

internal enum FailoverState
{
    Primary,
    Backup,
}

internal class FailoverStateController
{
    private readonly object _lock = new();
    private FailoverState _state = FailoverState.Primary;
    private DateTimeOffset? _nextProbeAt;
    private readonly TimeSpan? _recoveryInterval;
    private readonly TimeProvider _timeProvider;
    private bool _probeInProgress;

    public FailoverStateController(TimeSpan? recoveryInterval, TimeProvider? timeProvider = null)
    {
        _recoveryInterval = recoveryInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool ShouldUsePrimary()
    {
        lock (_lock)
        {
            if (_state == FailoverState.Primary)
            {
                return true;
            }

            if (_nextProbeAt.HasValue && _timeProvider.GetUtcNow() >= _nextProbeAt.Value && !_probeInProgress)
            {
                _probeInProgress = true;
                return true;
            }

            return false;
        }
    }

    public void MarkPrimaryUnhealthy()
    {
        lock (_lock)
        {
            _state = FailoverState.Backup;
            _probeInProgress = false;
            _nextProbeAt = _recoveryInterval.HasValue ? _timeProvider.GetUtcNow().Add(_recoveryInterval.Value) : null;
        }
    }

    public void MarkPrimaryRecovered()
    {
        lock (_lock)
        {
            _state = FailoverState.Primary;
            _nextProbeAt = null;
            _probeInProgress = false;
        }
    }

    public void ResetToPrimary()
    {
        lock (_lock)
        {
            _state = FailoverState.Primary;
            _nextProbeAt = null;
            _probeInProgress = false;
        }
    }
}
