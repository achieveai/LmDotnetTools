using MemoryServer.Models;
using MemoryServer.Services;

namespace MemoryServer.Tests.Mocks;

/// <summary>
/// Mock implementation of IMemoryRepository for unit testing.
/// Provides in-memory storage and configurable behavior for testing scenarios.
/// </summary>
public class MockMemoryRepository : IMemoryRepository
{
    private readonly Dictionary<int, Memory> _memories = new();
    private int _nextId = 1;

    // Configuration for testing scenarios
    public bool ShouldThrowOnAdd { get; set; } = false;
    public bool ShouldThrowOnUpdate { get; set; } = false;
    public bool ShouldThrowOnDelete { get; set; } = false;
    public bool ShouldThrowOnSearch { get; set; } = false;
    public Exception? ExceptionToThrow { get; set; }

    // Tracking for verification
    public List<string> MethodCalls { get; } = new();
    public Dictionary<string, object?> LastCallParameters { get; } = new();

    public Task<Memory> AddAsync(string content, SessionContext sessionContext, Dictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
    {
        MethodCalls.Add(nameof(AddAsync));
        LastCallParameters["content"] = content;
        LastCallParameters["sessionContext"] = sessionContext;
        LastCallParameters["metadata"] = metadata;

        if (ShouldThrowOnAdd)
        {
            throw ExceptionToThrow ?? new InvalidOperationException("Mock exception on Add");
        }

        var memory = new Memory
        {
            Id = _nextId++,
            Content = content,
            UserId = sessionContext.UserId,
            AgentId = sessionContext.AgentId,
            RunId = sessionContext.RunId,
            Metadata = metadata,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Version = 1
        };

        _memories[memory.Id] = memory;
        return Task.FromResult(memory);
    }

    public Task<Memory?> GetByIdAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        MethodCalls.Add(nameof(GetByIdAsync));
        LastCallParameters["id"] = id;
        LastCallParameters["sessionContext"] = sessionContext;

        if (!_memories.TryGetValue(id, out var memory))
        {
            return Task.FromResult<Memory?>(null);
        }

        // Check session context access
        if (!memory.GetSessionContext().Matches(sessionContext))
        {
            return Task.FromResult<Memory?>(null);
        }

        return Task.FromResult<Memory?>(memory);
    }

    public Task<Memory?> UpdateAsync(int id, string content, SessionContext sessionContext, Dictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
    {
        MethodCalls.Add(nameof(UpdateAsync));
        LastCallParameters["id"] = id;
        LastCallParameters["content"] = content;
        LastCallParameters["sessionContext"] = sessionContext;
        LastCallParameters["metadata"] = metadata;

        if (ShouldThrowOnUpdate)
        {
            throw ExceptionToThrow ?? new InvalidOperationException("Mock exception on Update");
        }

        if (!_memories.TryGetValue(id, out var memory))
        {
            return Task.FromResult<Memory?>(null);
        }

        // Check session context access
        if (!memory.GetSessionContext().Matches(sessionContext))
        {
            return Task.FromResult<Memory?>(null);
        }

        var updatedMemory = memory.WithUpdatedTimestamp();
        updatedMemory.Content = content;
        updatedMemory.Metadata = metadata;

        _memories[id] = updatedMemory;
        return Task.FromResult<Memory?>(updatedMemory);
    }

    public Task<bool> DeleteAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        MethodCalls.Add(nameof(DeleteAsync));
        LastCallParameters["id"] = id;
        LastCallParameters["sessionContext"] = sessionContext;

        if (ShouldThrowOnDelete)
        {
            throw ExceptionToThrow ?? new InvalidOperationException("Mock exception on Delete");
        }

        if (!_memories.TryGetValue(id, out var memory))
        {
            return Task.FromResult(false);
        }

        // Check session context access
        if (!memory.GetSessionContext().Matches(sessionContext))
        {
            return Task.FromResult(false);
        }

        _memories.Remove(id);
        return Task.FromResult(true);
    }

    public Task<List<Memory>> GetAllAsync(SessionContext sessionContext, int limit = 100, int offset = 0, CancellationToken cancellationToken = default)
    {
        MethodCalls.Add(nameof(GetAllAsync));
        LastCallParameters["sessionContext"] = sessionContext;
        LastCallParameters["limit"] = limit;
        LastCallParameters["offset"] = offset;

        var memories = _memories.Values
            .Where(m => m.GetSessionContext().Matches(sessionContext))
            .OrderBy(m => m.Id)
            .Skip(offset)
            .Take(limit)
            .ToList();

        return Task.FromResult(memories);
    }

    public Task<List<Memory>> SearchAsync(string query, SessionContext sessionContext, int limit = 10, float scoreThreshold = 0.0f, CancellationToken cancellationToken = default)
    {
        MethodCalls.Add(nameof(SearchAsync));
        LastCallParameters["query"] = query;
        LastCallParameters["sessionContext"] = sessionContext;
        LastCallParameters["limit"] = limit;
        LastCallParameters["scoreThreshold"] = scoreThreshold;

        if (ShouldThrowOnSearch)
        {
            throw ExceptionToThrow ?? new InvalidOperationException("Mock exception on Search");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(new List<Memory>());
        }

        var memories = _memories.Values
            .Where(m => m.GetSessionContext().Matches(sessionContext))
            .Where(m => m.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.WithScore(0.8f)) // Mock score
            .Take(limit)
            .ToList();

        return Task.FromResult(memories);
    }

    public Task<MemoryStats> GetStatsAsync(SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        MethodCalls.Add(nameof(GetStatsAsync));
        LastCallParameters["sessionContext"] = sessionContext;

        var sessionMemories = _memories.Values
            .Where(m => m.GetSessionContext().Matches(sessionContext))
            .ToList();

        var stats = new MemoryStats
        {
            TotalMemories = sessionMemories.Count,
            TotalContentSize = sessionMemories.Sum(m => m.Content.Length),
            AverageContentLength = sessionMemories.Count > 0 ? sessionMemories.Average(m => m.Content.Length) : 0,
            OldestMemory = sessionMemories.Count > 0 ? sessionMemories.Min(m => m.CreatedAt) : null,
            NewestMemory = sessionMemories.Count > 0 ? sessionMemories.Max(m => m.CreatedAt) : null,
            MemoryCountByScope = new Dictionary<string, int>
            {
                ["User"] = sessionMemories.Count(m => m.GetSessionContext().GetScope() == SessionScope.User),
                ["Agent"] = sessionMemories.Count(m => m.GetSessionContext().GetScope() == SessionScope.Agent),
                ["Run"] = sessionMemories.Count(m => m.GetSessionContext().GetScope() == SessionScope.Run)
            }
        };

        return Task.FromResult(stats);
    }

    public Task<int> DeleteAllAsync(SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        MethodCalls.Add(nameof(DeleteAllAsync));
        LastCallParameters["sessionContext"] = sessionContext;

        var toDelete = _memories.Values
            .Where(m => m.GetSessionContext().Matches(sessionContext))
            .ToList();

        foreach (var memory in toDelete)
        {
            _memories.Remove(memory.Id);
        }

        return Task.FromResult(toDelete.Count);
    }

    public Task<List<MemoryHistoryEntry>> GetHistoryAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        MethodCalls.Add(nameof(GetHistoryAsync));
        LastCallParameters["id"] = id;
        LastCallParameters["sessionContext"] = sessionContext;

        // Mock implementation - return empty history for simplicity
        // In a real implementation, this would return version history
        return Task.FromResult(new List<MemoryHistoryEntry>());
    }

    // Helper methods for testing
    public void Reset()
    {
        _memories.Clear();
        _nextId = 1;
        MethodCalls.Clear();
        LastCallParameters.Clear();
        ShouldThrowOnAdd = false;
        ShouldThrowOnUpdate = false;
        ShouldThrowOnDelete = false;
        ShouldThrowOnSearch = false;
        ExceptionToThrow = null;
    }

    public void AddTestMemory(Memory memory)
    {
        _memories[memory.Id] = memory;
        if (memory.Id >= _nextId)
        {
            _nextId = memory.Id + 1;
        }
    }

    public int GetMemoryCount() => _memories.Count;

    public bool HasMemory(int id) => _memories.ContainsKey(id);

    public Memory? GetMemoryById(int id) => _memories.TryGetValue(id, out var memory) ? memory : null;

    // Vector storage and search methods (mock implementations)

    public Task StoreEmbeddingAsync(int memoryId, float[] embedding, string modelName, CancellationToken cancellationToken = default)
    {
        MethodCalls.Add(nameof(StoreEmbeddingAsync));
        LastCallParameters["memoryId"] = memoryId;
        LastCallParameters["embedding"] = embedding;
        LastCallParameters["modelName"] = modelName;

        // Mock implementation - just track the call
        return Task.CompletedTask;
    }

    public Task<float[]?> GetEmbeddingAsync(int memoryId, CancellationToken cancellationToken = default)
    {
        MethodCalls.Add(nameof(GetEmbeddingAsync));
        LastCallParameters["memoryId"] = memoryId;

        // Mock implementation - return null (no embedding stored)
        return Task.FromResult<float[]?>(null);
    }

    public Task<List<VectorSearchResult>> SearchVectorAsync(
        float[] queryEmbedding,
        SessionContext sessionContext,
        int limit = 10,
        float threshold = 0.7f,
        CancellationToken cancellationToken = default)
    {
        MethodCalls.Add(nameof(SearchVectorAsync));
        LastCallParameters["queryEmbedding"] = queryEmbedding;
        LastCallParameters["sessionContext"] = sessionContext;
        LastCallParameters["limit"] = limit;
        LastCallParameters["threshold"] = threshold;

        // Mock implementation - return empty results
        return Task.FromResult(new List<VectorSearchResult>());
    }

    public Task<List<Memory>> SearchHybridAsync(
        string query,
        float[] queryEmbedding,
        SessionContext sessionContext,
        int limit = 10,
        float traditionalWeight = 0.3f,
        float vectorWeight = 0.7f,
        CancellationToken cancellationToken = default)
    {
        MethodCalls.Add(nameof(SearchHybridAsync));
        LastCallParameters["query"] = query;
        LastCallParameters["queryEmbedding"] = queryEmbedding;
        LastCallParameters["sessionContext"] = sessionContext;
        LastCallParameters["limit"] = limit;
        LastCallParameters["traditionalWeight"] = traditionalWeight;
        LastCallParameters["vectorWeight"] = vectorWeight;

        // Mock implementation - fall back to traditional search
        return SearchAsync(query, sessionContext, limit, 0.0f, cancellationToken);
    }

    public Task<List<string>> GetAgentsAsync(string userId, CancellationToken cancellationToken = default)
    {
        MethodCalls.Add(nameof(GetAgentsAsync));
        LastCallParameters["userId"] = userId;

        var agents = _memories.Values
            .Where(m => m.UserId == userId && !string.IsNullOrEmpty(m.AgentId))
            .Select(m => m.AgentId!)
            .Distinct()
            .ToList();

        return Task.FromResult(agents);
    }

    public Task<List<string>> GetRunsAsync(string userId, string agentId, CancellationToken cancellationToken = default)
    {
        MethodCalls.Add(nameof(GetRunsAsync));
        LastCallParameters["userId"] = userId;
        LastCallParameters["agentId"] = agentId;

        var runs = _memories.Values
            .Where(m => m.UserId == userId && m.AgentId == agentId && !string.IsNullOrEmpty(m.RunId))
            .Select(m => m.RunId!)
            .Distinct()
            .ToList();

        return Task.FromResult(runs);
    }
} 