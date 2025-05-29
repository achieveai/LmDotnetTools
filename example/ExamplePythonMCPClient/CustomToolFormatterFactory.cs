using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmCore.Misc.Utils;
using AchieveAi.LmDotnetTools.Misc.Middleware;
using AchieveAi.LmDotnetTools.Misc.Utils;

namespace AchieveAi.LmDotnetTools.Example.ExamplePythonMCPClient
{
    /// <summary>
    /// Custom formatter factory for specialized formatting of tool outputs
    /// </summary>
    public class CustomToolFormatterFactory : IToolFormatterFactory
    {
        private readonly ConsoleColorPair _functionNameColor = new() { Foreground = ConsoleColor.Blue };
        private readonly ConsoleColorPair _propertyNameColor = new() { Foreground = ConsoleColor.Cyan };
        private readonly ConsoleColorPair _propertyValueColor = new() { Foreground = ConsoleColor.White };
        private readonly ConsoleColorPair _punctuationColor = new() { Foreground = ConsoleColor.DarkGray };
        private readonly ConsoleColorPair _pythonCodeColor = new() { Foreground = ConsoleColor.Green };
        private readonly ConsoleColorPair _thinkingColor = new() { Foreground = ConsoleColor.Yellow };

        // Markdown syntax highlighting colors
        private readonly ConsoleColorPair _headerColor = new() { Foreground = ConsoleColor.Magenta };
        private readonly ConsoleColorPair _boldColor = new() { Foreground = ConsoleColor.Cyan };
        private readonly ConsoleColorPair _italicColor = new() { Foreground = ConsoleColor.DarkCyan };
        private readonly ConsoleColorPair _listNumberColor = new() { Foreground = ConsoleColor.Green };
        private readonly ConsoleColorPair _bulletListColor = new() { Foreground = ConsoleColor.DarkGreen };
        private readonly ConsoleColorPair _blockquoteColor = new() { Foreground = ConsoleColor.DarkGray };
        private readonly ConsoleColorPair _codeBlockColor = new() { Foreground = ConsoleColor.DarkYellow };

        // Python syntax highlighting colors
        private readonly ConsoleColorPair _pythonKeywordColor = new() { Foreground = ConsoleColor.Blue };
        private readonly ConsoleColorPair _pythonStringColor = new() { Foreground = ConsoleColor.Yellow };
        private readonly ConsoleColorPair _pythonNumberColor = new() { Foreground = ConsoleColor.Magenta };
        private readonly ConsoleColorPair _pythonBoolColor = new() { Foreground = ConsoleColor.Cyan };
        private readonly ConsoleColorPair _pythonOperatorColor = new() { Foreground = ConsoleColor.DarkGray };
        private readonly ConsoleColorPair _pythonCommentColor = new() { Foreground = ConsoleColor.DarkGreen };
        private readonly ConsoleColorPair _pythonIdentifierColor = new() { Foreground = ConsoleColor.White };
        private readonly ConsoleColorPair _pythonPunctuationColor = new() { Foreground = ConsoleColor.Green };        private readonly ConsoleColorPair _errorColor = new() { Foreground = ConsoleColor.Red };
        
        // State management for JSON accumulation
        private readonly JsonFragmentToStructuredUpdateGenerator _accumulator = new("tool");
        private readonly IToolFormatterFactory _defaultFormatterFactory = new DefaultToolFormatterFactory(new ConsoleColorPair { Foreground = ConsoleColor.Blue });
        private ConsoleColorPair _markdownColor = new() { Foreground = ConsoleColor.White };
        
        // Cached lines for partial string processing
        private string _thinkingLine = string.Empty;
        private string _pythonLine = string.Empty;

        // Regular expressions for Python syntax highlighting
        private static readonly Regex PythonKeywordRegex = new(@"\b(def|class|if|else|elif|for|while|import|from|return|try|except|finally|with|as|in|is|and|or|not|True|False|None)\b", RegexOptions.Compiled);
        private static readonly Regex PythonStringRegex = new(@"(""[^""\\]*(?:\\.[^""\\]*)*""|'[^'\\]*(?:\\.[^'\\]*)*'|""""|''''|"""""".*?""""""|'''.*?''')", RegexOptions.Compiled);
        private static readonly Regex PythonNumberRegex = new(@"\b\d+(\.\d+)?([eE][+-]?\d+)?\b", RegexOptions.Compiled);
        private static readonly Regex PythonBoolRegex = new(@"\b(True|False|None)\b", RegexOptions.Compiled);
        private static readonly Regex PythonOperatorRegex = new(@"(\+|\-|\*|\/|\%|\=\=|\!\=|\<|\>|\<\=|\>\=|\=|\+\=|\-\=|\*\=|\/\=|\%\=|\&|\||\^|\~|\<\<|\>\>|\&\=|\|\=|\^=)", RegexOptions.Compiled);
        private static readonly Regex PythonCommentRegex = new(@"#.*$", RegexOptions.Compiled);
        private static readonly Regex PythonIdentifierRegex = new(@"[a-zA-Z_][a-zA-Z0-9_]*", RegexOptions.Compiled);

        /// <summary>
        /// Creates a formatter for the specified tool
        /// </summary>
        /// <param name="toolCallName">Name of the tool to create a formatter for</param>
        /// <returns>A formatter for the specified tool</returns>
        public ToolFormatter CreateFormatter(string toolCallName)
        {
            // Create specialized formatters based on tool name
            if (toolCallName.EndsWith("sequentialthinking", StringComparison.OrdinalIgnoreCase))
            {                // Reset JSON accumulation state for new tool call
                _accumulator.Reset();
                return SequentialThinkingFormatter;
            }
            else if (toolCallName.EndsWith("execute_python_in_container", StringComparison.OrdinalIgnoreCase))
            {                // Reset JSON accumulation state for new tool call
                _accumulator.Reset();
                return PythonCodeFormatter;
            }
            else
            {
                return _defaultFormatterFactory.GetFormatter(toolCallName);
            }
        }        /// <summary>
        /// Specialized formatter for sequential thinking tool
        /// </summary>
        private IEnumerable<(ConsoleColorPair, string)> SequentialThinkingFormatter(string toolName, string paramUpdate)
        {
            var results = new List<(ConsoleColorPair, string)>();

            // For name-only first call, just show the function name
            if (string.IsNullOrEmpty(paramUpdate))
            {
                results.Add((_functionNameColor, "Sequential Thinking" + " "));
                return results;
            }

            try
            {
                // Process the fragment through the accumulator
                var updates = _accumulator.AddFragment(paramUpdate);

                foreach (var update in updates)
                {                    // We're interested in thought properties - both complete and partial
                    if (update.Path == "root.thought" && update.TextValue != null)
                    {
                        // Unescape the JSON string
                        string thought = JsonStringUtils.UnescapeJsonString(update.TextValue);
                        
                        // Check if this is a complete or partial update
                        bool isComplete = update.Kind == JsonFragmentKind.CompleteString;

                        // For better incremental experience, process this chunk directly
                        // but don't process the last line unless it's a complete string
                        if (!string.IsNullOrEmpty(_thinkingLine))
                        {
                            // Append the new chunk to the cached line
                            thought = _thinkingLine + thought;
                            _thinkingLine = string.Empty;
                        }

                        // Format and add to results
                        foreach (var token in FormatMarkdown(thought, isComplete))
                        {
                            results.Add(token);
                        }

                        // For complete strings, we're done with this fragment and should process any cached line
                        if (isComplete && !string.IsNullOrEmpty(_thinkingLine))
                        {
                            // Process the last cached line
                            foreach (var token in ProcessSingleLine(_thinkingLine))
                            {
                                results.Add(token);
                            }
                            results.Add((_thinkingColor, Environment.NewLine));
                            _thinkingLine = string.Empty;
                        }
                    }
                }
            }
            catch (Exception ex)
            {                // On error, clear state and output raw text with error marker
                _accumulator.Reset();
                _thinkingLine = string.Empty; // Also clear the cached line
                results.Add((_errorColor, $"[Parser Error: {ex.Message}] "));
                results.Add((_propertyValueColor, paramUpdate));
            }

            return results;
        }

        /// <summary>
        /// Formats a markdown string with syntax highlighting
        /// </summary>
        private IEnumerable<(ConsoleColorPair, string)> FormatMarkdown(string markdown, bool isComplete = false)
        {
            if (string.IsNullOrEmpty(markdown))
            {
                yield break;
            }

            // Split into lines for easier processing
            var lines = markdown.Split('\n');

            // If this is not a complete string, cache the last line for later
            if (!isComplete && lines.Length > 0)
            {
                _thinkingLine = lines[lines.Length - 1]; // Store the last line for further processing
                lines = lines.Take(lines.Length - 1).ToArray(); // Exclude the last line for now
            }
            foreach (var line in lines)
            {
                // Process each complete line using our single line processor
                foreach (var token in ProcessSingleLine(line))
                {
                    yield return token;
                }

                // Add line break after each complete line
                yield return (_thinkingColor, Environment.NewLine);
            }
        }        /// <summary>
        /// Processes a single markdown line with syntax highlighting
        /// </summary>
        private IEnumerable<(ConsoleColorPair, string)> ProcessSingleLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                yield return (_thinkingColor, Environment.NewLine);
                yield break;
            }

            // Header detection
            if (line.TrimStart().StartsWith("#"))
            {
                yield return (_headerColor, line);
                yield break;
            }

            // Blockquote detection
            if (line.TrimStart().StartsWith(">"))
            {
                int quoteEnd = line.IndexOf(">");
                yield return (_blockquoteColor, line.Substring(0, quoteEnd + 1));
                yield return (_thinkingColor, line.Substring(quoteEnd + 1));
                yield break;
            }

            // Numbered list detection
            var listMatch = Regex.Match(line, @"^\s*(\d+\.\s+)");
            if (listMatch.Success)
            {
                yield return (_listNumberColor, listMatch.Groups[1].Value);
                yield return (_thinkingColor, line.Substring(listMatch.Groups[1].Value.Length));
                yield break;
            }

            // Bullet list detection
            var bulletMatch = Regex.Match(line, @"^\s*([*\-]\s+)");
            if (bulletMatch.Success)
            {
                yield return (_bulletListColor, bulletMatch.Groups[1].Value);
                yield return (_thinkingColor, line.Substring(bulletMatch.Groups[1].Value.Length));
                yield break;
            }

            // Process inline formatting (bold, italic, code)
            int pos = 0;
            while (pos < line.Length)
            {
                // Bold detection
                if (pos + 1 < line.Length && line.Substring(pos, 2) == "**")
                {
                    int closeBold = line.IndexOf("**", pos + 2);
                    if (closeBold != -1)
                    {
                        yield return (_boldColor, line.Substring(pos, closeBold + 2 - pos));
                        pos = closeBold + 2;
                        continue;
                    }
                }

                // Italic detection
                if (pos < line.Length && line[pos] == '*' && (pos == 0 || line[pos - 1] != '*'))
                {
                    int closeItalic = line.IndexOf("*", pos + 1);
                    if (closeItalic != -1 && (closeItalic + 1 >= line.Length || line[closeItalic + 1] != '*'))
                    {
                        yield return (_italicColor, line.Substring(pos, closeItalic + 1 - pos));
                        pos = closeItalic + 1;
                        continue;
                    }
                }

                // Inline code detection
                if (pos < line.Length && line[pos] == '`')
                {
                    int closeCode = line.IndexOf("`", pos + 1);
                    if (closeCode != -1)
                    {
                        yield return (_codeBlockColor, line.Substring(pos, closeCode + 1 - pos));
                        pos = closeCode + 1;
                        continue;
                    }
                }

                // Regular text (one character at a time)
                yield return (_thinkingColor, line[pos].ToString());
                pos++;
            }
        }        /// <summary>
        /// Specialized formatter for Python code execution
        /// </summary>
        private IEnumerable<(ConsoleColorPair, string)> PythonCodeFormatter(string toolName, string paramUpdate)
        {
            var results = new List<(ConsoleColorPair, string)>();

            // For name-only first call, just show the function name
            if (string.IsNullOrEmpty(paramUpdate))
            {
                results.Add((_functionNameColor, "Python Code Execution" + " "));
                return results;
            }

            try
            {
                // Process the fragment through the accumulator
                var updates = _accumulator.AddFragment(paramUpdate);

                foreach (var update in updates)
                {                    // We're interested in code properties - both complete and partial
                    if (update.Path.EndsWith("code") && update.TextValue != null)
                    {
                        // Unescape the JSON string
                        string code = JsonStringUtils.UnescapeJsonString(update.TextValue);
                        
                        // Check if this is a complete or partial update
                        bool isComplete = update.Kind == JsonFragmentKind.CompleteString;

                        // For better incremental experience, process this chunk directly
                        // but don't process the last line unless it's a complete string
                        if (!string.IsNullOrEmpty(_pythonLine))
                        {
                            // Append the new chunk to the cached line
                            code = _pythonLine + code;
                            _pythonLine = string.Empty;
                        }

                        // Format and add to results
                        foreach (var token in FormatPythonCode(code, isComplete))
                        {
                            results.Add(token);
                        }

                        // For complete strings, we're done with this fragment and should process any cached line
                        if (isComplete && !string.IsNullOrEmpty(_pythonLine))
                        {
                            // Process the last cached line
                            foreach (var token in FormatPythonLine(_pythonLine))
                            {
                                results.Add(token);
                            }
                            results.Add((_pythonCodeColor, Environment.NewLine));
                            _pythonLine = string.Empty;
                        }
                    }                }
            }
            catch (Exception ex)
            {
                // On error, clear state and output raw text with error marker
                _accumulator.Reset();
                _pythonLine = string.Empty; // Also clear the cached line
                results.Add((_errorColor, $"[Parser Error: {ex.Message}] "));
                results.Add((_pythonCodeColor, paramUpdate));
            }

            return results;
        }        /// <summary>
        /// Formats Python code with syntax highlighting
        /// </summary>
        private IEnumerable<(ConsoleColorPair, string)> FormatPythonCode(string code, bool isComplete = true)
        {
            if (string.IsNullOrEmpty(code))
            {
                yield break;
            }

            // Split into lines for line-by-line processing
            var lines = code.Split('\n');

            // If this is not a complete string, cache the last line for later
            if (!isComplete && lines.Length > 0)
            {
                _pythonLine = lines[lines.Length - 1]; // Store the last line for further processing
                lines = lines.Take(lines.Length - 1).ToArray(); // Exclude the last line for now
            }            foreach (var line in lines)
            {
                // Process line segment by segment based on regex matches
                foreach (var token in FormatPythonLine(line))
                {
                    yield return token;
                }
                
                yield return ((_pythonCodeColor, Environment.NewLine));
            }
        }
          /// <summary>
        /// Formats a single line of Python code with syntax highlighting
        /// </summary>
        private IEnumerable<(ConsoleColorPair, string)> FormatPythonLine(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                yield break;
            }
            
            int pos = 0;
            
            // Indicate line start with a consistent indentation
            yield return ((_pythonPunctuationColor, "  "));

            while (pos < line.Length)
            {
                // Try to match each syntax element in priority order

                // 1. Comments (should be checked first as they override everything to end of line)
                var commentMatch = PythonCommentRegex.Match(line, pos);
                if (commentMatch.Success && commentMatch.Index == pos)
                {
                    yield return ((_pythonCommentColor, commentMatch.Value));
                    pos = line.Length; // Skip to end of line
                    continue;
                }

                // 2. String literals
                var stringMatch = PythonStringRegex.Match(line, pos);
                if (stringMatch.Success && stringMatch.Index == pos)
                {
                    yield return ((_pythonStringColor, stringMatch.Value));
                    pos += stringMatch.Length;
                    continue;
                }

                // 3. Keywords
                var keywordMatch = PythonKeywordRegex.Match(line, pos);
                if (keywordMatch.Success && keywordMatch.Index == pos)
                {
                    yield return ((_pythonKeywordColor, keywordMatch.Value));
                    pos += keywordMatch.Length;
                    continue;
                }

                // 4. Boolean literals
                var boolMatch = PythonBoolRegex.Match(line, pos);
                if (boolMatch.Success && boolMatch.Index == pos)
                {
                    yield return ((_pythonBoolColor, boolMatch.Value));
                    pos += boolMatch.Length;
                    continue;
                }

                // 5. Number literals
                var numberMatch = PythonNumberRegex.Match(line, pos);
                if (numberMatch.Success && numberMatch.Index == pos)
                {
                    yield return ((_pythonNumberColor, numberMatch.Value));
                    pos += numberMatch.Length;
                    continue;
                }

                // 6. Operators
                var operatorMatch = PythonOperatorRegex.Match(line, pos);
                if (operatorMatch.Success && operatorMatch.Index == pos)
                {
                    yield return ((_pythonOperatorColor, operatorMatch.Value));
                    pos += operatorMatch.Length;
                    continue;
                }

                // 7. Identifiers (variable names, function names, etc.)
                var identifierMatch = PythonIdentifierRegex.Match(line, pos);
                if (identifierMatch.Success && identifierMatch.Index == pos)
                {
                    yield return ((_pythonIdentifierColor, identifierMatch.Value));
                    pos += identifierMatch.Length;
                    continue;
                }

                // 8. Any other character (punctuation, etc.)
                yield return ((_pythonPunctuationColor, line[pos].ToString()));
                pos++;
            }
        }

        public ToolFormatter GetFormatter(string toolName)
        {
            return CreateFormatter(toolName);
        }
    }
}
