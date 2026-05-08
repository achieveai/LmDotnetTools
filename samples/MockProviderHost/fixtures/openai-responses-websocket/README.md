# OpenAI Responses WebSocket capture fixtures

These fixtures are sanitized WebSocket captures from Codex talking to an
OpenAI Responses API server. They are intended as protocol reference material
for implementing `MockProviderHost` WebSocket support for `/v1/responses`.

Only secrets, IDs, and metadata were redacted. Tool names, tool descriptions,
parameter schemas, event types, and other protocol values are preserved so the
object shapes remain useful for implementation.

Files:

- `sample_ws_stream_001.redacted.json`
- `sample_ws_stream_002.redacted.json`

The raw captures should not be committed. See issue #34 for the work item and
the discussion that motivated these fixtures.
