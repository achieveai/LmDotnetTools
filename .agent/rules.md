# LmDotnetTools Agent Rules

## Build Commands
- **Build Solution**: `dotnet build LmDotnetTools.sln`
- **Build with Log**: `dotnet build LmDotnetTools.sln -bl:.logs/build.binlog`
- **Run All Tests**: `dotnet test LmDotnetTools.sln`
- **Run Specific Test Project**: `dotnet test tests/LmCore.Tests/LmCore.Tests.csproj`
- **Test with TRX Output**: `dotnet test LmDotnetTools.sln --logger "trx;LogFileName=results.trx" --results-directory .logs/test-results`

## Coding Conventions
- **Formatting**: Use CSharpier. Configuration is in `.csharpierrc.json`.
- **Style**: Follow `.editorconfig` rules.
- **Fixing Errors**: Do NOT use scripts to fix build errors/warnings. Use autofixers or manually fix them.
- **Playwright**: ALWAYS use the `Task` tool when using Playwright to avoid polluting the primary context.

## Logging Philosophy
- **Structured Logging**: Use structured properties, NOT string interpolation.
    - ✅ Good: `Logger.LogDebug("Processing item {ItemId}", itemId);`
    - ❌ Bad: `Logger.LogDebug($"Processing item {itemId}");`
- **Boundaries**: Log at entry/exit of methods and before/after external calls.
- **Complex Objects**: Use `LogData("key", object)` for complex structures.
- **Tracing**: Use `Trace()` for detailed flow analysis.
- **Test Context**: Production code logs automatically include `TestClass` and `TestMethod` when running tests.

## Test Logging Queries (DuckDB)
Logs are stored in `.logs/tests/tests.jsonl`.
- **Key Fields**: `TestClass`, `TestMethod`, `TestRunId`, `@t` (Time), `@l` (Level), `@mt` (Message), `SourceContext`.
- **Example Query**:
  ```sql
  SELECT "@t", "@l", SourceContext, "@mt"
  FROM read_json('/Users/gautambhakar/Sources/LmDotnetTools/.logs/tests/tests.jsonl')
  WHERE TestClass = 'MyTestClass'
  ORDER BY "@t";
  ```

## Important Directories
- `src/`: Core source code.
- `tests/`: Unit and integration tests.
- `samples/`: Example applications.
- `McpServers/`: MCP server implementations.
- `.logs/`: Build and test logs (git-ignored).
