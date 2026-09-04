namespace AchieveAi.LmDotnetTools.LmMultiTurn.Audit;

/// <summary>Thrown when a configured audit sink cannot accept a required source record.</summary>
public sealed class AuditCaptureException : Exception
{
    public AuditCaptureException(string recordType, Exception innerException)
        : base($"Audit capture failed for record type '{recordType}'.", innerException)
    {
        RecordType = recordType;
    }

    public string RecordType { get; }
}
