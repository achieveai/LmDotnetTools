# LmStreaming.Sample

## Session Recording

In development, open the app with `?record=1` (or `?record=true`) to enable server-side recording for that WebSocket session:

- WebSocket stream messages are written as JSONL to:
  - `samples/LmStreaming.Sample/recordings/<threadId>_<timestamp>.ws.jsonl`
- LLM provider request/response dumps are written to:
  - `samples/LmStreaming.Sample/recordings/<threadId>_<timestamp>.llm.request.txt`
  - `samples/LmStreaming.Sample/recordings/<threadId>_<timestamp>.llm.response.txt`

This works for multi-turn runs and records provider calls for whichever provider mode is active (`openai`, `anthropic`, `test-anthropic`).
