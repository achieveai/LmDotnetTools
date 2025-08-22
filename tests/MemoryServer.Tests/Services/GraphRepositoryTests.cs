using MemoryServer.Infrastructure;
using MemoryServer.Models;
using MemoryServer.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace MemoryServer.Tests.Services;

/// <summary>
/// Comprehensive tests for GraphRepository implementation.
/// Uses data-driven testing approach with file-based SQLite database for realistic testing.
/// ARCHITECTURE: Only the GraphRepository manages database connections to avoid deadlocks.
/// </summary>
[Collection("GraphRepository")]
public class GraphRepositoryTests : IDisposable
{
    private readonly TestSqliteSessionFactory _sessionFactory;
    private readonly GraphRepository _repository;
    private readonly Mock<ILogger<GraphRepository>> _mockLogger;
    private readonly Mock<ILoggerFactory> _mockLoggerFactory;
    private readonly int _instanceId;
    private static int _instanceCounter = 0;

    public GraphRepositoryTests()
    {
        _instanceId = Interlocked.Increment(ref _instanceCounter);

        _mockLogger = new Mock<ILogger<GraphRepository>>();

        // Create a simple logger factory that returns NullLogger instances
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLoggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger>().Object);

        // Create TestSqliteSessionFactory for proper test isolation
        _sessionFactory = new TestSqliteSessionFactory(_mockLoggerFactory.Object);

        // Initialize the repository with session factory
        _repository = new GraphRepository(_sessionFactory, _mockLogger.Object);
    }

    #region Entity CRUD Tests

    [Theory]
    [MemberData(nameof(EntityTestCases))]
    public async Task AddEntityAsync_WithValidData_ShouldSucceed(
        Entity entity,
        SessionContext sessionContext)
    {
        // Arrange
        // Act
        var result = await _repository.AddEntityAsync(entity, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Id > 0, "Entity should have generated ID");
        Assert.Equal(entity.Name, result.Name);
        Assert.Equal(entity.Type, result.Type);
        Assert.Equal(sessionContext.UserId, result.UserId);
    }

    [Theory]
    [MemberData(nameof(EntityTestCases))]
    public async Task GetEntityByIdAsync_WithExistingEntity_ShouldReturnEntity(
        Entity entity,
        SessionContext sessionContext)
    {
        // Arrange
        var addedEntity = await _repository.AddEntityAsync(entity, sessionContext);

        // Act
        var result = await _repository.GetEntityByIdAsync(addedEntity.Id, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(addedEntity.Id, result.Id);
        Assert.Equal(addedEntity.Name, result.Name);
        Assert.Equal(addedEntity.Type, result.Type);
    }

    [Theory]
    [MemberData(nameof(EntityTestCases))]
    public async Task GetEntityByNameAsync_WithExistingEntity_ShouldReturnEntity(
        Entity entity,
        SessionContext sessionContext)
    {
        // Arrange
        await _repository.AddEntityAsync(entity, sessionContext);

        // Act
        var result = await _repository.GetEntityByNameAsync(entity.Name, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(entity.Name, result.Name);
        Assert.Equal(entity.Type, result.Type);
    }

    [Theory]
    [MemberData(nameof(EntityUpdateTestCases))]
    public async Task UpdateEntityAsync_WithValidData_ShouldUpdateSuccessfully(
        Entity originalEntity,
        Entity updatedEntity,
        SessionContext sessionContext)
    {
        // Arrange
        var addedEntity = await _repository.AddEntityAsync(originalEntity, sessionContext);
        updatedEntity.Id = addedEntity.Id;

        // Act
        var result = await _repository.UpdateEntityAsync(updatedEntity, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(updatedEntity.Name, result.Name);
        Assert.Equal(updatedEntity.Type, result.Type);
        Assert.Equal(updatedEntity.Confidence, result.Confidence);
    }

    #endregion

    #region Relationship CRUD Tests

    [Theory]
    [MemberData(nameof(RelationshipTestCases))]
    public async Task AddRelationshipAsync_WithValidData_ShouldSucceed(
        Relationship relationship,
        SessionContext sessionContext)
    {
        // Arrange
        // Act
        var result = await _repository.AddRelationshipAsync(relationship, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Id > 0, "Relationship should have generated ID");
        Assert.Equal(relationship.Source, result.Source);
        Assert.Equal(relationship.RelationshipType, result.RelationshipType);
        Assert.Equal(relationship.Target, result.Target);
    }

    [Theory]
    [MemberData(nameof(RelationshipTestCases))]
    public async Task GetRelationshipByIdAsync_WithExistingRelationship_ShouldReturnRelationship(
        Relationship relationship,
        SessionContext sessionContext)
    {
        // Arrange
        var addedRelationship = await _repository.AddRelationshipAsync(relationship, sessionContext);

        // Act
        var result = await _repository.GetRelationshipByIdAsync(addedRelationship.Id, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(addedRelationship.Id, result.Id);
        Assert.Equal(addedRelationship.Source, result.Source);
        Assert.Equal(addedRelationship.Target, result.Target);
    }

    #endregion

    #region Session Isolation Tests

    [Theory]
    [MemberData(nameof(SessionIsolationTestCases))]
    public async Task SessionIsolation_WithDifferentSessions_ShouldIsolateData(
        SessionContext session1,
        SessionContext session2,
        Entity entity1,
        Entity entity2)
    {
        // Arrange
        // Act
        await _repository.AddEntityAsync(entity1, session1);
        await _repository.AddEntityAsync(entity2, session2);

        var entitiesSession1 = await _repository.GetEntitiesAsync(session1);
        var entitiesSession2 = await _repository.GetEntitiesAsync(session2);

        // Assert
        Assert.Single(entitiesSession1);
        Assert.Single(entitiesSession2);
        Assert.Equal(entity1.Name, entitiesSession1.First().Name);
        Assert.Equal(entity2.Name, entitiesSession2.First().Name);
    }

    #endregion

    #region Deadlock Detection Test

    [Fact]
    public async Task DeadlockDetection_SingleEntityAddition_ShouldCompleteWithinTimeout()
    {
        // Create a simple entity
        var entity = new Entity
        {
            Name = "TestEntity",
            Type = "test",
            UserId = "deadlock_test_user",
            Confidence = 0.8f
        };

        var sessionContext = new SessionContext { UserId = "deadlock_test_user" };

        // Use timeout wrapper to detect deadlocks quickly
        var result = await WithTimeoutAsync(
            _repository.AddEntityAsync(entity, sessionContext),
            8,
            "Deadlock Detection Entity Addition");

        Assert.NotNull(result);
        Assert.True(result.Id > 0);
    }

    #endregion

    #region Test Data

    public static IEnumerable<object[]> EntityTestCases => new List<object[]>
    {
        new object[]
        {
            new Entity { Name = "John Doe", Type = "person", UserId = "user123", Confidence = 0.8f },
            new SessionContext { UserId = "user123" }
        },
        new object[]
        {
            new Entity
            {
                Name = "New York",
                Type = "city",
                UserId = "user456",
                Aliases = new List<string> { "NYC", "Big Apple" },
                Confidence = 0.9f
            },
            new SessionContext { UserId = "user456", AgentId = "agent789" }
        },
        new object[]
        {
            new Entity
            {
                Name = "Machine Learning",
                Type = "concept",
                UserId = "user789",
                Metadata = new Dictionary<string, object> { { "domain", "AI" } },
                Confidence = 0.95f
            },
            new SessionContext { UserId = "user789", AgentId = "agent123", RunId = "run456" }
        }
    };

    public static IEnumerable<object[]> EntityUpdateTestCases => new List<object[]>
    {
        new object[]
        {
            new Entity { Name = "John", Type = "person", UserId = "user123", Confidence = 0.5f },
            new Entity { Name = "John", Type = "person", UserId = "user123", Confidence = 0.9f },
            new SessionContext { UserId = "user123" }
        },
        new object[]
        {
            new Entity { Name = "Entity", Type = "unknown", UserId = "user456", Confidence = 0.8f },
            new Entity { Name = "Entity", Type = "person", UserId = "user456", Confidence = 0.8f },
            new SessionContext { UserId = "user456" }
        }
    };

    public static IEnumerable<object[]> RelationshipTestCases => new List<object[]>
    {
        new object[]
        {
            new Relationship { Source = "John", RelationshipType = "likes", Target = "Pizza", UserId = "user123", Confidence = 0.8f },
            new SessionContext { UserId = "user123" }
        },
        new object[]
        {
            new Relationship
            {
                Source = "Alice",
                RelationshipType = "works_at",
                Target = "Google",
                UserId = "user456",
                TemporalContext = "since 2023",
                Confidence = 0.9f
            },
            new SessionContext { UserId = "user456", AgentId = "agent789" }
        }
    };

    public static IEnumerable<object[]> SessionIsolationTestCases => new List<object[]>
    {
        new object[]
        {
            new SessionContext { UserId = "user1" },
            new SessionContext { UserId = "user2" },
            new Entity { Name = "Entity1", UserId = "user1", Confidence = 0.8f },
            new Entity { Name = "Entity2", UserId = "user2", Confidence = 0.8f }
        },
        new object[]
        {
            new SessionContext { UserId = "user1", AgentId = "agent1" },
            new SessionContext { UserId = "user1", AgentId = "agent2" },
            new Entity { Name = "Entity1", UserId = "user1", AgentId = "agent1", Confidence = 0.8f },
            new Entity { Name = "Entity2", UserId = "user1", AgentId = "agent2", Confidence = 0.8f }
        }
    };

    #endregion

    #region Timeout Helper

    private async Task<T> WithTimeoutAsync<T>(Task<T> task, int timeoutSeconds, string operationName)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var timeoutTask = Task.Delay(Timeout.Infinite, cts.Token);

        var completedTask = await Task.WhenAny(task, timeoutTask);

        if (completedTask == timeoutTask)
        {
            throw new TimeoutException($"{operationName} timed out after {timeoutSeconds} seconds");
        }

        return await task;
    }

    private async Task WithTimeoutAsync(Task task, int timeoutSeconds, string operationName)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var timeoutTask = Task.Delay(Timeout.Infinite, cts.Token);

        var completedTask = await Task.WhenAny(task, timeoutTask);

        if (completedTask == timeoutTask)
        {
            throw new TimeoutException($"{operationName} timed out after {timeoutSeconds} seconds");
        }

        await task;
    }

    #endregion

    public void Dispose()
    {
        try
        {
            // Cleanup the TestSqliteSessionFactory to remove test database files
            _sessionFactory?.Cleanup();

            // Force garbage collection to help release any remaining resources
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        catch (Exception ex)
        {
            // Log disposal errors but don't throw
            System.Diagnostics.Debug.WriteLine($"Error during disposal: {ex.Message}");
        }
    }
}

[CollectionDefinition("GraphRepository")]
public class GraphRepositoryCollection
{
    // This class has no code, and is never created.
    // Its purpose is simply to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}