using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.LmCore.Configuration;

/// <summary>
/// Provides validation for function names and prefixes according to OpenAI naming requirements.
/// </summary>
public static partial class FunctionNameValidator
{
    /// <summary>
    /// Regular expression pattern for valid OpenAI function names.
    /// Names must contain only letters (a-z, A-Z), numbers (0-9), underscores (_), or hyphens (-).
    /// Maximum length is 64 characters.
    /// </summary>
    private static readonly Regex ValidNamePattern = ValidNameRegex();

    /// <summary>
    /// Regular expression pattern for valid function name prefixes.
    /// Prefixes follow the same rules as function names but should be shorter to leave room for the actual function name.
    /// </summary>
    private static readonly Regex ValidPrefixPattern = ValidPrefixRegex();

    /// <summary>
    /// Maximum allowed length for a complete function name (including prefix and separator).
    /// </summary>
    public const int MaxFunctionNameLength = 64;

    /// <summary>
    /// Maximum recommended length for a prefix to ensure there's room for the function name.
    /// </summary>
    public const int MaxPrefixLength = 32;

    /// <summary>
    /// Validates whether a function name complies with OpenAI naming requirements.
    /// </summary>
    /// <param name="functionName">The function name to validate</param>
    /// <returns>True if the name is valid, false otherwise</returns>
    public static bool IsValidFunctionName(string? functionName)
    {
        return !string.IsNullOrWhiteSpace(functionName) && ValidNamePattern.IsMatch(functionName);
    }

    /// <summary>
    /// Validates whether a prefix complies with OpenAI naming requirements for use in function names.
    /// </summary>
    /// <param name="prefix">The prefix to validate</param>
    /// <returns>True if the prefix is valid, false otherwise</returns>
    public static bool IsValidPrefix(string? prefix)
    {
        return !string.IsNullOrWhiteSpace(prefix) && ValidPrefixPattern.IsMatch(prefix);
    }

    /// <summary>
    /// Gets a descriptive error message for an invalid function name.
    /// </summary>
    /// <param name="functionName">The invalid function name</param>
    /// <returns>A descriptive error message</returns>
    public static string GetFunctionNameValidationError(string? functionName)
    {
        return string.IsNullOrWhiteSpace(functionName) ? "Function name cannot be null or empty."
            : functionName.Length > MaxFunctionNameLength
                ? $"Function name '{functionName}' exceeds maximum length of {MaxFunctionNameLength} characters."
            : $"Function name '{functionName}' contains invalid characters. "
                + "Only letters (a-z, A-Z), numbers (0-9), underscores (_), and hyphens (-) are allowed.";
    }

    /// <summary>
    /// Gets a descriptive error message for an invalid prefix.
    /// </summary>
    /// <param name="prefix">The invalid prefix</param>
    /// <returns>A descriptive error message</returns>
    public static string GetPrefixValidationError(string? prefix)
    {
        return string.IsNullOrWhiteSpace(prefix) ? "Prefix cannot be null or empty."
            : prefix.Length > MaxPrefixLength
                ? $"Prefix '{prefix}' exceeds recommended maximum length of {MaxPrefixLength} characters. "
                    + $"This may not leave enough room for function names (total limit is {MaxFunctionNameLength} characters)."
            : $"Prefix '{prefix}' contains invalid characters. "
                + "Only letters (a-z, A-Z), numbers (0-9), underscores (_), and hyphens (-) are allowed.";
    }

    /// <summary>
    /// Validates whether a prefixed function name (prefix + separator + name) is valid.
    /// </summary>
    /// <param name="prefix">The prefix part</param>
    /// <param name="functionName">The function name part</param>
    /// <param name="separator">The separator between prefix and name (default is "__")</param>
    /// <returns>True if the complete name is valid, false otherwise</returns>
    public static bool IsValidPrefixedFunctionName(string prefix, string functionName, string separator = "__")
    {
        var completeName = $"{prefix}{separator}{functionName}";
        return IsValidFunctionName(completeName);
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{1,64}$", RegexOptions.Compiled)]
    private static partial Regex ValidNameRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{1,32}$", RegexOptions.Compiled)]
    private static partial Regex ValidPrefixRegex();
}
