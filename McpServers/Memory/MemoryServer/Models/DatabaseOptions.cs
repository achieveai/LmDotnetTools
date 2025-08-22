namespace MemoryServer.Models;

/// <summary>
/// Configuration options for the database.
/// </summary>
public class DatabaseOptions
{
    public string ConnectionString { get; set; } = "Data Source=memory.db;Cache=Shared";
    public bool EnableWAL { get; set; } = true;
    public int BusyTimeout { get; set; } = 30000;
    public int CommandTimeout { get; set; } = 30;
    public int MaxConnections { get; set; } = 10;
}