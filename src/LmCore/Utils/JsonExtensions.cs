using System.Text.Json;
using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

public static class JsonExtensions
{
    public static string ToJsonString(this JsonNode? node)
    {
        if (node == null)
        {
            return string.Empty;
        }
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
} 