namespace MemoryServer.Infrastructure;

/// <summary>
/// Generates unique integer IDs for memories using a secure auto-incrementing sequence.
/// Provides better LLM integration compared to UUIDs while maintaining uniqueness.
/// Uses Database Session Pattern for reliable connection management.
/// </summary>
public class MemoryIdGenerator
{
    private readonly ISqliteSessionFactory _sessionFactory;
    private readonly ILogger<MemoryIdGenerator> _logger;
    private readonly SemaphoreSlim _generationSemaphore;

    public MemoryIdGenerator(
        ISqliteSessionFactory sessionFactory,
        ILogger<MemoryIdGenerator> logger
    )
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _generationSemaphore = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// Generates the next unique integer ID for a memory.
    /// Uses database sequence to ensure uniqueness across all instances.
    /// </summary>
    public async Task<int> GenerateNextIdAsync(CancellationToken cancellationToken = default)
    {
        await _generationSemaphore.WaitAsync(cancellationToken);

        try
        {
            await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

            return await session.ExecuteInTransactionAsync(
                async (connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;

                    // Insert into sequence table and get the generated ID
                    command.CommandText =
                        @"
                    INSERT INTO memory_id_sequence DEFAULT VALUES;
                    SELECT last_insert_rowid();";

                    var result = await command.ExecuteScalarAsync(cancellationToken);
                    var id = Convert.ToInt32(result);

                    // Validate ID range for safety
                    if (id <= 0)
                    {
                        throw new InvalidOperationException($"Generated ID {id} is not positive");
                    }

                    if (id > int.MaxValue - 1000)
                    {
                        _logger.LogWarning(
                            "Generated ID {Id} is approaching maximum integer value",
                            id
                        );
                    }

                    _logger.LogDebug("Generated memory ID: {Id}", id);
                    return id;
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate memory ID");
            throw new InvalidOperationException("Failed to generate unique memory ID", ex);
        }
        finally
        {
            _generationSemaphore.Release();
        }
    }

    /// <summary>
    /// Generates multiple IDs in a single transaction for batch operations.
    /// </summary>
    public async Task<List<int>> GenerateMultipleIdsAsync(
        int count,
        CancellationToken cancellationToken = default
    )
    {
        if (count <= 0)
            throw new ArgumentException("Count must be positive", nameof(count));

        if (count > 1000)
            throw new ArgumentException(
                "Cannot generate more than 1000 IDs at once",
                nameof(count)
            );

        await _generationSemaphore.WaitAsync(cancellationToken);

        try
        {
            await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

            return await session.ExecuteInTransactionAsync(
                async (connection, transaction) =>
                {
                    var ids = new List<int>();

                    for (int i = 0; i < count; i++)
                    {
                        using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText =
                            @"
                        INSERT INTO memory_id_sequence DEFAULT VALUES;
                        SELECT last_insert_rowid();";

                        var result = await command.ExecuteScalarAsync(cancellationToken);
                        var id = Convert.ToInt32(result);

                        if (id <= 0)
                        {
                            throw new InvalidOperationException(
                                $"Generated ID {id} is not positive"
                            );
                        }

                        ids.Add(id);
                    }

                    _logger.LogDebug(
                        "Generated {Count} memory IDs: {Ids}",
                        count,
                        string.Join(", ", ids)
                    );
                    return ids;
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate {Count} memory IDs", count);
            throw new InvalidOperationException(
                $"Failed to generate {count} unique memory IDs",
                ex
            );
        }
        finally
        {
            _generationSemaphore.Release();
        }
    }

    /// <summary>
    /// Validates that an ID exists in the sequence (for security checks).
    /// </summary>
    public async Task<bool> ValidateIdExistsAsync(
        int id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

            return await session.ExecuteAsync(async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                    SELECT COUNT(*) FROM memory_id_sequence WHERE id <= @id";
                command.Parameters.AddWithValue("@id", id);

                var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                return count > 0;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate ID {Id}", id);
            return false;
        }
    }

    /// <summary>
    /// Gets the current maximum ID in the sequence.
    /// </summary>
    public async Task<int> GetMaxIdAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

            return await session.ExecuteAsync(async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COALESCE(MAX(id), 0) FROM memory_id_sequence";

                var result = await command.ExecuteScalarAsync(cancellationToken);
                return Convert.ToInt32(result);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get maximum ID");
            throw new InvalidOperationException("Failed to get maximum ID", ex);
        }
    }

    /// <summary>
    /// Gets statistics about ID generation.
    /// </summary>
    public async Task<IdGenerationStats> GetStatsAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

            return await session.ExecuteAsync(async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                    SELECT 
                        COUNT(*) as total_generated,
                        MIN(id) as min_id,
                        MAX(id) as max_id
                    FROM memory_id_sequence";

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var totalGeneratedOrdinal = reader.GetOrdinal("total_generated");
                    var minIdOrdinal = reader.GetOrdinal("min_id");
                    var maxIdOrdinal = reader.GetOrdinal("max_id");

                    var totalGenerated = reader.GetInt32(totalGeneratedOrdinal);
                    var minId = reader.IsDBNull(minIdOrdinal) ? 0 : reader.GetInt32(minIdOrdinal);
                    var maxId = reader.IsDBNull(maxIdOrdinal) ? 0 : reader.GetInt32(maxIdOrdinal);

                    return new IdGenerationStats
                    {
                        TotalGenerated = totalGenerated,
                        MinId = minId,
                        MaxId = maxId,
                        RemainingCapacity = int.MaxValue - maxId,
                    };
                }

                return new IdGenerationStats();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get ID generation statistics");
            throw new InvalidOperationException("Failed to get ID generation statistics", ex);
        }
    }
}

/// <summary>
/// Statistics about ID generation.
/// </summary>
public class IdGenerationStats
{
    /// <summary>
    /// Total number of IDs generated.
    /// </summary>
    public int TotalGenerated { get; set; }

    /// <summary>
    /// Minimum ID generated.
    /// </summary>
    public int MinId { get; set; }

    /// <summary>
    /// Maximum ID generated.
    /// </summary>
    public int MaxId { get; set; }

    /// <summary>
    /// Remaining capacity before reaching int.MaxValue.
    /// </summary>
    public long RemainingCapacity { get; set; }

    /// <summary>
    /// Percentage of integer range used.
    /// </summary>
    public double UsagePercentage =>
        TotalGenerated > 0 ? (double)TotalGenerated / int.MaxValue * 100 : 0;
}
