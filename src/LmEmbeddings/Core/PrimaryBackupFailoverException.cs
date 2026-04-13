namespace AchieveAi.LmDotnetTools.LmEmbeddings.Core;

public class PrimaryBackupFailoverException : Exception
{
    public Exception PrimaryException { get; }
    public Exception BackupException { get; }

    public PrimaryBackupFailoverException(string message, Exception primaryException, Exception backupException)
        : base(message, new AggregateException("Primary and backup failures.", primaryException, backupException))
    {
        PrimaryException = primaryException;
        BackupException = backupException;
    }
}
