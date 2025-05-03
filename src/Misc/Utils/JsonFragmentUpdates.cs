using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.Misc.Utils;

/// <summary>
/// Represents the kind of fragment update emitted during JSON parsing
/// </summary>
public enum JsonFragmentKind
{
    /// <summary>
    /// Start of an object ('{')
    /// </summary>
    StartObject,

    /// <summary>
    /// End of an object ('}')
    /// </summary>
    EndObject,

    /// <summary>
    /// Start of an array ('[')
    /// </summary>
    StartArray,

    /// <summary>
    /// End of an array (']')
    /// </summary>
    EndArray,

    /// <summary>
    /// Start of a string value (opening quote)
    /// </summary>
    StartString,

    /// <summary>
    /// Partial content of a string value (not complete yet)
    /// </summary>
    PartialString,

    /// <summary>
    /// Complete string value (including closing quote)
    /// </summary>
    CompleteString,

    /// <summary>
    /// Complete object property key (including quotes)
    /// </summary>
    Key,

    /// <summary>
    /// Complete numeric value
    /// </summary>
    CompleteNumber,

    /// <summary>
    /// Complete boolean value (true/false)
    /// </summary>
    CompleteBoolean,

    /// <summary>
    /// Complete null value
    /// </summary>
    CompleteNull
}

/// <summary>
/// Represents a single update from the JSON fragment parser
/// </summary>
public sealed record JsonFragmentUpdate
{
    /// <summary>
    /// The JSON path to this fragment (e.g. "root.users[1].address.street")
    /// </summary>
    public string Path { get; init; }

    /// <summary>
    /// The kind of fragment update
    /// </summary>
    public JsonFragmentKind Kind { get; init; }

    /// <summary>
    /// The text value of the fragment (if applicable)
    /// </summary>
    public string? TextValue { get; init; }

    /// <summary>
    /// The JsonNode value (if applicable for complete values)
    /// </summary>
    public JsonNode? JsonValue { get; init; }

    /// <summary>
    /// Creates a new JsonFragmentUpdate
    /// </summary>
    public JsonFragmentUpdate(string path, JsonFragmentKind kind, string? textValue = null, JsonNode? jsonValue = null)
    {
        Path = path;
        Kind = kind;
        TextValue = textValue;
        JsonValue = jsonValue;
    }
}
