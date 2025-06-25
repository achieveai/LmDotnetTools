using System.Text.Json;
using System.Text.Json.Schema;

// Test how nullable types are represented in JSON schema
public class TestEntity
{
    public string Name { get; set; } = string.Empty;  // Required
    public string? Type { get; set; }                // Nullable/Optional
    public float Confidence { get; set; } = 1.0f;    // Required
}

var schema = JsonSerializerOptions.Default.GetJsonSchemaAsNode(typeof(TestEntity));
Console.WriteLine("Raw .NET Schema:");
Console.WriteLine(schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); 