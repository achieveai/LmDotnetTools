using System.Text;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Accumulates and manages JSON fragments from streaming responses using a robust stack-based visitor pattern.
/// Emits incremental updates as JSON is parsed, allowing for partial JSON processing.
/// </summary>
public class JsonFragmentToStructuredUpdateGenerator
{
    private readonly StringBuilder _buffer = new();
    private readonly string _toolName;
    private readonly Stack<Frame> _contextStack = new();
    private readonly List<char> _charBuffer = new();
    private ValueBuffer _currentValue = new();
    private bool _expectingPropertyName = false;
    private bool _afterColon = false;
    private string _lastPropertyName = string.Empty;
    private System.Text.StringBuilder? _currentString = null;
    private TokenType _currentTokenType = TokenType.None;
    private StringBuilder? _pendingStringUpdate = null; // Buffer for pending string updates
    private bool _wasComplete = false; // Track previous completion state to detect completion transitions

    private enum TokenType
    {
        None,
        Number,
        Literal
    }

    private sealed record Frame
    {
        public ContainerType Type { get; init; }
        public string? PropertyName { get; init; }
        public int ElementIndex { get; init; }
        public bool IsInString { get; init; }
        public bool IsEscaped { get; init; }
        public bool IsStartOrEnd { get; init; }
        public Frame? Parent { get; init; }

        public Frame(
            ContainerType type,
            Frame? parent = null,
            string? propertyName = null,
            int elementIndex = 0,
            bool isInString = false,
            bool isEscaped = false,
            bool isStartOrEnd = false)
        {
            Type = type;
            Parent = parent;
            PropertyName = propertyName;
            ElementIndex = elementIndex;
            IsInString = isInString;
            IsEscaped = isEscaped;
            IsStartOrEnd = isStartOrEnd;
        }
    }

    /// <summary>
    /// Creates a new JsonFragmentAccumulator for the specified tool
    /// </summary>
    /// <param name="toolName">The name of the tool generating the fragments</param>
    public JsonFragmentToStructuredUpdateGenerator(string toolName)
    {
        _toolName = toolName;
        // Initialize with a root frame
        _contextStack.Push(new Frame(ContainerType.Root));
    }

    /// <summary>
    /// Gets the name of the tool associated with this accumulator
    /// </summary>
    public string ToolName => _toolName;

    /// <summary>
    /// Gets the current accumulated JSON buffer as a string
    /// </summary>
    public string CurrentJson => _buffer.ToString();

    /// <summary>
    /// Gets whether the JSON is complete (balanced braces/brackets and no in-flight tokens)
    /// </summary>
    public bool IsComplete =>
        _contextStack.Count == 1 &&
        _currentValue.Kind == ValueKind.None &&
        !_expectingPropertyName &&
        !_afterColon;

    /// <summary>
    /// Adds a fragment to the accumulated JSON buffer and processes it character by character
    /// </summary>
    /// <param name="fragment">The JSON fragment to add</param>
    /// <returns>A sequence of fragment updates from parsing the new content</returns>
    public IEnumerable<JsonFragmentUpdate> AddFragment(string fragment)
    {
        if (string.IsNullOrEmpty(fragment))
        {
            yield break;
        }

        // Append to the full buffer for reference
        _buffer.Append(fragment);

        var pendingStringUpdate = new StringBuilder();
        var lastStringPath = string.Empty;

        // Process each character
        foreach (char c in fragment)
        {
            _charBuffer.Add(c);

            // Collect all updates from processing this character
            var charUpdates = ProcessChar(c).ToList();

            foreach (var update in charUpdates)
            {
                // For PartialString updates from value strings (not property names), group them by fragment
                if (update.Kind == JsonFragmentKind.PartialString)
                {
                    // If path changed, emit the pending update and start new buffer
                    if (lastStringPath != string.Empty && lastStringPath != update.Path)
                    {
                        if (pendingStringUpdate.Length > 0)
                        {
                            yield return new JsonFragmentUpdate(
                                lastStringPath,
                                JsonFragmentKind.PartialString,
                                pendingStringUpdate.ToString(),
                                pendingStringUpdate.ToString()
                            );
                            pendingStringUpdate.Clear();
                        }
                    }

                    lastStringPath = update.Path;
                    pendingStringUpdate.Append(update.TextValue);
                }
                else
                {
                    // For non-PartialString updates, first emit any pending string update
                    if (pendingStringUpdate.Length > 0)
                    {
                        yield return new JsonFragmentUpdate(
                            lastStringPath,
                            JsonFragmentKind.PartialString,
                            pendingStringUpdate.ToString(),
                            pendingStringUpdate.ToString()
                        );
                        pendingStringUpdate.Clear();
                        lastStringPath = string.Empty;
                    }

                    // Then emit the current update
                    yield return update;
                }
            }

            // Check for completion state change after processing each character
            bool isCurrentlyComplete = IsComplete;
            if (!_wasComplete && isCurrentlyComplete)
            {
                // Document just became complete - emit completion event
                yield return new JsonFragmentUpdate(
                    "root",
                    JsonFragmentKind.JsonComplete,
                    CurrentJson,
                    null
                );
            }
            _wasComplete = isCurrentlyComplete;
        }

        // Emit any remaining pending string update
        if (pendingStringUpdate.Length > 0)
        {
            yield return new JsonFragmentUpdate(
                lastStringPath,
                JsonFragmentKind.PartialString,
                pendingStringUpdate.ToString(),
                pendingStringUpdate.ToString()
            );
        }
    }

    /// <summary>
    /// Resets the parser state to start fresh
    /// </summary>
    public void Reset()
    {
        _buffer.Clear();
        _charBuffer.Clear();
        _contextStack.Clear();
        _contextStack.Push(new Frame(ContainerType.Root));
        _currentValue = new ValueBuffer();
        _expectingPropertyName = false;
        _afterColon = false;
        _lastPropertyName = string.Empty;
        _wasComplete = false;
    }

    /// <summary>
    /// Clears the accumulated buffer, but maintains the parser state
    /// </summary>
    public void Clear()
    {
        _buffer.Clear();
    }

    /// <summary>
    /// For backward compatibility: Attempts to extract a property value from the accumulated JSON
    /// </summary>
    /// <typeparam name="T">The expected type of the property</typeparam>
    /// <param name="propertyName">The name of the property to extract</param>
    /// <param name="value">The extracted value if successful</param>
    /// <returns>True if extraction was successful, false otherwise</returns>
    public bool TryGetValue<T>(string propertyName, out T? value)
    {
        return JsonStringUtils.TryExtractPropertyFromPartialJson(CurrentJson, propertyName, out value);
    }

    /// <summary>
    /// For backward compatibility: Attempts to parse the current buffer as a complete JSON object
    /// </summary>
    /// <typeparam name="T">The expected type of the JSON object</typeparam>
    /// <param name="result">The parsed object if successful</param>
    /// <returns>True if parsing was successful, false otherwise</returns>
    public bool TryParseCompleteJson<T>(out T? result)
    {
        result = default;

        try
        {
            if (IsComplete)
            {
                result = JsonSerializer.Deserialize<T>(CurrentJson);
                return result != null;
            }
        }
        catch
        {
            // Parsing failed
        }

        return false;
    }

    #region Internal Parsing Implementation

    private IEnumerable<JsonFragmentUpdate> ProcessChar(char c)
    {
        // Handle string content first
        if (_contextStack.Count > 0 && _contextStack.Peek().IsInString)
        {
            foreach (var update in HandleStringChar(c))
            {
                yield return update;
            }
            yield break;
        }

        // Handle token completion
        if (_currentString != null && _currentTokenType != TokenType.None)
        {
            foreach (var update in CheckAndEmitPendingToken(c))
            {
                yield return update;
            }
        }

        // Handle structural characters
        var updates = HandleStructuralChar(c);
        if (updates != null)
        {
            foreach (var update in updates)
            {
                yield return update;
            }
            yield break;
        }

        // Handle primitive characters
        if (IsPrimitiveChar(c))
        {
            foreach (var update in HandlePrimitiveChar(c))
            {
                yield return update;
            }
            yield break;
        }

        // Skip whitespace and other non-significant characters
        if (char.IsWhiteSpace(c))
        {
            yield break;
        }
    }

    private IEnumerable<JsonFragmentUpdate>? HandleStructuralChar(char c)
    {
        return c switch
        {
            '{' => HandleStartObject(),
            '}' => HandleEndObject(),
            '[' => HandleStartArray(),
            ']' => HandleEndArray(),
            ',' => HandleComma(),
            ':' => HandleColon(),
            '"' => HandleStartString(),
            _ => null
        };
    }

    private bool IsPrimitiveChar(char c)
    {
        return char.IsDigit(c) || c == '-' || char.IsLetter(c) || c == '.';
    }

    private IEnumerable<JsonFragmentUpdate> CheckAndEmitPendingToken(char c)
    {
        // If the character can't be part of the current token, emit it
        if (_currentTokenType == TokenType.Number && !IsValidNumberChar(c))
        {
            foreach (var update in EmitCurrentToken())
            {
                yield return update;
            }
        }
        else if (_currentTokenType == TokenType.Literal && !char.IsLetter(c))
        {
            foreach (var update in EmitCurrentToken())
            {
                yield return update;
            }
        }
    }

    private bool IsValidNumberChar(char c)
    {
        return char.IsDigit(c) || c == '.' || c == '-' || c == 'e' || c == 'E' || c == '+';
    }

    private IEnumerable<JsonFragmentUpdate> HandleStringChar(char c)
    {
        var currentFrame = _contextStack.Peek();

        // Initialize pending update buffer if needed
        if (_pendingStringUpdate == null)
        {
            _pendingStringUpdate = new StringBuilder();
        }

        if (currentFrame.IsEscaped)
        {
            _currentString!.Append(c);
            _pendingStringUpdate.Append(c);
            // Update frame with new state
            _contextStack.Pop();
            _contextStack.Push(currentFrame with { IsEscaped = false });
            yield break;
        }

        if (c == '\\')
        {
            _currentString!.Append(c);
            _pendingStringUpdate.Append(c);
            // Update frame with new state
            _contextStack.Pop();
            _contextStack.Push(currentFrame with { IsEscaped = true });
            yield break;
        }

        if (c == '"')
        {
            // End of string
            _currentString!.Append(c);
            _pendingStringUpdate?.Append(c);

            // Update frame state to not be in string
            _contextStack.Pop();
            _contextStack.Push(currentFrame with
            {
                IsInString = false,
                IsEscaped = false,
                IsStartOrEnd = currentFrame.Type == ContainerType.Array ? false : currentFrame.IsStartOrEnd
            });

            var stringValue = _currentString.ToString();

            // For property names, use the Key kind - no partial string events for property names
            if (_expectingPropertyName)
            {
                _lastPropertyName = stringValue[1..^1]; // Remove quotes for internal use
                yield return new JsonFragmentUpdate(
                    GetCurrentPath(),
                    JsonFragmentKind.Key,
                    stringValue,
                    stringValue
                );
            }
            else
            {
                // For value strings, use CompleteString
                yield return new JsonFragmentUpdate(
                    GetCurrentPath(),
                    JsonFragmentKind.CompleteString,
                    stringValue,
                    stringValue
                );
            }

            // Reset string state
            _currentString = null;
            _pendingStringUpdate = null;

            yield break;
        }

        // Regular character in string
        _currentString!.Append(c);
        _pendingStringUpdate!.Append(c);

        // Emit partial string updates for value strings only (not property names)
        if (!_expectingPropertyName)
        {
            yield return new JsonFragmentUpdate(
                GetCurrentPath(),
                JsonFragmentKind.PartialString,
                c.ToString(),
                c.ToString()
            );
        }
    }

    private IEnumerable<JsonFragmentUpdate> HandleStartObject()
    {
        var parentFrame = _contextStack.Count > 0 ? _contextStack.Peek() : null;
        var objectFrame = new Frame(ContainerType.Object, parentFrame)
        {
            IsStartOrEnd = true
        };

        // For objects within arrays, use the array's index for path
        if (parentFrame?.Type == ContainerType.Array)
        {
            // Don't inherit array's property name
            _contextStack.Pop();
            _contextStack.Push(parentFrame with { IsStartOrEnd = false });
        }
        // For root object or nested objects, check if we have a property name
        else if (!string.IsNullOrEmpty(_lastPropertyName))
        {
            // Only update the property name if this is a new object after a key
            if (_afterColon)
            {
                objectFrame = objectFrame with { PropertyName = _lastPropertyName };
                _lastPropertyName = string.Empty;
            }
        }

        _contextStack.Push(objectFrame);
        _expectingPropertyName = true;

        yield return new JsonFragmentUpdate(
            GetCurrentPath(),
            JsonFragmentKind.StartObject,
            "{",
            null
        );
    }

    private IEnumerable<JsonFragmentUpdate> HandleEndObject()
    {
        if (_contextStack.Count > 1 && _contextStack.Peek().Type == ContainerType.Object)
        {
            // First check if we have a pending token to emit
            if (_currentString != null)
            {
                foreach (var update in EmitCurrentToken())
                {
                    yield return update;
                }
            }

            var frame = _contextStack.Peek();
            var updatedFrame = frame with { IsStartOrEnd = true };
            _contextStack.Pop();
            _contextStack.Push(updatedFrame);

            // Calculate the path before popping the frame
            // For EndObject, we want the parent's path
            string currentPath;
            if (_contextStack.Count > 2)
            {
                // Store the frame temporarily
                var tempFrame = _contextStack.Pop();
                currentPath = GetCurrentPath(); // Get the parent's path
                _contextStack.Push(tempFrame); // Put the frame back
            }
            else
            {
                // If we're at the root object, use "root"
                currentPath = "root";
            }

            _contextStack.Pop();

            // Reset object-specific state flags when finishing an object
            // This ensures proper completion detection
            _afterColon = false;

            // If we're returning to a parent object, restore the appropriate state
            if (_contextStack.Count > 1 && _contextStack.Peek().Type == ContainerType.Object)
            {
                // We're back in a parent object, so we're no longer expecting a property name
                // (we just finished a value)
                _expectingPropertyName = false;
            }
            else
            {
                // We're at root level, no longer in any object context
                _expectingPropertyName = false;
            }

            yield return new JsonFragmentUpdate(
                currentPath,
                JsonFragmentKind.EndObject,
                "}",
                null
            );
        }
    }

    private IEnumerable<JsonFragmentUpdate> HandleStartArray()
    {
        var parentFrame = _contextStack.Count > 0 ? _contextStack.Peek() : null;
        var arrayFrame = new Frame(ContainerType.Array, parentFrame)
        {
            IsStartOrEnd = true
        };

        // For arrays after a property name
        if (parentFrame?.Type == ContainerType.Object && _afterColon)
        {
            arrayFrame = arrayFrame with { PropertyName = _lastPropertyName };
            _lastPropertyName = string.Empty; // Clear last property name after using it
        }
        // For nested arrays, don't inherit property name
        else if (parentFrame?.Type == ContainerType.Array)
        {
            _contextStack.Pop();
            _contextStack.Push(parentFrame with { IsStartOrEnd = false });
        }

        _contextStack.Push(arrayFrame);
        _afterColon = false;

        yield return new JsonFragmentUpdate(
            GetCurrentPath(),
            JsonFragmentKind.StartArray,
            "[",
            null
        );
    }

    private IEnumerable<JsonFragmentUpdate> HandleEndArray()
    {
        if (_contextStack.Count > 1 && _contextStack.Peek().Type == ContainerType.Array)
        {
            // First check if we have a pending token to emit
            if (_currentString != null)
            {
                foreach (var update in EmitCurrentToken())
                {
                    yield return update;
                }
            }

            var frame = _contextStack.Peek();
            var updatedFrame = frame with { IsStartOrEnd = true };
            _contextStack.Pop();
            _contextStack.Push(updatedFrame);
            var currentPath = GetCurrentPath();
            _contextStack.Pop();

            yield return new JsonFragmentUpdate(
                currentPath,
                JsonFragmentKind.EndArray,
                "]",
                null
            );
        }
    }

    private IEnumerable<JsonFragmentUpdate> HandleComma()
    {
        if (_contextStack.Count > 1)
        {
            // First check if we have a pending token to emit
            if (_currentString != null)
            {
                foreach (var update in EmitCurrentToken())
                {
                    yield return update;
                }
            }

            var currentFrame = _contextStack.Peek();
            if (currentFrame.Type == ContainerType.Array)
            {
                // Increment array index after emitting value
                _contextStack.Pop();
                _contextStack.Push(currentFrame with
                {
                    ElementIndex = currentFrame.ElementIndex + 1,
                    IsStartOrEnd = false
                });
            }
            else if (currentFrame.Type == ContainerType.Object)
            {
                _expectingPropertyName = true; // Reset for next property in object
                _lastPropertyName = string.Empty; // Clear last property name
                _afterColon = false; // Reset colon flag
            }
        }

        yield break;
    }

    private IEnumerable<JsonFragmentUpdate> HandleColon()
    {
        if (_contextStack.Count > 1 && _contextStack.Peek().Type == ContainerType.Object)
        {
            _expectingPropertyName = false;
            _afterColon = true;
        }

        yield break;
    }

    private IEnumerable<JsonFragmentUpdate> HandleStartString()
    {
        if (_contextStack.Count > 0)
        {
            // First check if we have a pending token to emit
            if (_currentString != null)
            {
                foreach (var update in EmitCurrentToken())
                {
                    yield return update;
                }
            }

            var currentFrame = _contextStack.Peek();
            // Update frame with new state
            _contextStack.Pop();
            _contextStack.Push(currentFrame with
            {
                IsInString = true,
                IsEscaped = false,
                IsStartOrEnd = currentFrame.Type == ContainerType.Array ? false : currentFrame.IsStartOrEnd
            });

            // Initialize string buffers
            _currentString = new StringBuilder();
            _pendingStringUpdate = new StringBuilder();
            _currentString.Append('"');
            _currentTokenType = TokenType.None; // Not a primitive token

            // Only emit StartString for value strings, not property names
            if (!_expectingPropertyName)
            {
                yield return new JsonFragmentUpdate(
                    GetCurrentPath(),
                    JsonFragmentKind.StartString,
                    "\"",
                    null
                );
            }
        }
    }

    private IEnumerable<JsonFragmentUpdate> HandlePrimitiveChar(char c)
    {
        if (_currentString == null)
        {
            _currentString = new StringBuilder();

            // Determine token type from first character
            if (char.IsDigit(c) || c == '-')
            {
                _currentTokenType = TokenType.Number;
            }
            else if (char.IsLetter(c))
            {
                _currentTokenType = TokenType.Literal;
            }
        }

        _currentString.Append(c);

        // Mark array frame as no longer at boundary when we start a value
        if (_contextStack.Count > 0 && _contextStack.Peek().Type == ContainerType.Array)
        {
            var frame = _contextStack.Peek();
            _contextStack.Pop();
            _contextStack.Push(frame with { IsStartOrEnd = false });
        }

        yield break;
    }

    private IEnumerable<JsonFragmentUpdate> EmitCurrentToken()
    {
        if (_currentString == null) yield break;

        var token = _currentString.ToString().Trim();

        if (_currentTokenType == TokenType.Literal)
        {
            yield return CreateLiteralTokenUpdate(token);
        }
        else if (_currentTokenType == TokenType.Number && double.TryParse(token, out _))
        {
            yield return new JsonFragmentUpdate(
                GetCurrentPath(),
                JsonFragmentKind.CompleteNumber,
                token,
                token
            );
        }

        // Reset token state
        _currentString = null;
        _currentTokenType = TokenType.None;
    }

    private JsonFragmentUpdate CreateLiteralTokenUpdate(string token)
    {
        if (token == "true" || token == "false")
        {
            return new JsonFragmentUpdate(
                GetCurrentPath(),
                JsonFragmentKind.CompleteBoolean,
                token,
                token
            );
        }
        else if (token == "null")
        {
            return new JsonFragmentUpdate(
                GetCurrentPath(),
                JsonFragmentKind.CompleteNull,
                token,
                null
            );
        }

        // Default case - shouldn't usually happen
        return new JsonFragmentUpdate(
            GetCurrentPath(),
            JsonFragmentKind.CompleteNull,
            token,
            null
        );
    }

    private string GetCurrentPath()
    {
        var pathBuilder = new StringBuilder("root");
        var frames = _contextStack.Reverse().Skip(1).ToArray(); // Skip root, and reverse to go from root down

        // For key updates, always return parent path
        if (_expectingPropertyName && !_afterColon)
        {
            return GetParentPath(frames);
        }

        // Build the full path from all frames
        foreach (var frame in frames)
        {
            AppendFrameToPath(pathBuilder, frame);
        }

        // If we're processing a value and have a property name, append it
        if (!_expectingPropertyName && !string.IsNullOrEmpty(_lastPropertyName))
        {
            pathBuilder.Append('.');
            pathBuilder.Append(_lastPropertyName);
        }

        return pathBuilder.ToString();
    }

    private string GetParentPath(Frame[] frames)
    {
        var pathBuilder = new StringBuilder("root");
        if (frames.Length >= 1)
        {
            var parentFrames = frames.Take(frames.Length - 1).ToArray();
            foreach (var frame in parentFrames)
            {
                AppendFrameToPath(pathBuilder, frame);
            }
        }
        return pathBuilder.ToString();
    }

    private static void AppendFrameToPath(StringBuilder pathBuilder, Frame frame)
    {
        switch (frame.Type)
        {
            case ContainerType.Object when !string.IsNullOrEmpty(frame.PropertyName):
                pathBuilder.Append('.');
                pathBuilder.Append(frame.PropertyName);
                break;
            case ContainerType.Array:
                if (!string.IsNullOrEmpty(frame.PropertyName))
                {
                    pathBuilder.Append('.');
                    pathBuilder.Append(frame.PropertyName);
                }
                // Only include array index if we're not at a boundary (start/end)
                if (!frame.IsStartOrEnd)
                {
                    pathBuilder.Append('[');
                    pathBuilder.Append(frame.ElementIndex);
                    pathBuilder.Append(']');
                }
                break;
        }
    }

    #endregion

    #region Helper Types

    private enum ContainerType
    {
        Root,
        Object,
        Array
    }

    private enum ValueKind
    {
        None,
        String,
        Number,
        Boolean,
        Null
    }

    private class ValueBuffer
    {
        public ValueKind Kind { get; set; } = ValueKind.None;
    }

    #endregion
}