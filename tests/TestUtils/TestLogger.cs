using System.Text.Json;

namespace AchieveAi.LmDotnetTools.TestUtils;

/// <summary>
///     Helper for test debugging and diagnostics
/// </summary>
public static class TestLogger
{
    public static void Log(string message)
    {
        Console.WriteLine($"[TEST_LOG] {message}");
    }

    public static void LogObject(string name, object? obj)
    {
        if (obj == null)
        {
            Log($"{name}: null");
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
            Log($"{name}: {json}");
        }
        catch (Exception ex)
        {
            Log($"{name}: Error serializing: {ex.Message}");
        }
    }
}
