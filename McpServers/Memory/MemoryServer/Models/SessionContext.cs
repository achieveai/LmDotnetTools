namespace MemoryServer.Models;

/// <summary>
/// Defines the session scope for memory operations and access control.
/// Supports hierarchical session isolation: User > Agent > Run.
/// </summary>
public class SessionContext
{
    /// <summary>
    /// User identifier (required for all operations).
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Optional agent identifier for finer session control.
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// Optional run identifier for conversation-level isolation.
    /// </summary>
    public string? RunId { get; set; }

    /// <summary>
    /// Additional session metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Checks if this session context matches another for access control.
    /// </summary>
    public bool Matches(SessionContext other)
    {
        if (other == null)
        {
            return false;
        }

        // User ID must always match
        if (UserId != other.UserId)
        {
            return false;
        }

        // If either has AgentId, they must match
        if (!string.IsNullOrEmpty(AgentId) || !string.IsNullOrEmpty(other.AgentId))
        {
            if (AgentId != other.AgentId)
            {
                return false;
            }
        }

        // If either has RunId, they must match
        if (!string.IsNullOrEmpty(RunId) || !string.IsNullOrEmpty(other.RunId))
        {
            if (RunId != other.RunId)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Creates a session context with only user ID.
    /// </summary>
    public static SessionContext ForUser(string userId)
    {
        return new SessionContext { UserId = userId };
    }

    /// <summary>
    /// Creates a session context with user and agent ID.
    /// </summary>
    public static SessionContext ForAgent(string userId, string agentId)
    {
        return new SessionContext { UserId = userId, AgentId = agentId };
    }

    /// <summary>
    /// Creates a session context with user, agent, and run ID.
    /// </summary>
    public static SessionContext ForRun(string userId, string agentId, string runId)
    {
        return new SessionContext
        {
            UserId = userId,
            AgentId = agentId,
            RunId = runId,
        };
    }

    /// <summary>
    /// Gets a string representation for logging and debugging.
    /// </summary>
    public override string ToString()
    {
        var parts = new List<string> { UserId };

        // Only add agent part if there's actually an agent ID
        if (!string.IsNullOrEmpty(AgentId))
        {
            parts.Add(AgentId);

            // Only add run part if it exists
            if (!string.IsNullOrEmpty(RunId))
            {
                parts.Add(RunId);
            }
        }
        else if (!string.IsNullOrEmpty(RunId))
        {
            // Special case: user and run but no agent
            parts.Add(""); // Empty agent part
            parts.Add(RunId);
        }

        return string.Join("/", parts);
    }

    /// <summary>
    /// Gets the session scope level.
    /// </summary>
    public SessionScope GetScope()
    {
        return !string.IsNullOrEmpty(RunId) ? SessionScope.Run : !string.IsNullOrEmpty(AgentId) ? SessionScope.Agent : SessionScope.User;
    }
}

/// <summary>
/// Defines the scope level of a session.
/// </summary>
public enum SessionScope
{
    /// <summary>
    /// User-level scope (broadest).
    /// </summary>
    User,

    /// <summary>
    /// Agent-level scope (medium).
    /// </summary>
    Agent,

    /// <summary>
    /// Run-level scope (narrowest).
    /// </summary>
    Run,
}
