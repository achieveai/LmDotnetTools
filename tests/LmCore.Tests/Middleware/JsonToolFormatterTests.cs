using AchieveAi.LmDotnetTools.Misc.Utils;
using AchieveAi.LmDotnetTools.Misc.Middleware;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class JsonToolFormatterTests
{
    private readonly ITestOutputHelper _output;

    public JsonToolFormatterTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Test_SimpleJsonFormatting()
    {
        // Arrange
        var formatter = new JsonToolFormatter();
        var json = "{\"name\":\"John\",\"age\":30}";
        var fragmentUpdates = CreateFragmentUpdates("testTool", json);

        // Act
        var updates = formatter.Format("testTool", fragmentUpdates).ToList();

        // Assert
        PrintUpdates(updates);

        // Verify basic structure
        Assert.Contains(updates, u => u.Text == "{");
        Assert.Contains(updates, u => u.Text == "\"name\"");
        Assert.Contains(updates, u => u.Text == ": ");
        Assert.Contains(updates, u => u.Text == "\"John");
        Assert.Contains(updates, u => u.Text == ",");
        Assert.Contains(updates, u => u.Text == "\"age\"");
        Assert.Contains(updates, u => u.Text == "30");
        Assert.Contains(updates, u => u.Text == "}");

        // Verify colors
        Assert.Contains(updates, u => u.Text == "\"name\"" && u.Color.Foreground == ConsoleColor.Green);
        Assert.Contains(updates, u => u.Text == "\"John" && u.Color.Foreground == ConsoleColor.Magenta);
        Assert.Contains(updates, u => u.Text == "\"" && u.Color.Foreground == ConsoleColor.Magenta);
        Assert.Contains(updates, u => u.Text == "30" && u.Color.Foreground == ConsoleColor.Cyan);
    }

    [Fact]
    public void Test_NestedJsonFormatting()
    {
        // Arrange
        var formatter = new JsonToolFormatter();
        var json = "{\"user\":{\"name\":\"John\",\"scores\":[10,20,30]}}";
        var fragmentUpdates = CreateFragmentUpdates("testTool", json);

        // Act
        var updates = formatter.Format("testTool", fragmentUpdates).ToList();

        // Assert
        PrintUpdates(updates);

        // Verify indentation and structure
        Assert.Contains(updates, u => u.Text.Contains("\n  ")); // Check for indentation
        Assert.Contains(updates, u => u.Text == "\"user\"" && u.Color.Foreground == ConsoleColor.Green);
        Assert.Contains(updates, u => u.Text == "[" && u.Color.Foreground == ConsoleColor.White);
        Assert.Contains(updates, u => u.Text == "10" && u.Color.Foreground == ConsoleColor.Cyan);
    }

    [Fact]
    public void Test_StreamedJsonFormatting()
    {
        // Arrange
        var formatter = new JsonToolFormatter();
        
        // Act - Send JSON in fragments using a single generator for streaming behavior
        var generator = new JsonFragmentToStructuredUpdateGenerator("testTool");
        var fragmentUpdates1 = generator.AddFragment("{\"status\":\"process").ToList();
        var fragmentUpdates2 = generator.AddFragment("ing\",\"progress\":").ToList();
        var fragmentUpdates3 = generator.AddFragment("50}").ToList();
        
        var updates1 = formatter.Format("testTool", fragmentUpdates1).ToList();
        var updates2 = formatter.Format("testTool", fragmentUpdates2).ToList();
        var updates3 = formatter.Format("testTool", fragmentUpdates3).ToList();

        // Assert
        PrintUpdates(updates1, "Fragment 1");
        PrintUpdates(updates2, "Fragment 2");
        PrintUpdates(updates3, "Fragment 3");

        // Verify partial string handling
        Assert.Contains(updates1, u => u.Text == "\"status\"" && u.Color.Foreground == ConsoleColor.Green);
        Assert.Contains(updates2, u => u.Text == "ing" && u.Color.Foreground == ConsoleColor.Magenta);
        Assert.Contains(updates3, u => u.Text == "50" && u.Color.Foreground == ConsoleColor.Cyan);
    }

    /// <summary>
    /// Helper method to create JsonFragmentUpdates from raw JSON for testing
    /// </summary>
    private IEnumerable<JsonFragmentUpdate> CreateFragmentUpdates(string toolName, string json)
    {
        var generator = new JsonFragmentToStructuredUpdateGenerator(toolName);
        return generator.AddFragment(json);
    }

    private void PrintUpdates(List<(ConsoleColorPair Color, string Text)> updates, string? header = null)
    {
        if (header != null)
        {
            _output.WriteLine($"\n{header}:");
        }

        foreach (var (color, text) in updates)
        {
            _output.WriteLine($"Color: {color.Foreground}, Text: |{text.Replace("\n", "\\n")}|");
        }
    }
} 