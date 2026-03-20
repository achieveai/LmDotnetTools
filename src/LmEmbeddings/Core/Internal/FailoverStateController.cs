namespace AchieveAi.LmDotnetTools.LmEmbeddings.Core.Internal;

internal enum FailoverState
{
    Primary,
    Backup
}

internal class FailoverStateController
{
    private readonly object _lock = new();
    private FailoverState _state = FailoverState.Primary;
    private DateTimeOffset? _nextProbeAt;
    private readonly TimeSpan? _recoveryInterval;
    private bool _probeInProgress;

    public FailoverStateController(TimeSpan? recoveryInterval)
    {
        _recoveryInterval = recoveryInterval;
    }

    public bool ShouldUsePrimary()
    {
        lock (_lock)
        {
            if (_state == FailoverState.Primary)
            {
                return true;
            }

            if (_nextProbeAt.HasValue
                && DateTimeOffset.UtcNow >= _nextProbeAt.Value
                && !_probeInProgress)
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
            _nextProbeAt = _recoveryInterval.HasValue
                ? DateTimeOffset.UtcNow.Add(_recoveryInterval.Value)
                : null;
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
