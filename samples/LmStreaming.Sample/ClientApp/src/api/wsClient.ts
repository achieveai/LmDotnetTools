import type { Message, AuthEvent } from '@/types';
import { isAuthEventPayload } from '@/types';
import type { ChatRequest } from './chatClient';
import { logger } from '@/utils';

const log = logger.forComponent('WebSocketClient');

/**
 * Callbacks for WebSocket stream events
 */
export interface WebSocketClientCallbacks {
  onMessage: (message: Message) => void;
  onDone: () => void;
  /**
   * Surface a stream failure to the caller. `code` carries the structured discriminator from a
   * server error frame (e.g. `subagent_unavailable`, `subagent_stream_failed`, `relay_failed`) when
   * one is present, so callers can distinguish a terminal application error from a transient/parse
   * failure. Optional and backward-compatible — existing `(error) => void` callers still bind.
   */
  onError: (error: string, code?: string) => void;
  /**
   * Out-of-band deferred-auth events (`auth_required` / `auth_completed`) pushed by the
   * backend while a sandbox webhook call is held awaiting an interactive sign-in.
   */
  onAuthEvent?: (event: AuthEvent) => void;
  /**
   * Fired when the socket closes for ANY reason (clean or not) — after the `!wasClean` error
   * surfacing below. Lets a caller react to a server-initiated NormalClosure (which fires neither
   * `onDone` nor `onError`), e.g. the sub-agent focus view resuming after a backpressure drop.
   */
  onClose?: (info: { wasClean: boolean; code: number; reason: string }) => void;
}

/**
 * WebSocket client options
 */
export interface WebSocketClientOptions extends WebSocketClientCallbacks {
  baseUrl?: string;
  threadId?: string;
  modeId?: string;
  /**
   * Provider id requested for this connection. Honored only when the thread has not
   * yet locked in a provider; persisted threads keep their original provider regardless.
   */
  providerId?: string | null;
  /**
   * Workspace id requested for this connection. Honored only when the thread has
   * not yet locked in a workspace; persisted threads keep their original workspace.
   */
  workspaceId?: string | null;
  record?: boolean;
}

/**
 * WebSocket connection state
 */
export interface WebSocketConnection {
  socket: WebSocket;
  connectionId: string;
  threadId: string;
  isConnected: boolean;
}

/**
 * Identity fields that arrive in snake_case on some wire shapes (tool-call JSON) but are read in
 * camelCase by the client merge key (`kind-runId-generationId-messageOrderIdx`). Without a
 * snake_case → camelCase alias these messages fall back to 'default' and fail to group with their
 * camelCase siblings. (tool_call_id is consumed directly in snake_case by getMergeKey, so it does
 * not need aliasing here.)
 */
const SNAKE_CASE_IDENTITY_ALIASES: Readonly<Record<string, string>> = {
  generation_id: 'generationId',
  run_id: 'runId',
  parent_run_id: 'parentRunId',
  message_order_idx: 'messageOrderIdx',
};

/**
 * Server emits PascalCase JSON (System.Text.Json default policy) but TS Message types
 * are camelCase. Normalize at the deserialize boundary so downstream handlers don't
 * need per-field dual-casing reads. Recurses into nested objects/arrays. Preserves the
 * original keys alongside the camelCase aliases (write-once, never overwrite) so any handler
 * that already reads the original casing still works during migration. Handles both PascalCase
 * (leading-uppercase) and a known set of snake_case identity fields.
 *
 * Exported for unit testing of the aliasing contract.
 */
export function normalizeKeys(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(normalizeKeys);
  }
  if (value === null || typeof value !== 'object') {
    return value;
  }
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
    const normalized = normalizeKeys(v);
    // Preserve original key (handlers may still read PascalCase during migration).
    out[k] = normalized;
    // Add camelCase alias if the key starts with an uppercase ASCII letter and the
    // alias slot is free. Skip JSON discriminator '$type'.
    if (k.length > 0 && k !== '$type') {
      const first = k.charCodeAt(0);
      if (first >= 65 && first <= 90) {
        const camel = k.charAt(0).toLowerCase() + k.slice(1);
        if (!(camel in out)) {
          out[camel] = normalized;
        }
      }
    }
    // Add camelCase alias for known snake_case identity fields (write-once).
    const snakeAlias = SNAKE_CASE_IDENTITY_ALIASES[k];
    if (snakeAlias && !(snakeAlias in out)) {
      out[snakeAlias] = normalized;
    }
  }
  return out;
}

/**
 * Build the chat WebSocket URL (`/ws?threadId=..&connectionId=..[&modeId=..][&providerId=..]
 * [&workspaceId=..][&record=1]`). Split out so `createWebSocketConnection` delegates URL construction
 * and the socket wiring below is shared with other endpoints (e.g. the sub-agent stream).
 */
function buildChatWebSocketUrl(
  options: WebSocketClientOptions,
  effectiveThreadId: string,
  connectionId: string
): string {
  const { baseUrl = '', modeId, providerId, workspaceId, record } = options;
  const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
  const wsHost = baseUrl || window.location.host;
  let wsUrl = `${wsProtocol}//${wsHost}/ws?threadId=${effectiveThreadId}&connectionId=${connectionId}`;
  if (modeId) {
    wsUrl += `&modeId=${encodeURIComponent(modeId)}`;
  }
  if (providerId) {
    wsUrl += `&providerId=${encodeURIComponent(providerId)}`;
  }
  if (workspaceId) {
    wsUrl += `&workspaceId=${encodeURIComponent(workspaceId)}`;
  }
  if (record) {
    wsUrl += '&record=1';
  }
  return wsUrl;
}

/**
 * Summarize a WebSocket payload that FAILED to parse into content-free diagnostics. This handler is
 * SHARED by the parent chat and the focused sub-agent transcript stream, so the raw payload may carry
 * prompts / reasoning / tool content (EUII). We must therefore log ONLY metadata — never `event.data`
 * or any payload text:
 *   - `byteLength`  — UTF-8 byte size of a string payload (undefined for non-string frames).
 *   - `type`        — the `$type` discriminator IF it can be lifted with a bounded regex. The
 *                     discriminator is a fixed enum token (e.g. `text`, `tool_call`), not content.
 *   - `errorName`   — the exception category (`err.name`), never its message/content.
 * Exported for unit testing of the privacy contract.
 */
export function summarizeParseFailure(data: unknown, err: unknown): {
  byteLength: number | undefined;
  type: string | undefined;
  errorName: string;
} {
  let byteLength: number | undefined;
  let type: string | undefined;
  if (typeof data === 'string') {
    byteLength = new TextEncoder().encode(data).length;
    // Lift ONLY the discriminator token; the capture group is bounded to a quoted enum value and
    // never includes surrounding payload content.
    const match = /"\$type"\s*:\s*"([^"]+)"/.exec(data);
    type = match ? match[1] : undefined;
  }
  const errorName = err instanceof Error ? err.name : typeof err;
  return { byteLength, type, errorName };
}

/**
 * Open a WebSocket at `wsUrl` and wire the standard stream callbacks (auth-event → done → error →
 * normalized message). This is the shared socket handling used by BOTH the chat stream
 * ({@link createWebSocketConnection}) and the sub-agent stream (`connectSubAgent`), so the
 * onmessage normalize/done/error logic lives in ONE place. The returned {@link WebSocketConnection}
 * carries `effectiveThreadId`/`connectionId` for the caller's bookkeeping; this helper does NOT
 * build the URL (callers do, since query strings differ per endpoint).
 *
 * Exported for reuse by sibling WebSocket clients; not part of the public chat API.
 */
export function openWebSocketConnection(
  wsUrl: string,
  effectiveThreadId: string,
  connectionId: string,
  callbacks: WebSocketClientCallbacks
): Promise<WebSocketConnection> {
  const { onMessage, onDone, onError, onAuthEvent, onClose } = callbacks;

  return new Promise((resolve, reject) => {
    log.info('Connecting to WebSocket', { url: wsUrl, connectionId, threadId: effectiveThreadId });

    const socket = new WebSocket(wsUrl);

    socket.onopen = () => {
      log.info('WebSocket connected', { connectionId, threadId: effectiveThreadId });
      resolve({
        socket,
        connectionId,
        threadId: effectiveThreadId,
        isConnected: true,
      });
    };

    socket.onmessage = (event) => {
      try {
        const data = event.data as string;

        // Check for deferred-auth events BEFORE the generic error sniffing below — these
        // frames are out-of-band prompts (sign-in required / completed), not chat messages.
        if (isAuthEventPayload(data)) {
          const authEvent = JSON.parse(data) as AuthEvent;
          log.info('Received auth event', { type: authEvent.$type, providerId: authEvent.providerId });
          onAuthEvent?.(authEvent);
          return;
        }

        // Check for done signal
        if (data === '{"$type":"done"}' || data.includes('"$type":"done"')) {
          log.debug('Received done signal');
          onDone();
          return;
        }

        // Check for error signal
        if (data.includes('"$type":"error"')) {
          const errorData = JSON.parse(data);
          log.error('Received error', { error: errorData });
          onError(
            errorData.message || 'Unknown error',
            typeof errorData.code === 'string' ? errorData.code : undefined
          );
          return;
        }

        // Parse as message and normalize PascalCase keys to camelCase aliases at the
        // deserialize boundary so handlers can read camelCase directly (see F7).
        const raw = JSON.parse(data) as unknown;
        const message = normalizeKeys(raw) as Message;
        if (message.$type) {
          log.trace('Received message', { type: message.$type });
          onMessage(message);
        }
      } catch (err) {
        // Surface parse/normalize failures via onError so the UI shows a banner instead of silently
        // hanging — same pattern as the other error paths in this connection. Log ONLY content-free
        // metadata: this SHARED handler now carries focused sub-agent transcript frames, so logging
        // `event.data` here would leak prompts/reasoning/tool content into client diagnostics (EUII).
        const msg = err instanceof Error ? err.message : 'Failed to parse server message';
        const { byteLength, type, errorName } = summarizeParseFailure(event.data, err);
        log.error('Failed to parse WebSocket message', {
          byteLength,
          type,
          connectionId,
          threadId: effectiveThreadId,
          errorName,
        });
        onError(msg);
      }
    };

    socket.onerror = (event) => {
      log.error('WebSocket error', { event });
      onError('WebSocket connection error');
      reject(new Error('WebSocket connection error'));
    };

    socket.onclose = (event) => {
      log.info('WebSocket closed', { code: event.code, reason: event.reason });
      if (!event.wasClean) {
        onError(`WebSocket closed unexpectedly: ${event.reason || 'Unknown reason'}`);
      }
      onClose?.({ wasClean: event.wasClean, code: event.code, reason: event.reason });
    };
  });
}

/**
 * Create a WebSocket connection for chat streaming.
 * The WebSocket sends raw JSON messages (not SSE format).
 */
export function createWebSocketConnection(
  options: WebSocketClientOptions
): Promise<WebSocketConnection> {
  const { threadId, onMessage, onDone, onError, onAuthEvent } = options;
  const connectionId = generateConnectionId();
  const effectiveThreadId = threadId || generateThreadId();
  const wsUrl = buildChatWebSocketUrl(options, effectiveThreadId, connectionId);
  return openWebSocketConnection(wsUrl, effectiveThreadId, connectionId, {
    onMessage,
    onDone,
    onError,
    onAuthEvent,
  });
}

/**
 * Send a chat message over an existing WebSocket connection.
 * The message is sent as JSON and the server streams responses back.
 */
export function sendWebSocketMessage(
  connection: WebSocketConnection,
  message: string
): void {
  if (!connection.isConnected || connection.socket.readyState !== WebSocket.OPEN) {
    throw new Error('WebSocket is not connected');
  }

  const request: ChatRequest = { Message: message };
  log.info('Sending chat message via WebSocket', { messageLength: message.length });
  connection.socket.send(JSON.stringify(request));
}

/**
 * Close a WebSocket connection.
 */
export function closeWebSocketConnection(connection: WebSocketConnection): void {
  if (connection.socket.readyState === WebSocket.OPEN) {
    log.info('Closing WebSocket connection', { connectionId: connection.connectionId });
    connection.socket.close(1000, 'Client closing');
  }
}

/**
 * Generate a unique connection ID. Exported so sibling WebSocket clients (e.g. the sub-agent
 * stream) reuse the same id scheme instead of re-deriving it.
 */
export function generateConnectionId(): string {
  return `ws-${Date.now()}-${Math.random().toString(36).substring(2, 9)}`;
}

/**
 * Generate a unique thread ID for agent routing
 */
function generateThreadId(): string {
  return `thread-${Date.now()}-${Math.random().toString(36).substring(2, 9)}`;
}

/**
 * Send a chat message using WebSocket (creates connection, sends, waits for done).
 * This is a higher-level API similar to sendChatMessage for SSE.
 */
export async function sendChatMessageWs(
  message: string,
  options: WebSocketClientOptions
): Promise<void> {
  const { onMessage, onDone, onError } = options;

  let connection: WebSocketConnection | null = null;

  try {
    // Create connection with wrapped callbacks
    connection = await createWebSocketConnection({
      ...options,
      onMessage,
      onDone: () => {
        onDone();
        // Close connection after done
        if (connection) {
          closeWebSocketConnection(connection);
        }
      },
      onError: (error) => {
        onError(error);
        // Close connection on error
        if (connection) {
          closeWebSocketConnection(connection);
        }
      },
    });

    // Send the message
    sendWebSocketMessage(connection, message);

  } catch (error) {
    log.error('WebSocket chat failed', { error: error instanceof Error ? error.message : error });
    onError(error instanceof Error ? error.message : 'Unknown error');

    // Cleanup
    if (connection) {
      closeWebSocketConnection(connection);
    }
  }
}
