# SSE Handler Migration Status

## Completed in this change set

- Added non-stream OpenAI response support to:
  - `src/LmTestUtils/TestMode/TestSseMessageHandler.cs`
- Added reusable test harness and wrappers:
  - `src/LmTestUtils/TestMode/TestModeHttpClientFactory.cs`
  - Includes:
    - `CreateOpenAiTestClient(...)`
    - `CreateAnthropicTestClient(...)`
    - request-capture delegating wrapper
    - status-sequence delegating wrapper for retry/error scenarios
- Added unit coverage for new harness behavior:
  - `tests/LmTestUtils.Tests/TestMode/TestModeHttpClientFactoryTests.cs`
- Migrated provider HTTP-client tests to SSE-handler stack:
  - `tests/OpenAIProvider.Tests/Agents/OpenClientHttpTests.cs`
  - `tests/AnthropicProvider.Tests/Agents/AnthropicClientHttpTests.cs`
- Migrated Anthropic agent behavior tests:
  - `tests/AnthropicProvider.Tests/Agents/BasicConversationTests.cs`
  - `tests/AnthropicProvider.Tests/Agents/FunctionToolTests.cs`
  - `tests/AnthropicProvider.Tests/Agents/ThinkingModeTests.cs`
- Migrated OpenAI agent/data-driven playback paths to deterministic SSE-handler flow:
  - `tests/OpenAIProvider.Tests/Agents/OpenAiAgent.Tests.cs`
  - `tests/OpenAIProvider.Tests/Agents/DataDrivenFunctionToolTests.cs`
  - `tests/OpenAIProvider.Tests/Agents/DataDrivenReasoningTests.cs`
  - `tests/OpenAIProvider.Tests/Agents/DataDrivenReasoningStreamingTests.cs`
  - `tests/OpenAIProvider.Tests/Agents/DataDrivenMultiTurnReasoningTests.cs`
- Migrated Anthropic data-driven playback path to deterministic SSE-handler flow:
  - `tests/AnthropicProvider.Tests/Agents/DataDrivenFunctionToolTests.cs` (`FunctionTool_RequestAndResponseTransformation`)
- Hardened creator facts as manual-only:
  - All data-creation tests in the above OpenAI files
  - `CreateWeatherFunctionToolTestData` / `CreateMultiFunctionToolTestData` in
    `tests/AnthropicProvider.Tests/Agents/DataDrivenFunctionToolTests.cs`

## Remaining MockHttpHandlerBuilder usage in provider agent folders

### Intentional (record/playback coverage retained)

- `tests/AnthropicProvider.Tests/Agents/AnthropicClientWrapper.Tests.cs`

### Artifact-creation paths (manual-only)

- `tests/OpenAIProvider.Tests/Agents/DataDrivenFunctionToolTests.cs` (creator facts)
- `tests/OpenAIProvider.Tests/Agents/DataDrivenReasoningTests.cs` (creator facts)
- `tests/OpenAIProvider.Tests/Agents/DataDrivenReasoningStreamingTests.cs` (creator facts)
- `tests/OpenAIProvider.Tests/Agents/DataDrivenMultiTurnReasoningTests.cs` (creator facts)
- `tests/AnthropicProvider.Tests/Agents/DataDrivenFunctionToolTests.cs` (creator facts)

## Phase 4 Audit Result

- Provider agent test folders audited for `MockHttpHandlerBuilder` / `FakeHttpMessageHandler`.
- No remaining mock-handler usage in normal SSE playback/validation paths.
- Remaining usage is intentionally scoped to:
  - record/playback coverage test: `tests/AnthropicProvider.Tests/Agents/AnthropicClientWrapper.Tests.cs`
  - manual artifact-creation facts listed above.
- Migration backlog in provider agent folders: none.

## Notes

- Existing cassette files were intentionally kept.
- Record/playback-focused tests remain intact by policy.
- Test logging now defaults to maximum app/test verbosity (`Trace`/Serilog `Verbose`) for all test runs,
  while `Microsoft` and `System` categories remain overridden to `Warning` to keep framework noise bounded.
