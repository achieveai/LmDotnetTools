import type { Message } from '@/types';
import { logger } from '@/utils';
import {
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
  onError: (error: string) => void;
  /**
   * Fired when the focus socket closes for any reason (clean or not). The server closes the socket
   * (NormalClosure) after a backpressure drop expecting the client to reconnect + replay; a clean
   * close fires neither `onDone` nor `onError`, so callers rely on this to avoid a frozen view.
   */
  onClose?: (info: { wasClean: boolean; code: number; reason: string }) => void;
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
  callbacks: SubAgentWsCallbacks
): Promise<WebSocketConnection> {
  const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
  const wsHost = window.location.host;
  const wsUrl =
    `${wsProtocol}//${wsHost}/ws/subagent` +
    `?parentThreadId=${encodeURIComponent(parentThreadId)}&agentId=${encodeURIComponent(agentId)}`;

  // The connection's threadId is the child's thread id (matches the SubAgentSummary.threadId
  // convention) so callers can correlate it with rehydrated history.
  const childThreadId = `subagent-${agentId}`;
  const connectionId = generateConnectionId();

  log.info('Connecting to sub-agent stream', { parentThreadId, agentId, childThreadId });

  return openWebSocketConnection(wsUrl, childThreadId, connectionId, callbacks);
}
