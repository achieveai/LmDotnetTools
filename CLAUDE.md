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

### Analyzing Logs with DuckDB
Use the DuckDB MCP server to query test results:

```sql
-- Query TRX test results (XML format)
SELECT * FROM read_csv('.logs/test-results/*.trx');

-- For detailed analysis, parse XML columns
-- Or export build output to CSV first, then query
```
