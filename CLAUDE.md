# LmDotnetTools Project Context

## Overview
LmDotnetTools is a comprehensive .NET SDK for working with large language models (LLMs) from multiple providers including OpenAI, Anthropic, and OpenRouter. It provides a unified interface with streaming support, middleware pipeline, and type-safe models.

## Build Commands
```bash
# Build the solution
dotnet build LmDotnetTools.sln

# Run all tests
dotnet test LmDotnetTools.sln

# Run specific test project
dotnet test tests/LmCore.Tests/LmCore.Tests.csproj
```

## Key Directories
- `src/` - Core source code (LmCore, OpenAIProvider, AnthropicProvider, etc.)
- `tests/` - Unit and integration tests
- `samples/` - Example applications
- `docs/` - Additional documentation
- `McpServers/` - MCP server implementations

## Coding Conventions
- Use CSharpier for code formatting (`.csharpierrc.json`)
- Follow `.editorconfig` rules for style guidelines
- Do not use scripts to fix build errors/warnings - use autofixers or manually fix
- When using Playwright, ALWAYS use `Task` tool to avoid polluting primary context

## Environment Requirements
- **.NET SDK**: 8.0+ (check `Directory.Build.props` for exact version)
- **Optional Environment Variables**:
  - `OPENROUTER_API_KEY` - For OpenRouter provider
  - `ENABLE_USAGE_MIDDLEWARE` - Enable usage tracking (default: true)

## Testing
- Mock utilities available in `src/LmTestUtils/`
- See `src/LmTestUtils/README-SSE.md` for SSE testing docs

## Important Files
- `Directory.Build.props` - Central build configuration
- `FORMATTING.md` - Detailed formatting guidelines
- `CHANGELOG.md` - Version history

## Build & Test Logging
Logs are stored in `.logs/` directory (git-ignored).

```bash
# Build with binary log
dotnet build LmDotnetTools.sln -bl:.logs/build.binlog

# Test with TRX output
dotnet test LmDotnetTools.sln --logger "trx;LogFileName=results.trx" --results-directory .logs/test-results
```

### Structured Test Logs
All tests output structured JSON logs to `.logs/tests/tests.jsonl`. Each log line includes:
- `TestClass` - The test class name
- `TestMethod` - The test method name
- `TestRunId` - Unique identifier for the test run (timestamp-based)
- Standard Serilog fields (`@t`, `@m`, `@l`, `SourceContext`, etc.)

Previous test logs are automatically archived to `.logs/tests/tests-{timestamp}.jsonl.gz` and old archives (>7 days) are deleted.

### Using LoggingTestBase
Inherit from `LoggingTestBase` to get automatic test correlation in logs:

```csharp
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;

public class MyTests : LoggingTestBase
{
    public MyTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void MyTest()
    {
        Logger.LogInformation("Starting test with value {Value}", 42);
        
        // Production code logs include TestClass/TestMethod automatically
        var service = new MyService(LoggerFactory.CreateLogger<MyService>());
        service.DoWork(); // Logs from here will include test context
        
        LogData("result", myResult); // Log structured data for debugging
    }
}
```

### Querying Logs with DuckDB
Use DuckDB CLI or the DuckDB MCP server to query structured test logs:

```sql
-- NOTE: When using in-memory DuckDB server, use ABSOLUTE paths to log files
-- Find all logs for a specific test
SELECT "@t" as Time, "@l" as Level, SourceContext, "@mt" as Message
FROM read_json('/Users/gautambhakar/Sources/LmDotnetTools/.logs/tests/tests.jsonl')
WHERE TestClass = 'FunctionRegistryTests'
  AND TestMethod = 'Build_WithExplicitFunction'
ORDER BY "@t";

-- Find errors across all tests in a run
SELECT TestClass, TestMethod, "@mt" as Message, Exception
FROM read_json('/Users/gautambhakar/Sources/LmDotnetTools/.logs/tests/tests.jsonl')
WHERE "@l" = 'Error'
  AND TestRunId = '2026-02-01_12-00-00';

-- Trace control flow through production code
SELECT "@t" as Time, SourceContext, "@mt" as Message
FROM read_json('/Users/gautambhakar/Sources/LmDotnetTools/.logs/tests/tests.jsonl')
WHERE TestMethod = 'MyFailingTest'
ORDER BY "@t";

-- Count logs per test
SELECT TestClass, TestMethod, COUNT(*) as LogCount
FROM read_json('/Users/gautambhakar/Sources/LmDotnetTools/.logs/tests/tests.jsonl')
GROUP BY TestClass, TestMethod
ORDER BY LogCount DESC;
```

### Test Logging Philosophy

**Key Principles:**

1. **Use structured properties, not string interpolation**
   ```csharp
   // ✅ Good - queryable properties
   Logger.LogDebug("Processing item {ItemId} with status {Status}", itemId, status);
   
   // ❌ Bad - not queryable
   Logger.LogDebug($"Processing item {itemId} with status {status}");
   ```

2. **Log at boundaries** - Entry/exit of methods, before/after external calls

3. **Use `LogData()` for complex objects** - Automatically destructures objects:
   ```csharp
   LogData("request", chatRequest);
   LogData("response", apiResponse);
   ```

4. **Trace control flow** - Use `Trace()` for detailed flow analysis:
   ```csharp
   Trace("Entering middleware {MiddlewareName}", GetType().Name);
   Trace("Stream chunk received: {ChunkLength} bytes", chunk.Length);
   ```

5. **Correlate with test context** - All production code logs automatically include `TestClass` and `TestMethod`, enabling:
   - Filter logs for a failing test
   - Trace exactly what production code did during a specific test
   - Debug intermittent failures by comparing log patterns

