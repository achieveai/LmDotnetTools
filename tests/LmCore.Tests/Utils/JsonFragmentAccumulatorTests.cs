using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Utils;

public class JsonFragmentAccumulatorTests
{
    private static readonly string[] item = ["{\"key\":", " \"value\"}"];
    private static readonly string[] itemArray = ["[1,", " 2, 3]"];
    private static readonly string[] itemArray0 = ["{\"outer\": {\"inner\":", " \"value\"}}"];
    private static readonly string[] itemArray1 = ["\"simple", "_string\""];
    private readonly ITestOutputHelper output;

    public JsonFragmentAccumulatorTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    /// <summary>
    ///     Test data for JSON completion event testing
    /// </summary>
    public static IEnumerable<object[]> JsonCompletionTestCases =>
        [
            // Simple object completion
            ["test_tool", item, "Simple object should emit JsonComplete event when closed"],
            // Simple array completion
            ["test_tool", itemArray, "Simple array should emit JsonComplete event when closed"],
            // Nested object completion
            ["test_tool", itemArray0, "Nested object should emit JsonComplete event when fully closed"],
            // Single value completion
            ["test_tool", itemArray1, "Simple string should emit JsonComplete event when closed"],
        ];

    /// <summary>
    ///     Test that basic JSON paths are correctly tracked
    /// </summary>
    [Fact]
    public void Test_BasicPath()
    {
        // Arrange
        var accumulator = new JsonFragmentToStructuredUpdateGenerator("testTool");

        // Act
        var updates = accumulator.AddFragment("{\"name\":\"John\",\"age\":30}").ToList();

        // Assert based on what the code actually does
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.StartObject);
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.Key && u.TextValue == "\"name\"");
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.StartString);
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.CompleteString);
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.Key && u.TextValue == "\"age\"");
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.CompleteNumber);
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.EndObject);

        // Path assertions - we'll do more basic path checks for now
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.StartObject && u.Path.Contains("root"));

        // Print updates for debugging
        PrintUpdates(updates, "All Updates");
    }

    /// <summary>
    ///     Test that array paths and elements are correctly tracked
    /// </summary>
    [Fact]
    public void Test_ArrayPath()
    {
        // Arrange
        var accumulator = new JsonFragmentToStructuredUpdateGenerator("testTool");

        // Act
        var updates = accumulator.AddFragment("{\"items\":[1,2,\"three\"]}").ToList();

        // Assert - using looser assertions to check event flow but not exact paths
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.StartObject);
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.Key && u.TextValue == "\"items\"");
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.StartArray);
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.CompleteNumber && u.TextValue == "1");
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.CompleteNumber && u.TextValue == "2");
        Assert.Contains(
            updates,
            u => u.Kind == JsonFragmentKind.CompleteString && u.TextValue != null && u.TextValue.Contains("three")
        );
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.EndArray);
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.EndObject);

        // Print updates for debugging
        PrintUpdates(updates, "All Updates");
    }

    /// <summary>
    ///     Test that partial string values are emitted correctly
    /// </summary>
    [Fact]
    public void Test_PartialStringValues()
    {
        // Arrange
        var accumulator = new JsonFragmentToStructuredUpdateGenerator("testTool");

        // Act
        var updates1 = accumulator.AddFragment("{\"description\":\"This is ").ToList();
        var updates2 = accumulator.AddFragment("a long string").ToList();
        var updates3 = accumulator.AddFragment(" that spans multiple fragments\"}").ToList();

        // Assert - First fragment
        Assert.Contains(updates1, u => u.Kind == JsonFragmentKind.StartObject);
        Assert.Contains(updates1, u => u.Kind == JsonFragmentKind.Key && u.TextValue == "\"description\"");
        Assert.Contains(updates1, u => u.Kind == JsonFragmentKind.StartString);
        Assert.Contains(updates1, u => u.Kind == JsonFragmentKind.PartialString);

        // Assert - Second fragment
        Assert.Contains(updates2, u => u.Kind == JsonFragmentKind.PartialString);

        // Assert - Third fragment
        Assert.Contains(updates3, u => u.Kind == JsonFragmentKind.PartialString);
        Assert.Contains(updates3, u => u.Kind == JsonFragmentKind.CompleteString);
        Assert.Contains(updates3, u => u.Kind == JsonFragmentKind.EndObject);

        // Verify the accumulated JSON
        Assert.Equal(
            "{\"description\":\"This is a long string that spans multiple fragments\"}",
            accumulator.CurrentJson
        );

        // Print updates for debugging
        PrintUpdates(updates1, "Fragment 1");
        PrintUpdates(updates2, "Fragment 2");
        PrintUpdates(updates3, "Fragment 3");
    }

    /// <summary>
    ///     Test that keys are emitted correctly with their full values
    /// </summary>
    [Fact]
    public void Test_Keys()
    {
        // Arrange
        var accumulator = new JsonFragmentToStructuredUpdateGenerator("testTool");

        // Act - Send key parts in fragments
        var updates1 = accumulator.AddFragment("{\"user").ToList();
        var updates2 = accumulator.AddFragment("Name\":\"John\"}").ToList();

        // Assert - No partial key events should be emitted
        Assert.DoesNotContain(updates1, u => u.Kind == JsonFragmentKind.PartialString);

        // Assert - Full key is emitted once complete
        Assert.Contains(updates2, u => u.Kind == JsonFragmentKind.Key && u.TextValue == "\"userName\"");
        Assert.Contains(updates2, u => u.Kind == JsonFragmentKind.CompleteString);

        // Act 2 - Test with escaped characters in key
        accumulator.Reset();
        var updates3 = accumulator.AddFragment("{\"escaped\\\"Key\":\"value\"}").ToList();

        // Assert 2 - Just check that we get a Key event
        Assert.Contains(updates3, u => u.Kind == JsonFragmentKind.Key && u.TextValue == "\"escaped\\\"Key\"");

        // Print updates for debugging
        PrintUpdates(updates1, "Fragment 1");
        PrintUpdates(updates2, "Fragment 2");
        PrintUpdates(updates3, "Fragment 3");
    }

    /// <summary>
    ///     Test that numbers, nulls and booleans are handled properly
    /// </summary>
    [Fact]
    public void Test_ScalarValues()
    {
        // Arrange
        var accumulator = new JsonFragmentToStructuredUpdateGenerator("testTool");

        // Act
        var updates = accumulator
            .AddFragment(
                "{\"integer\":42,\"decimal\":3.14,\"boolean\":true,\"nullValue\":null,\"scientificNotation\":1.23e-4}"
            )
            .ToList();

        PrintUpdates(updates);

        // Assert - basic type assertions
        AssertHasUpdate(updates, JsonFragmentKind.CompleteNumber, "root.integer", "42");
        AssertHasUpdate(updates, JsonFragmentKind.CompleteNumber, "root.decimal", "3.14");
        AssertHasUpdate(updates, JsonFragmentKind.CompleteBoolean, "root.boolean", "true");
        AssertHasUpdate(updates, JsonFragmentKind.CompleteNull, "root.nullValue", "null");
        AssertHasUpdate(updates, JsonFragmentKind.CompleteNumber, "root.scientificNotation", "1.23e-4");
    }

    /// <summary>
    ///     Test that nested objects and arrays are handled correctly
    /// </summary>
    [Fact]
    public void Test_NestedStructures()
    {
        // Arrange
        var accumulator = new JsonFragmentToStructuredUpdateGenerator("testTool");

        // Act
        var updates = accumulator
            .AddFragment(
                "{\"user\":{\"name\":\"John\",\"address\":{\"city\":\"New York\"}},\"scores\":[10,[20,30],{\"final\":100}]}"
            )
            .ToList();

        PrintUpdates(updates);

        Assert.DoesNotContain(updates, u => u.Kind == JsonFragmentKind.PartialString && u.TextValue == "\"New Y");

        // Test each path
        AssertHasUpdate(updates, JsonFragmentKind.Key, "root.user", "\"name\"");
        AssertHasUpdate(updates, JsonFragmentKind.CompleteString, "root.user.name", "\"John\"");
        AssertHasUpdate(updates, JsonFragmentKind.Key, "root.user.address", "\"city\"");
        AssertHasUpdate(updates, JsonFragmentKind.CompleteString, "root.user.address.city", "\"New York\"");
        AssertHasUpdate(updates, JsonFragmentKind.CompleteNumber, "root.scores[0]", "10");
        AssertHasUpdate(updates, JsonFragmentKind.CompleteNumber, "root.scores[1][0]", "20");
        AssertHasUpdate(updates, JsonFragmentKind.CompleteNumber, "root.scores[1][1]", "30");
        AssertHasUpdate(updates, JsonFragmentKind.Key, "root.scores[2]", "\"final\"");
        AssertHasUpdate(updates, JsonFragmentKind.CompleteNumber, "root.scores[2].final", "100");
    }

    [Fact]
    public void Test_NestedObjectPaths()
    {
        // Arrange
        var accumulator = new JsonFragmentToStructuredUpdateGenerator("testTool");

        // Act
        var updates = accumulator
            .AddFragment("{\"user\":{\"name\":\"John\",\"address\":{\"city\":\"New York\"}}}")
            .ToList();

        // Print updates for debugging
        PrintUpdates(updates, "All Updates");

        // Validate key paths
        AssertHasUpdate(updates, JsonFragmentKind.Key, "root", "\"user\"");
        AssertHasUpdate(updates, JsonFragmentKind.Key, "root.user", "\"name\"");
        AssertHasUpdate(updates, JsonFragmentKind.Key, "root.user.address", "\"city\"");

        // Validate value paths
        AssertHasUpdate(updates, JsonFragmentKind.CompleteString, "root.user.name", "\"John\"");
        AssertHasUpdate(updates, JsonFragmentKind.CompleteString, "root.user.address.city", "\"New York\"");
    }

    [Fact]
    public void Test_NestedArrayPaths()
    {
        // Arrange
        var accumulator = new JsonFragmentToStructuredUpdateGenerator("testTool");

        // Act
        var updates = accumulator.AddFragment("{\"scores\":[10,[20,30],{\"final\":100}]}").ToList();

        // Print updates for debugging
        PrintUpdates(updates, "All Updates");

        // Validate array paths
        AssertHasUpdate(updates, JsonFragmentKind.CompleteNumber, "root.scores[0]", "10");
        AssertHasUpdate(updates, JsonFragmentKind.CompleteNumber, "root.scores[1][0]", "20");
        AssertHasUpdate(updates, JsonFragmentKind.CompleteNumber, "root.scores[1][1]", "30");
        AssertHasUpdate(updates, JsonFragmentKind.Key, "root.scores[2]", "\"final\"");
        AssertHasUpdate(updates, JsonFragmentKind.CompleteNumber, "root.scores[2].final", "100");
    }

    [Fact]
    public void Test_SimpleObjectPath()
    {
        // Arrange
        var accumulator = new JsonFragmentToStructuredUpdateGenerator("testTool");

        // Act
        var updates = accumulator.AddFragment("{\"name\":\"John\"}").ToList();

        // Print updates for debugging
        PrintUpdates(updates, "All Updates");

        // Assert
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.StartObject && u.Path == "root");
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.Key && u.TextValue == "\"name\"" && u.Path == "root");
        Assert.Contains(
            updates,
            u => u.Kind == JsonFragmentKind.CompleteString && u.TextValue == "\"John\"" && u.Path == "root.name"
        );
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.EndObject && u.Path == "root");
    }

    [Fact]
    public void Test_SimpleArrayPath()
    {
        // Arrange
        var accumulator = new JsonFragmentToStructuredUpdateGenerator("testTool");

        // Act
        var updates = accumulator.AddFragment("[1,2,3]").ToList();

        // Print updates for debugging
        PrintUpdates(updates, "All Updates");

        // Assert
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.StartArray && u.Path == "root");
        Assert.Contains(
            updates,
            u => u.Kind == JsonFragmentKind.CompleteNumber && u.TextValue == "1" && u.Path == "root[0]"
        );
        Assert.Contains(
            updates,
            u => u.Kind == JsonFragmentKind.CompleteNumber && u.TextValue == "2" && u.Path == "root[1]"
        );
        Assert.Contains(
            updates,
            u => u.Kind == JsonFragmentKind.CompleteNumber && u.TextValue == "3" && u.Path == "root[2]"
        );
        Assert.Contains(updates, u => u.Kind == JsonFragmentKind.EndArray && u.Path == "root");
    }

    /// <summary>
    ///     Test that string updates are grouped by fragment but still incremental
    /// </summary>
    [Fact]
    public void Test_StringUpdatesGroupedByFragment()
    {
        // Arrange
        var accumulator = new JsonFragmentToStructuredUpdateGenerator("testTool");

        // Act - Send string in multiple fragments
        var updates1 = accumulator.AddFragment("{\"message\":\"First ").ToList();
        var updates2 = accumulator.AddFragment("second ").ToList();
        var updates3 = accumulator.AddFragment("third\"}").ToList();

        // Print updates for debugging
        PrintUpdates(updates1, "Fragment 1");
        PrintUpdates(updates2, "Fragment 2");
        PrintUpdates(updates3, "Fragment 3");

        // Assert
        // Fragment 1 should have one PartialString with the entire first fragment's string content
        AssertSingleStringUpdate(updates1, JsonFragmentKind.PartialString, "First ");

        // Fragment 2 should have one PartialString with the entire second fragment's string content
        AssertSingleStringUpdate(updates2, JsonFragmentKind.PartialString, "second ");

        // Fragment 3 should have one PartialString with content up to the quote, then a CompleteString
        AssertSingleStringUpdate(updates3, JsonFragmentKind.PartialString, "third");

        // Should also have a CompleteString in the last fragment
        Assert.Contains(updates3, u => u.Kind == JsonFragmentKind.CompleteString);

        // Verify the final JSON is correct
        Assert.Equal("{\"message\":\"First second third\"}", accumulator.CurrentJson);
    }

    /// <summary>
    ///     Test that escape sequences split between fragments are handled correctly
    /// </summary>
    [Fact]
    public void Test_EscapeSequencesBetweenFragments()
    {
        // Arrange
        var accumulator = new JsonFragmentToStructuredUpdateGenerator("testTool");

        // Act - Send escape sequence split across fragments
        var updates1 = accumulator.AddFragment("{\"escaped\":\"before\\").ToList();
        var updates2 = accumulator.AddFragment("\"after\"}").ToList();

        // Print updates for debugging
        PrintUpdates(updates1, "Fragment 1");
        PrintUpdates(updates2, "Fragment 2");

        // Assert
        // Fragment 1 should have one PartialString with content up to (but not including) the escape char
        AssertSingleStringUpdate(updates1, JsonFragmentKind.PartialString, "before");

        // Fragment 2 should have PartialString with the content after the escape sequence
        var partialStrings2 = updates2.Where(u => u.Kind == JsonFragmentKind.PartialString).ToList();
        Assert.Contains(partialStrings2, u => u.TextValue == "after");

        // And verify the complete string at the end includes the proper escape sequence
        var completeString = Assert.Single(updates2, u => u.Kind == JsonFragmentKind.CompleteString);
        Assert.Equal("\"before\\\"after\"", completeString.TextValue);

        // Verify the final JSON is correct
        Assert.Equal("{\"escaped\":\"before\\\"after\"}", accumulator.CurrentJson);
    }

    [Theory]
    [MemberData(nameof(JsonCompletionTestCases))]
    public void Test_JsonCompletionEvent(string toolName, string[] fragments, string description)
    {
        TestContextLogger.LogDebug("Testing scenario: {Description}", description);

        var generator = new JsonFragmentToStructuredUpdateGenerator(toolName);
        var allUpdates = new List<JsonFragmentUpdate>();

        // Process all fragments
        foreach (var fragment in fragments)
        {
            var updates = generator.AddFragment(fragment).ToList();
            allUpdates.AddRange(updates);

            TestContextLogger.LogDebug(
                "Fragment processed. Fragment: {Fragment}, UpdateCount: {UpdateCount}",
                fragment,
                updates.Count
            );
            foreach (var update in updates)
            {
                TestContextLogger.LogDebug("Update emitted. Kind: {Kind}, Path: {Path}, Value: {Value}", update.Kind, update.Path, update.TextValue);
            }
        }

        // Verify that JSON is complete
        Assert.True(generator.IsComplete, "Generator should report JSON as complete");

        // Verify that we got exactly one JsonComplete event
        var completionEvents = allUpdates.Where(u => u.Kind == JsonFragmentKind.JsonComplete).ToList();
        _ = Assert.Single(completionEvents);

        var completionEvent = completionEvents.First();
        Assert.Equal("root", completionEvent.Path);
        Assert.NotNull(completionEvent.TextValue);
        Assert.True(completionEvent.TextValue!.Length > 0);

        TestContextLogger.LogDebug("JsonComplete emitted. Json: {Json}", completionEvent.TextValue);
    }

    #region Helper Methods

    /// <summary>
    ///     Asserts that exactly one string update of the specified kind exists with the expected value
    /// </summary>
    private static void AssertSingleStringUpdate(
        List<JsonFragmentUpdate> updates,
        JsonFragmentKind kind,
        string expectedValue
    )
    {
        var stringUpdate = Assert.Single(updates, u => u.Kind == kind);
        Assert.Equal(expectedValue, stringUpdate.TextValue);
    }

    /// <summary>
    ///     Asserts that an update with the specified kind, path and value exists
    /// </summary>
    private void AssertHasUpdate(
        List<JsonFragmentUpdate> updates,
        JsonFragmentKind kind,
        string expectedPath,
        string? expectedValue = null
    )
    {
        var matches = updates
            .Where(u => u.Kind == kind && (expectedValue == null || u.TextValue == expectedValue))
            .ToList();

        output.WriteLine($"\nTesting {kind} with expected path '{expectedPath}':");
        foreach (var match in matches)
        {
            output.WriteLine($"  Found: Path='{match.Path}' Value='{match.TextValue}'");
        }

        Assert.Contains(
            updates,
            u => u.Kind == kind && u.Path == expectedPath && (expectedValue == null || u.TextValue == expectedValue)
        );
    }

    /// <summary>
    ///     Prints a list of updates with an optional header
    /// </summary>
    private void PrintUpdates(List<JsonFragmentUpdate> updates, string? header = null)
    {
        if (header != null)
        {
            output.WriteLine($"{header}:");
        }
        else
        {
            output.WriteLine("All Updates in sequence:");
        }

        foreach (var update in updates)
        {
            output.WriteLine($"{update.Kind,-15}: Path='{update.Path,-30}' Value='{update.TextValue ?? "null"}'");
        }
    }

    #endregion
}
