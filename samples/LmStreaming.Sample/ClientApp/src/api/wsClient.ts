import type { Message } from '@/types';
import type { ChatRequest } from './chatClient';
import { logger } from '@/utils';

const log = logger.forComponent('WebSocketClient');

/**
 * Callbacks for WebSocket stream events
 */
export interface WebSocketClientCallbacks {
  onMessage: (message: Message) => void;
  onDone: () => void;
  onError: (error: string) => void;
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
 * Server emits PascalCase JSON (System.Text.Json default policy) but TS Message types
 * are camelCase. Normalize at the deserialize boundary so downstream handlers don't
 * need per-field dual-casing reads. Recurses into nested objects/arrays. Preserves the
 * original PascalCase keys alongside the camelCase aliases (write-once, never overwrite)
 * so any handler that already reads PascalCase still works during migration.
 */
function normalizeKeys(value: unknown): unknown {
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
  }
  return out;
}

/**
 * Create a WebSocket connection for chat streaming.
 * The WebSocket sends raw JSON messages (not SSE format).
 */
export function createWebSocketConnection(
  options: WebSocketClientOptions
): Promise<WebSocketConnection> {
  const { baseUrl = '', threadId, modeId, providerId, record, onMessage, onDone, onError } = options;

  return new Promise((resolve, reject) => {
    const connectionId = generateConnectionId();
    const effectiveThreadId = threadId || generateThreadId();
    const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsHost = baseUrl || window.location.host;
    let wsUrl = `${wsProtocol}//${wsHost}/ws?threadId=${effectiveThreadId}&connectionId=${connectionId}`;
    if (modeId) {
      wsUrl += `&modeId=${encodeURIComponent(modeId)}`;
    }
    if (providerId) {
      wsUrl += `&providerId=${encodeURIComponent(providerId)}`;
    }
    if (record) {
      wsUrl += '&record=1';
    }

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
          onError(errorData.message || 'Unknown error');
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
        // Surface parse/normalize failures via onError so the UI shows a banner
        // instead of silently hanging — same pattern as the other error paths in
        // this connection (error signal, socket error). Without this, a bug in
        // normalizeKeys or malformed server JSON would drop the message invisibly.
        const msg = err instanceof Error ? err.message : 'Failed to parse server message';
        log.error('Failed to parse WebSocket message', { error: err, data: event.data });
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
    };
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
 * Generate a unique connection ID
 */
function generateConnectionId(): string {
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
