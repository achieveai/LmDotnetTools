namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>Persisted property keys that relate one conversation to another.</summary>
public static class ConversationRelationKeys
{
    /// <summary>
    /// Thread id of the conversation that spawned a sub-agent. The historical
    /// <c>sample.subAgentOf</c> value is retained so existing persisted conversations remain valid.
    /// </summary>
    public const string SubAgentParentThreadId = "sample.subAgentOf";
}
