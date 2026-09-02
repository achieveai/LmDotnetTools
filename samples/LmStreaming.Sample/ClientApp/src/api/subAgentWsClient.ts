import type { Message } from '@/types';
import { logger } from '@/utils';
import {
  type GenerationAbandonedInfo,
  type WebSocketConnection,
  generateConnectionId,
  openWebSocketConnection,
} from './wsClient';

const log = logger.forComponent('SubAgentWsClient');

/**
 * Callbacks for a sub-agent stream. A subset of the chat callbacks — a focused child transcript has
 * no out-of-band deferred-auth prompts, so there is no `onAuthEvent`.
 */
export interface SubAgentWsCallbacks {
  onMessage: (message: Message) => void;
  onDone: () => void;
  /**
   * Surface a stream failure. `code` carries the structured discriminator from a server error frame
   * (e.g. `subagent_unavailable`, `subagent_stream_failed`, `relay_failed`) when present, letting the
   * panel treat a terminal application error differently from a transient/parse failure. Forwarded
   * verbatim from the shared {@link openWebSocketConnection} handler; backward-compatible with
   * `(error) => void` callers.
   */
  onError: (error: string, code?: string) => void;
  /**
   * Fired when the focus socket closes for any reason (clean or not). The server closes the socket
   * (NormalClosure) after a backpressure drop expecting the client to reconnect + replay; a clean
   * close fires neither `onDone` nor `onError`, so callers rely on this to avoid a frozen view.
   */
  onClose?: (info: { wasClean: boolean; code: number; reason: string }) => void;
  /**
   * Ack for a `client_tool_result` frame the browser sent over THIS sub-agent connection to resolve a
   * deferred client tool on the focused descendant (#246, e.g. a descendant's `AskUserQuestion`).
   * Mirrors {@link WebSocketClientCallbacks.onClientToolResultAck} — see `useSubAgentPanel.ts`'s
   * `submitToFocusedChild` for the resolver this settles.
   */
  onClientToolResultAck?: (toolCallId: string, duplicate: boolean) => void;
  /**
   * The server rejected a `client_tool_result` submission sent over THIS sub-agent connection
   * (#246). Mirrors {@link WebSocketClientCallbacks.onClientToolResultError}.
   */
  onClientToolResultError?: (toolCallId: string | undefined, code: string, message: string) => void;
  /**
   * The child's provider stream was cut mid-reply, the loop threw that generation away, and the SAME
   * turn is being retried under a fresh generation id on this still-open socket (#278). Mirrors
   * {@link WebSocketClientCallbacks.onGenerationAbandoned} — without it the focused transcript keeps
   * the abandoned generation's half-written block and renders the retry beside it.
   */
  onGenerationAbandoned?: (info: GenerationAbandonedInfo) => void;
}

/**
 * Open a WebSocket onto a conversation's focused sub-agent
 * (`/ws/subagent?parentThreadId=..&agentId=..`). The server streams the child agent's
 * `SubscribeAsync` output (same message shapes / done sentinel / structured errors as `/ws`) and
 * relays inbound `{Message:text}` frames to the child. Reuses the shared socket wiring
 * ({@link openWebSocketConnection}) so the normalize/done/error handling is identical to the parent
 * chat; only the URL differs. The returned connection is driven with the standard
 * `sendWebSocketMessage` / `closeWebSocketConnection` helpers.
 */
export function connectSubAgent(
  parentThreadId: string,
  agentId: string,
  childThreadId: string,
  callbacks: SubAgentWsCallbacks
): Promise<WebSocketConnection> {
  const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
  const wsHost = window.location.host;
  const wsUrl =
    `${wsProtocol}//${wsHost}/ws/subagent` +
    `?parentThreadId=${encodeURIComponent(parentThreadId)}&agentId=${encodeURIComponent(agentId)}`;

  // The connection's threadId is the child's thread id, taken from the roster (SubAgentSummary.threadId)
  // rather than composed here: since #705 it is `subagent-{scope}-{agentId}`, scoped to the ROOT
  // conversation, and only the server knows the scope. It has to equal what the server stamps on the
  // frames so callers can correlate the stream with rehydrated history.
  const connectionId = generateConnectionId();

  log.info('Connecting to sub-agent stream', { parentThreadId, agentId, childThreadId });

  return openWebSocketConnection(wsUrl, childThreadId, connectionId, callbacks);
}
