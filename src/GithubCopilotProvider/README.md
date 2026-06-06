# GithubCopilotProvider — route Anthropic & OpenAI agents through GitHub Copilot

This package lets you drive the existing `AnthropicProvider` and `OpenAiResponsesProvider`
agents against the **GitHub Copilot** backend instead of the vendors' public APIs. It owns the
Copilot-specific concerns — OAuth token acquisition, the `copilot-*` request headers, and the
SSE/WebSocket transports — and reuses the provider agents and their event→message mapping
unchanged. Only the HTTP/WebSocket transport differs.

Because of that role it is the one project that references **both** sibling providers
(`AnthropicProvider` and `OpenAiResponsesProvider`); the dependency direction is intentional and
one-way — the providers know nothing about Copilot.

## Components

### Auth (`Auth/`)
- **`ICopilotTokenProvider`** — supplies the GitHub OAuth bearer token used for each request.
- **`CliCredentialCopilotTokenProvider`** — reads an existing token from the GitHub CLI / Copilot
  credential files on disk.
- **`DeviceFlowCopilotTokenProvider`** — performs the GitHub OAuth device flow to obtain a token.
- **`CompositeCopilotTokenProvider`** — tries several providers in order (e.g. CLI first, device
  flow as fallback).
- **`CopilotOptions`** / **`CopilotSessionContext`** — header options (base URL, extra headers) and
  the stable machine/session tracking ids sent with each call.
- **`CopilotHttpClientFactory`** — builds an `HttpClient` wired with the headers handler.

### Http (`Http/`)
- **`CopilotHeadersHandler`** — `DelegatingHandler` that attaches the bearer token and the Copilot
  headers (`copilot-integration-id`, `editor-version`, session/interaction ids, …) to every request
  without overwriting headers the caller already set.

### Agents (`Agents/`)
- **`CopilotAnthropicAgentFactory.Create(...)`** — builds an `AnthropicAgent` that talks the
  Anthropic Messages API (`/v1/messages`) through Copilot.
- **`CopilotResponsesAgentFactory.Create(...)`** — builds an `OpenAiResponsesAgent` that talks the
  OpenAI Responses API (`/responses`) through Copilot over **SSE** or **WebSocket**
  (`CopilotResponsesTransport`).

## Usage

```csharp
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;

// 1. Get a Copilot token (CLI credential first, device flow as fallback).
ICopilotTokenProvider tokens = new CompositeCopilotTokenProvider(
    new CliCredentialCopilotTokenProvider(),
    new DeviceFlowCopilotTokenProvider(/* clientId, httpClient, ... */));

// 2a. Anthropic Messages API through Copilot.
var anthropic = CopilotAnthropicAgentFactory.Create("copilot-anthropic", tokens);
var reply = await anthropic.GenerateReplyAsync(
    [new TextMessage { Role = Role.User, Text = "Reply with the single word: READY" }],
    new GenerateReplyOptions { ModelId = "claude-sonnet-4", MaxToken = 64 });

// 2b. OpenAI Responses API through Copilot (WebSocket transport keeps server-side state per turn).
using var openai = CopilotResponsesAgentFactory.Create(
    "copilot-responses", tokens, CopilotResponsesTransport.WebSocket);
var response = await openai.GenerateReplyAsync(
    [new TextMessage { Role = Role.User, Text = "Reply with the single word: READY" }],
    new GenerateReplyOptions { ModelId = "gpt-4.1", MaxToken = 64 });
```

A shared `CopilotSessionContext` and `CopilotOptions` can be passed to both factories so multiple
agents report the same machine/session ids and header configuration.
