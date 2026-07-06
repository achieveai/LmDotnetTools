# ConversationDaemon.Sample

A standalone console app that drives an **already-running** [`LmStreaming.Sample`](../LmStreaming.Sample)
server purely over its headless REST API (added in PRs #138/#149). It provisions a conversation,
runs a few scripted mock-provider prompts, then prints a browser deep-link so a human can open the
**same** conversation live in the web UI and take over.

It takes **no** project reference to `LmStreaming.Sample` — it is a pure HTTP client built on BCL
`HttpClient` + `System.Text.Json` and talks only to the `/api/conversations` surface.

> **Local-use only.** The REST/WebSocket surface this sample uses is **unauthenticated** and intended
> for local development and demos. It is not a hardening template — do not expose the server or this
> daemon to an untrusted network.

## What it demonstrates

1. **Provision + metadata** — reserves a conversation thread (server mints the id) and locks its
   workspace/provider/mode, then sets a title/preview so it is identifiable in the web UI sidebar.
2. **Deep-link handoff** — prints `{baseUrl}/?threadId={threadId}`; opening it drops a human straight
   into the live conversation to watch or take over.
3. **Scripted turns** driven by the mock provider (`test-anthropic`):
   - a warm-up `calculate` tool call;
   - **sub-agent delegation** — a nested instruction chain where the parent calls the `Agent` tool,
     the sub-agent runs a tool and replies, and the parent wraps up;
   - **Wait / park-and-wake** — the run parks on a 3s timer (the daemon confirms the parked
     `is_deferred=true` state by polling the messages endpoint), then auto-resumes when the timer
     fires (the daemon confirms via run-state and status polling).

Between steps the daemon always waits for the prior run to reach idle before sending the next message.

## Prerequisites

- .NET SDK 9.0+ (see `Directory.Build.props`).
- A running `LmStreaming.Sample` instance. No API keys are needed — the scripted prompts use the
  built-in **mock** provider (`test-anthropic`), so nothing calls a real LLM.

## Running

### 1. Start `LmStreaming.Sample`

```bash
dotnet run --project samples/LmStreaming.Sample
```

By default it listens on `http://localhost:5000`. If your instance logs a different URL, note it and
pass it to the daemon in the next step.

### 2. Run the daemon

```bash
# Uses the default base URL (http://localhost:5000)
dotnet run --project samples/ConversationDaemon.Sample

# Or point it at a specific server URL (whatever the server logged)
dotnet run --project samples/ConversationDaemon.Sample -- --base-url http://localhost:5000
```

The base URL is resolved in this order: `--base-url <url>` → first positional argument →
`LMSTREAMING_BASE_URL` environment variable → `http://localhost:5000`.

If the server is not running, the daemon prints a single actionable line (how to start it) and exits
with a non-zero code — no stack trace.

### 3. Open the printed link

The daemon prints a line like:

```
Open this link in a browser to watch or take over the conversation live:
  http://localhost:5000/?threadId=thread-xxxxxxxx
```

Open it to watch the scripted turns arrive and then continue the conversation yourself.
