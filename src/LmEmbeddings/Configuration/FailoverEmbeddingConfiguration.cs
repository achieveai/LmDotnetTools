namespace AchieveAi.LmDotnetTools.LmEmbeddings.Configuration;

/// <summary>
/// Configuration POCO for binding failover embedding settings from IConfiguration.
/// </summary>
public class FailoverEmbeddingConfiguration
{
    public EndpointConfig Primary { get; set; } = new();
    public EndpointConfig Backup { get; set; } = new();
    public double PrimaryRequestTimeoutSeconds { get; set; } = 5;
    public bool FailoverOnHttpError { get; set; } = true;
    public double? RecoveryIntervalSeconds { get; set; } = 120;

    public class EndpointConfig
    {
        public string Endpoint { get; set; } = "";
        public string Model { get; set; } = "";
        public int EmbeddingSize { get; set; } = 1536;
        public string ApiKey { get; set; } = "";
    }
}
