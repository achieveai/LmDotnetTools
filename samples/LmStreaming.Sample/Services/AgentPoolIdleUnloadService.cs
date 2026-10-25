using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Unloads pooled conversations that have sat idle, so the host holds only conversations with work in
/// hand (#830). The next request for an unloaded conversation rebuilds it from the store.
/// </summary>
/// <remarks>
/// <para>
/// Without it the pool kept every conversation it had ever loaded, each with its sub-agent loops, until
/// the conversation was deleted: about 36 MB per finished review, never returned. A review host reached
/// 11 GB after ~280 reviews and took the WSL VM down with it.
/// </para>
/// <para>
/// <c>LmStreaming:AgentPool:IdleUnloadAfter</c> is how long a conversation must go unused before it is
/// unloaded (default 5 minutes; zero or negative turns unloading off). <c>IdleSweepInterval</c> is how
/// often the pool is checked (default 30 seconds). What counts as idle is the pool's call - see
/// <see cref="MultiTurnAgentPool.UnloadIdleAgentsAsync"/>.
/// </para>
/// </remarks>
internal sealed class AgentPoolIdleUnloadService : BackgroundService
{
    private static readonly TimeSpan DefaultIdleUnloadAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(30);

    private readonly MultiTurnAgentPool _pool;
    private readonly TimeSpan _idleUnloadAfter;
    private readonly TimeSpan _sweepInterval;
    private readonly ILogger<AgentPoolIdleUnloadService> _logger;

    public AgentPoolIdleUnloadService(
        MultiTurnAgentPool pool,
        IConfiguration configuration,
        ILogger<AgentPoolIdleUnloadService> logger
    )
    {
        _pool = pool;
        _idleUnloadAfter =
            configuration.GetValue<TimeSpan?>("LmStreaming:AgentPool:IdleUnloadAfter") ?? DefaultIdleUnloadAfter;
        _sweepInterval =
            configuration.GetValue<TimeSpan?>("LmStreaming:AgentPool:IdleSweepInterval") ?? DefaultSweepInterval;
        _logger = logger;

        if (_sweepInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("LmStreaming:AgentPool:IdleSweepInterval must be greater than zero.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_idleUnloadAfter <= TimeSpan.Zero)
        {
            _logger.LogWarning(
                "Idle conversation unloading is OFF (LmStreaming:AgentPool:IdleUnloadAfter = {IdleUnloadAfter}); "
                    + "every conversation stays in memory until it is deleted",
                _idleUnloadAfter
            );
            return;
        }

        _logger.LogInformation(
            "Unloading conversations idle for {IdleUnloadAfter}, checked every {SweepInterval}",
            _idleUnloadAfter,
            _sweepInterval
        );

        try
        {
            using var timer = new PeriodicTimer(_sweepInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await TrySweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task TrySweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await _pool.UnloadIdleAgentsAsync(_idleUnloadAfter, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The pool is shutting down with the host; the next tick ends the loop.
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Idle conversation sweep failed; will retry next interval");
        }
    }
}
