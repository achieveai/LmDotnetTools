---
applyTo: '**/*.cs'
---

# VSCode Self-Review Implementation Guide

## Enabling Autonomous Review in Cursor

To implement a self-review process in Cursor AI where it evaluates its work before completing tasks, you'll need to set up both the rules file and configure Cursor's Agent Mode correctly.

### 1. Setup YOLO Mode for Autonomous Actions

Cursor's "YOLO Mode" allows the AI to take autonomous actions like running tests and builds, which is essential for self-review. To enable it:

1. Open Cursor Settings
2. Scroll down to find YOLO Mode settings
3. Enable YOLO Mode
4. Add the following allowed commands to the Allow List:
   ```
   dotnet build
   dotnet test
   tsc
   npm test
   mkdir
   ```

### 2. Configure Diagnostic Output Handling

Ensure Cursor can capture diagnostic output by adding these to your project's test configuration:

#### For .NET Projects:
Add to your test project file (`.csproj`):
```xml
<PropertyGroup>
  <VSTestLogger>trx</VSTestLogger>
  <VSTestResultsDirectory>$(MSBuildProjectDirectory)/TestResults</VSTestResultsDirectory>
</PropertyGroup>
```

#### For Node.js Projects:
Update your package.json test script to include diagnostic output:
```json
"scripts": {
  "test": "jest --verbose"
}
```

### 3. Create Special Review Commands

Set up custom commands in Cursor to trigger the review process:

1. Open Command Palette (Cmd+Shift+P / Ctrl+Shift+P)
2. Type "Cursor: Create New Command"
3. Name it "Review Current Code"
4. Add this prompt:
   ```
   Perform a comprehensive review of the current file focusing on:
   1. Design pattern identification and correctness
   2. Code duplication detection
   3. Async code validation (no blocking calls)
   4. Build verification with dotnet build
   5. Test verification with dotnet test
   
   If any issues are found, fix them and explain the changes.
   ```

### 4. Set Up Project Structure for Data-Driven Testing

Create standard test utilities for data-driven testing:

1. Create a `TestUtilities` project/directory
2. Add reusable test data generators
3. Create a consistent approach for parameterized tests

### Example Template for Data-Driven Test:

```csharp
// Example C# data-driven test template
[Theory]
[MemberData(nameof(TestCases))]
public async Task OperationName_Scenario_ExpectedResult(
    // Input parameters
    string inputParam1,
    int inputParam2,
    // Expected output
    string expectedResult)
{
    // Arrange
    var sut = new SystemUnderTest();
    System.Diagnostics.Debug.WriteLine($"Testing with: {inputParam1}, {inputParam2}");
    
    // Act
    var result = await sut.OperationAsync(inputParam1, inputParam2);
    System.Diagnostics.Debug.WriteLine($"Result: {result}");
    
    // Assert
    Assert.Equal(expectedResult, result);
}

public static IEnumerable<object[]> TestCases => new List<object[]>
{
    // Format: inputParam1, inputParam2, expectedResult
    new object[] { "valid input", 42, "expected output" },
    new object[] { "edge case", 0, "special result" },
    new object[] { "error case", -1, "error message" }
};
```

### 5. Framework-Specific Research Commands

Add custom commands for framework research:

1. Create a command named "Research Framework"
2. Use this prompt:
   ```
   Search for best practices and up-to-date documentation for [FRAMEWORK_NAME].
   Focus on:
   1. Modern approaches
   2. Potential pitfalls
   3. Performance considerations
   4. Community-recommended patterns
   ```

### 6. Setup Workflow for New Tasks

When starting a new task, establish this workflow:

1. Define the requirements clearly
2. Research any unfamiliar frameworks (use the Research command)
3. Plan the approach, identifying appropriate design patterns
4. Implement with consistent async patterns
5. Create data-driven tests with diagnostic output
6. Run the self-review process before finalizing
7. Address any issues identified during review
