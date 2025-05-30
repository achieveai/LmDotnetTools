using MemoryServer.Models;
using MemoryServer.Services;
using MemoryServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemoryServer.Tests.Services;

/// <summary>
/// Comprehensive tests for GraphRepository including CRUD operations, graph traversal, and session isolation.
/// Uses data-driven testing approach with in-memory SQLite database for realistic testing.
/// </summary>
public class GraphRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteManager _sqliteManager;
    private readonly GraphRepository _repository;
    private readonly Mock<ILogger<GraphRepository>> _mockLogger;

    public GraphRepositoryTests()
    {
        // Setup in-memory SQLite database for testing with shared cache
        var connectionString = "Data Source=test_db_" + Guid.NewGuid().ToString("N")[..8] + ";Mode=Memory;Cache=Shared";
        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        _mockLogger = new Mock<ILogger<GraphRepository>>();
        
        // Create mock options for SqliteManager
        var mockOptions = new Mock<IOptions<DatabaseOptions>>();
        mockOptions.Setup(o => o.Value).Returns(new DatabaseOptions { ConnectionString = connectionString });
        
        var mockSqliteLogger = new Mock<ILogger<SqliteManager>>();
        _sqliteManager = new SqliteManager(mockOptions.Object, mockSqliteLogger.Object);
        
        // Initialize database schema
        _sqliteManager.InitializeDatabaseAsync().Wait();
        
        _repository = new GraphRepository(_sqliteManager, _mockLogger.Object);

        Debug.WriteLine("✅ Test database initialized");
    }

    #region Entity CRUD Tests

    [Theory]
    [MemberData(nameof(EntityTestCases))]
    public async Task AddEntityAsync_WithValidData_ShouldSucceed(
        string testName,
        Entity entity,
        SessionContext sessionContext)
    {
        // Arrange
        Debug.WriteLine($"Testing entity addition: {testName}");
        Debug.WriteLine($"Entity: {entity.Name}, Session: {sessionContext}");

        // Act
        var result = await _repository.AddEntityAsync(entity, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Id > 0, "Entity should have generated ID");
        Assert.Equal(entity.Name, result.Name);
        Assert.Equal(entity.Type, result.Type);
        Assert.Equal(sessionContext.UserId, result.UserId);

        Debug.WriteLine($"✅ Entity added with ID: {result.Id}");
    }

    [Theory]
    [MemberData(nameof(EntityTestCases))]
    public async Task GetEntityByIdAsync_WithExistingEntity_ShouldReturnEntity(
        string testName,
        Entity entity,
        SessionContext sessionContext)
    {
        // Arrange
        Debug.WriteLine($"Testing entity retrieval by ID: {testName}");
        var addedEntity = await _repository.AddEntityAsync(entity, sessionContext);

        // Act
        var result = await _repository.GetEntityByIdAsync(addedEntity.Id, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(addedEntity.Id, result.Id);
        Assert.Equal(addedEntity.Name, result.Name);
        Assert.Equal(addedEntity.Type, result.Type);

        Debug.WriteLine($"✅ Entity retrieved successfully: {result.Name}");
    }

    [Theory]
    [MemberData(nameof(EntityTestCases))]
    public async Task GetEntityByNameAsync_WithExistingEntity_ShouldReturnEntity(
        string testName,
        Entity entity,
        SessionContext sessionContext)
    {
        // Arrange
        Debug.WriteLine($"Testing entity retrieval by name: {testName}");
        await _repository.AddEntityAsync(entity, sessionContext);

        // Act
        var result = await _repository.GetEntityByNameAsync(entity.Name, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(entity.Name, result.Name);
        Assert.Equal(entity.Type, result.Type);

        Debug.WriteLine($"✅ Entity retrieved by name: {result.Name}");
    }

    [Theory]
    [MemberData(nameof(EntityUpdateTestCases))]
    public async Task UpdateEntityAsync_WithValidData_ShouldUpdateSuccessfully(
        string testName,
        Entity originalEntity,
        Entity updatedEntity,
        SessionContext sessionContext)
    {
        // Arrange
        Debug.WriteLine($"Testing entity update: {testName}");
        var addedEntity = await _repository.AddEntityAsync(originalEntity, sessionContext);
        updatedEntity.Id = addedEntity.Id;

        // Act
        var result = await _repository.UpdateEntityAsync(updatedEntity, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(updatedEntity.Name, result.Name);
        Assert.Equal(updatedEntity.Type, result.Type);
        Assert.Equal(updatedEntity.Confidence, result.Confidence);

        Debug.WriteLine($"✅ Entity updated: {result.Name}");
    }

    #endregion

    #region Relationship CRUD Tests

    [Theory]
    [MemberData(nameof(RelationshipTestCases))]
    public async Task AddRelationshipAsync_WithValidData_ShouldSucceed(
        string testName,
        Relationship relationship,
        SessionContext sessionContext)
    {
        // Arrange
        Debug.WriteLine($"Testing relationship addition: {testName}");
        Debug.WriteLine($"Relationship: {relationship.Source} --[{relationship.RelationshipType}]--> {relationship.Target}");

        // Act
        var result = await _repository.AddRelationshipAsync(relationship, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Id > 0, "Relationship should have generated ID");
        Assert.Equal(relationship.Source, result.Source);
        Assert.Equal(relationship.RelationshipType, result.RelationshipType);
        Assert.Equal(relationship.Target, result.Target);

        Debug.WriteLine($"✅ Relationship added with ID: {result.Id}");
    }

    [Theory]
    [MemberData(nameof(RelationshipTestCases))]
    public async Task GetRelationshipByIdAsync_WithExistingRelationship_ShouldReturnRelationship(
        string testName,
        Relationship relationship,
        SessionContext sessionContext)
    {
        // Arrange
        Debug.WriteLine($"Testing relationship retrieval by ID: {testName}");
        var addedRelationship = await _repository.AddRelationshipAsync(relationship, sessionContext);

        // Act
        var result = await _repository.GetRelationshipByIdAsync(addedRelationship.Id, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(addedRelationship.Id, result.Id);
        Assert.Equal(addedRelationship.Source, result.Source);
        Assert.Equal(addedRelationship.Target, result.Target);

        Debug.WriteLine($"✅ Relationship retrieved successfully");
    }

    #endregion

    #region Session Isolation Tests

    [Theory]
    [MemberData(nameof(SessionIsolationTestCases))]
    public async Task SessionIsolation_WithDifferentSessions_ShouldIsolateData(
        string testName,
        SessionContext session1,
        SessionContext session2,
        Entity entity1,
        Entity entity2)
    {
        // Arrange
        Debug.WriteLine($"Testing session isolation: {testName}");
        Debug.WriteLine($"Session1: {session1}, Session2: {session2}");

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

        Debug.WriteLine($"✅ Session isolation verified");
    }

    #endregion

    #region Test Data

    public static IEnumerable<object[]> EntityTestCases => new List<object[]>
    {
        new object[]
        {
            "Basic entity",
            new Entity { Name = "John Doe", Type = "person", UserId = "user123", Confidence = 0.8f },
            new SessionContext { UserId = "user123" }
        },
        new object[]
        {
            "Entity with aliases",
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
            "Entity with metadata",
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
            "Update entity confidence",
            new Entity { Name = "John", Type = "person", UserId = "user123", Confidence = 0.5f },
            new Entity { Name = "John", Type = "person", UserId = "user123", Confidence = 0.9f },
            new SessionContext { UserId = "user123" }
        },
        new object[]
        {
            "Update entity type",
            new Entity { Name = "Entity", Type = "unknown", UserId = "user456", Confidence = 0.8f },
            new Entity { Name = "Entity", Type = "person", UserId = "user456", Confidence = 0.8f },
            new SessionContext { UserId = "user456" }
        }
    };

    public static IEnumerable<object[]> RelationshipTestCases => new List<object[]>
    {
        new object[]
        {
            "Basic relationship",
            new Relationship { Source = "John", RelationshipType = "likes", Target = "Pizza", UserId = "user123", Confidence = 0.8f },
            new SessionContext { UserId = "user123" }
        },
        new object[]
        {
            "Relationship with temporal context",
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
            "Different users",
            new SessionContext { UserId = "user1" },
            new SessionContext { UserId = "user2" },
            new Entity { Name = "Entity1", UserId = "user1", Confidence = 0.8f },
            new Entity { Name = "Entity2", UserId = "user2", Confidence = 0.8f }
        },
        new object[]
        {
            "Same user, different agents",
            new SessionContext { UserId = "user1", AgentId = "agent1" },
            new SessionContext { UserId = "user1", AgentId = "agent2" },
            new Entity { Name = "Entity1", UserId = "user1", AgentId = "agent1", Confidence = 0.8f },
            new Entity { Name = "Entity2", UserId = "user1", AgentId = "agent2", Confidence = 0.8f }
        }
    };

    #endregion

    public void Dispose()
    {
        _connection?.Dispose();
        Debug.WriteLine("✅ Test database disposed");
    }
} 