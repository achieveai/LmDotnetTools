namespace CodeReviewDaemon.Sample.Orchestration;

internal enum DaemonAdmissionState
{
    Held,
    Active,
    Draining,
    Unhealthy,
    Stopped,
}

internal sealed class DaemonAdmissionCoordinator
{
    private readonly object _sync = new();
    private DaemonAdmissionState _state;
    private int _activeWork;
    private TaskCompletionSource _drained = CompletedDrain();

    public DaemonAdmissionCoordinator(DaemonAdmissionState initialState) => _state = initialState;

    public DaemonAdmissionState State
    {
        get
        {
            lock (_sync)
                return _state;
        }
    }

    public int ActiveWorkCount
    {
        get
        {
            lock (_sync)
                return _activeWork;
        }
    }

    public void Activate()
    {
        lock (_sync)
        {
            if (_state is DaemonAdmissionState.Unhealthy or DaemonAdmissionState.Stopped)
            {
                throw new InvalidOperationException($"Cannot activate a daemon in {_state} state.");
            }
            _state = DaemonAdmissionState.Active;
        }
    }

    public IDisposable? TryAdmit()
    {
        lock (_sync)
        {
            if (_state != DaemonAdmissionState.Active)
            {
                return null;
            }
            if (_activeWork++ == 0)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return new Lease(this);
        }
    }

    public Task BeginDrainAsync(CancellationToken cancellationToken)
    {
        Task drained;
        lock (_sync)
        {
            _state = DaemonAdmissionState.Draining;
            drained = _drained.Task;
        }
        return drained.WaitAsync(cancellationToken);
    }

    public void MarkUnhealthy()
    {
        lock (_sync)
            _state = DaemonAdmissionState.Unhealthy;
    }

    private void Release()
    {
        lock (_sync)
        {
            if (--_activeWork == 0)
            {
                _drained.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource CompletedDrain()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private sealed class Lease(DaemonAdmissionCoordinator owner) : IDisposable
    {
        private DaemonAdmissionCoordinator? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
