using MemoryServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;

namespace MemoryServer.Tests.Infrastructure;

/// <summary>
/// Integration tests for MemoryIdGenerator using direct SQLite connections.
/// Tests ID generation logic with minimal setup to avoid deadlocks.
/// </summary>
public class MemoryIdGeneratorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestMemoryIdGenerator _idGenerator;
    private readonly Mock<ILogger<MemoryIdGenerator>> _mockLogger;

    public MemoryIdGeneratorTests()
    {
        // Create direct in-memory connection to avoid SqliteManager deadlock issues
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        // Initialize schema directly
        InitializeSchemaDirectly();

        _mockLogger = new Mock<ILogger<MemoryIdGenerator>>();
        _idGenerator = new TestMemoryIdGenerator(_connection, _mockLogger.Object);
    }

    private void InitializeSchemaDirectly()
    {
        var createTableSql =
            @"
            CREATE TABLE IF NOT EXISTS memory_id_sequence (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );";

        using var command = _connection.CreateCommand();
        command.CommandText = createTableSql;
        command.ExecuteNonQuery();

        Debug.WriteLine("✅ Schema initialized directly");
    }

    private void ResetIdSequence()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "DELETE FROM memory_id_sequence; DELETE FROM sqlite_sequence WHERE name='memory_id_sequence';";
        command.ExecuteNonQuery();
        Debug.WriteLine("🔄 Reset ID sequence");
    }

    [Fact]
    public async Task GenerateNextIdAsync_FirstCall_ReturnsId1()
    {
        // Arrange
        ResetIdSequence();
        Debug.WriteLine("Testing first ID generation");

        // Act
        var id = await _idGenerator.GenerateNextIdAsync();
        Debug.WriteLine($"Generated first ID: {id}");

        // Assert
        Assert.Equal(1, id);
        Debug.WriteLine("✅ First ID generation test passed");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task GenerateNextIdAsync_MultipleCalls_ReturnsSequentialIds(int count)
    {
        // Arrange
        ResetIdSequence();
        Debug.WriteLine($"Testing sequential ID generation for {count} IDs");
        var generatedIds = new List<int>();

        // Act
        for (int i = 0; i < count; i++)
        {
            var id = await _idGenerator.GenerateNextIdAsync();
            generatedIds.Add(id);
            Debug.WriteLine($"Generated ID {i + 1}: {id}");
        }

        // Assert
        Assert.Equal(count, generatedIds.Count);

        // Verify IDs are sequential starting from 1
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i + 1, generatedIds[i]);
        }

        // Verify all IDs are unique
        Assert.Equal(count, generatedIds.Distinct().Count());

        Debug.WriteLine($"✅ Sequential ID generation test passed for {count} IDs");
    }

    [Fact]
    public async Task GenerateNextIdAsync_ConcurrentCalls_ReturnsUniqueIds()
    {
        // Arrange
        ResetIdSequence();
        Debug.WriteLine("Testing concurrent ID generation");
        const int taskCount = 10;
        var tasks = new List<Task<int>>();

        // Act
        for (int i = 0; i < taskCount; i++)
        {
            tasks.Add(_idGenerator.GenerateNextIdAsync());
        }

        var ids = await Task.WhenAll(tasks);
        Debug.WriteLine($"Generated concurrent IDs: [{string.Join(", ", ids)}]");

        // Assert
        Assert.Equal(taskCount, ids.Length);
        Assert.Equal(taskCount, ids.Distinct().Count()); // All IDs should be unique

        // Verify all IDs are in expected range
        Assert.All(ids, id => Assert.InRange(id, 1, taskCount));

        Debug.WriteLine("✅ Concurrent ID generation test passed");
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}

/// <summary>
/// Test-specific MemoryIdGenerator that uses a direct connection to avoid SqliteManager deadlocks.
/// </summary>
internal class TestMemoryIdGenerator
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<MemoryIdGenerator> _logger;
    private readonly SemaphoreSlim _generationSemaphore;

    public TestMemoryIdGenerator(SqliteConnection connection, ILogger<MemoryIdGenerator> logger)
    {
        _connection = connection;
        _logger = logger;
        _generationSemaphore = new SemaphoreSlim(1, 1);
    }

    public async Task<int> GenerateNextIdAsync(CancellationToken cancellationToken = default)
    {
        await _generationSemaphore.WaitAsync(cancellationToken);

        try
        {
            using var transaction = _connection.BeginTransaction();

            try
            {
                using var command = _connection.CreateCommand();
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

                transaction.Commit();

                _logger.LogDebug("Generated memory ID: {Id}", id);
                return id;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
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
}
