namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>Shared deterministic correlation for accepted S2S inputs, preserving legacy identifiers.</summary>
public static class IdempotentInputId
{
    public static string Create(string key, bool suppressSpawning, bool suppressActionTools = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return suppressActionTools
            ? $"idem-tools:{(suppressSpawning ? '1' : '0')}:{key}"
            : $"idem:{(suppressSpawning ? '1' : '0')}:{key}";
    }
}
