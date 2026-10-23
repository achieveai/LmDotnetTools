using Microsoft.Data.Sqlite;

namespace LmStreaming.Sample.Persistence;

internal sealed class SqliteWalCheckpointService : BackgroundService
{
    private static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    private readonly string _databasePath;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _interval;
    private readonly ILogger<SqliteWalCheckpointService> _logger;

    // The path is the one the conversation store opened, passed in rather than re-read from
    // configuration, so the two can never disagree about which file to checkpoint.
    public SqliteWalCheckpointService(
        string databasePath,
        IConfiguration configuration,
        ILogger<SqliteWalCheckpointService> logger
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
        _initialDelay =
            configuration.GetValue<TimeSpan?>("LmStreaming:WalCheckpoint:InitialDelay") ?? DefaultInitialDelay;
        _interval = configuration.GetValue<TimeSpan?>("LmStreaming:WalCheckpoint:Interval") ?? DefaultInterval;
        _logger = logger;

        if (_initialDelay < TimeSpan.Zero)
        {
            throw new InvalidOperationException("LmStreaming:WalCheckpoint:InitialDelay must not be negative.");
        }

        if (_interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("LmStreaming:WalCheckpoint:Interval must be greater than zero.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_initialDelay, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(_interval);

            do
            {
                await TryCheckpointAsync(stoppingToken).ConfigureAwait(false);
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task TryCheckpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = _databasePath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false,
                }.ToString()
            );
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var busyTimeoutCommand = connection.CreateCommand())
            {
                busyTimeoutCommand.CommandText = "PRAGMA busy_timeout=0;";
                _ = await busyTimeoutCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var passive = await ExecuteCheckpointAsync(connection, "PASSIVE", cancellationToken).ConfigureAwait(false);
            if (passive.Busy != 0 || passive.LogFrames != passive.CheckpointedFrames)
            {
                _logger.LogDebug(
                    "SQLite WAL passive checkpoint was busy or partial: busy={Busy}, "
                        + "checkpointedFrames={CheckpointedFrames}, logFrames={LogFrames}; "
                        + "truncate deferred.",
                    passive.Busy,
                    passive.CheckpointedFrames,
                    passive.LogFrames
                );
                return;
            }

            var truncate = await ExecuteCheckpointAsync(connection, "TRUNCATE", cancellationToken)
                .ConfigureAwait(false);
            if (truncate.Busy == 0 && truncate.LogFrames == 0)
            {
                // An already-empty WAL is still truncated (its file can outlive its frames), but
                // only a truncate that moved frames is worth an Information line every interval.
                _logger.Log(
                    passive.LogFrames > 0 ? LogLevel.Information : LogLevel.Debug,
                    "SQLite WAL truncated after checkpointing {FrameCount} frame(s).",
                    passive.LogFrames
                );
                return;
            }

            _logger.LogDebug(
                "SQLite WAL truncate was busy or partial: busy={Busy}, "
                    + "checkpointedFrames={CheckpointedFrames}, logFrames={LogFrames}; "
                    + "will retry next interval.",
                truncate.Busy,
                truncate.CheckpointedFrames,
                truncate.LogFrames
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "SQLite WAL checkpoint failed for {DatabasePath}; will retry next interval.",
                _databasePath
            );
        }
    }

    private static async Task<(int Busy, int LogFrames, int CheckpointedFrames)> ExecuteCheckpointAsync(
        SqliteConnection connection,
        string mode,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA wal_checkpoint({mode});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"PRAGMA wal_checkpoint({mode}) returned no result.");
        }

        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }
}
