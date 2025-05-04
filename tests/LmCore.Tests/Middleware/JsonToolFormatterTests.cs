using AchieveAi.LmDotnetTools.LmCore.Misc.Utils;
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

        // Act
        var updates = formatter.Format("testTool", json).ToList();

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

        // Act
        var updates = formatter.Format("testTool", json).ToList();

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
        
        // Act - Send JSON in fragments
        var updates1 = formatter.Format("testTool", "{\"status\":\"process").ToList();
        var updates2 = formatter.Format("testTool", "ing\",\"progress\":").ToList();
        var updates3 = formatter.Format("testTool", "50}").ToList();

        // Assert
        PrintUpdates(updates1, "Fragment 1");
        PrintUpdates(updates2, "Fragment 2");
        PrintUpdates(updates3, "Fragment 3");

        // Verify partial string handling
        Assert.Contains(updates1, u => u.Text == "\"status\"" && u.Color.Foreground == ConsoleColor.Green);
        Assert.Contains(updates2, u => u.Text == "ing" && u.Color.Foreground == ConsoleColor.Magenta);
        Assert.Contains(updates3, u => u.Text == "50" && u.Color.Foreground == ConsoleColor.Cyan);
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