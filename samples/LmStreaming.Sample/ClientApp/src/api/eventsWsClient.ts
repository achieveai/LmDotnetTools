import { logger } from '@/utils';
import { webSocketSubProtocols } from './wsClient';

const log = logger.forComponent('QuestionEventsWsClient');

/**
 * One `AskUserQuestion` parked somewhere in the app, as pushed by the server's `/ws/events` stream.
 *
 * Mirrors `PendingQuestionHub.Payload` field for field. `rootThreadId` is the conversation a client
 * navigates to in order to answer it — NOT necessarily where it was asked: when a sub-agent asks,
 * `agentId`/`childThreadId` name the descendant and the root still names the conversation.
 */
export interface PendingQuestionEvent {
  rootThreadId: string;
  agentId: string | null;
  childThreadId: string | null;
  toolCallId: string;
  prompt: string;
  conversationTitle: string | null;
  agentName: string | null;
  raisedAtUtc: string;
}

/**
 * What a consumer is told. Deliberately three narrow calls rather than a generic frame callback: the
 * only reason this socket exists is that a parked question is invisible to the conversation list —
 * a conversation's `lastUpdated` moves when a run COMPLETES, and a parked run has not completed.
 */
export interface QuestionEventHandlers {
  /**
   * Everything parked at the moment this connection was accepted, filtered to what the caller may
   * read. Delivered once per successful connect — so ALSO after a reconnect, which is the point:
   * anything raised or settled while the socket was down is reconciled by this frame.
   *
   * Additive by contract. A snapshot is the server's IN-MEMORY state, and an agent whose loop has
   * been evicted from the pool still has a genuinely pending question in its transcript, so a
   * consumer must not read absence from a snapshot as "settled".
   */
  onSnapshot: (questions: PendingQuestionEvent[]) => void;
  /** A question has just parked. May repeat for a call already known (restart recovery re-raises). */
  onPending: (question: PendingQuestionEvent) => void;
  /** A question stopped waiting — answered, cancelled, or unwound. Idempotent. */
  onSettled: (rootThreadId: string, toolCallId: string) => void;
}

export interface QuestionEventStreamOptions {
  /** Overrides the derived `ws(s)://<host>/ws/events`. */
  url?: string;
  /**
   * Backoff schedule between reconnect attempts; the last entry repeats forever. A failed connect
   * must not become a hot loop against a server that is down, and the stream must not give up
   * either — it is the only thing that makes a question raised elsewhere visible promptly.
   */
  retryDelaysMs?: number[];
  /** Socket factory seam for tests. */
  openSocket?: (url: string, protocols: string[] | undefined) => WebSocket;
}

export interface QuestionEventStream {
  /** Stops reconnecting and closes the current socket. Safe to call twice. */
  close: () => void;
}

const DEFAULT_RETRY_DELAYS_MS = [1_000, 2_000, 5_000, 10_000, 30_000];

function defaultUrl(): string {
  const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
  return `${wsProtocol}//${window.location.host}/ws/events`;
}

function text(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null;
}

/**
 * Reads one question payload, or null when the frame is not one. Every field is checked rather than
 * cast: this socket is app-wide, so a malformed frame must drop that one question instead of
 * seeding the inbox with an entry whose key is `undefined`.
 */
function asQuestion(value: unknown): PendingQuestionEvent | null {
  if (!value || typeof value !== 'object') return null;
  const row = value as Record<string, unknown>;
  const rootThreadId = text(row.rootThreadId);
  const toolCallId = text(row.toolCallId);
  if (!rootThreadId || !toolCallId) return null;
  return {
    rootThreadId,
    toolCallId,
    agentId: text(row.agentId),
    childThreadId: text(row.childThreadId),
    prompt: text(row.prompt) ?? 'Question waiting for your answer',
    conversationTitle: text(row.conversationTitle),
    agentName: text(row.agentName),
    raisedAtUtc: text(row.raisedAtUtc) ?? new Date().toISOString(),
  };
}

/**
 * Subscribe to the app-wide question stream (`/ws/events`), reconnecting with backoff for as long as
 * the returned handle is open.
 *
 * Unlike `/ws` and `/ws/subagent` this carries no conversation content and takes no thread id: it is
 * one connection per CLIENT, not per conversation, because its whole job is to report questions
 * parked in the conversations the user is NOT currently looking at. The server filters every frame
 * against the principal captured at the handshake, so what arrives here is already limited to
 * threads this caller may read.
 */
export function connectQuestionEvents(
  handlers: QuestionEventHandlers,
  options: QuestionEventStreamOptions = {}
): QuestionEventStream {
  const url = options.url ?? defaultUrl();
  const delays = options.retryDelaysMs ?? DEFAULT_RETRY_DELAYS_MS;
  const open =
    options.openSocket ??
    ((target: string, protocols: string[] | undefined) =>
      protocols === undefined ? new WebSocket(target) : new WebSocket(target, protocols));

  let closed = false;
  let socket: WebSocket | null = null;
  let attempt = 0;
  let retry: ReturnType<typeof setTimeout> | undefined;

  function handle(data: string) {
    const frame = JSON.parse(data) as Record<string, unknown>;
    switch (frame.$type) {
      case 'snapshot': {
        const rows = Array.isArray(frame.questions) ? frame.questions : [];
        const questions = rows
          .map(asQuestion)
          .filter((question): question is PendingQuestionEvent => question !== null);
        log.info('Received pending-question snapshot', { count: questions.length });
        handlers.onSnapshot(questions);
        return;
      }
      case 'question_pending': {
        const question = asQuestion(frame);
        if (!question) {
          log.warn('Ignoring malformed question_pending frame');
          return;
        }
        // Ids only: `prompt` is agent/user content and must not reach client diagnostics.
        log.info('Question parked', {
          rootThreadId: question.rootThreadId,
          agentId: question.agentId,
          toolCallId: question.toolCallId,
        });
        handlers.onPending(question);
        return;
      }
      case 'question_settled': {
        const rootThreadId = text(frame.rootThreadId);
        const toolCallId = text(frame.toolCallId);
        if (!rootThreadId || !toolCallId) {
          log.warn('Ignoring malformed question_settled frame');
          return;
        }
        log.info('Question settled', { rootThreadId, toolCallId });
        handlers.onSettled(rootThreadId, toolCallId);
        return;
      }
      default:
        log.debug('Ignoring unknown event frame', { type: String(frame.$type ?? '') });
    }
  }

  function scheduleReconnect() {
    if (closed) return;
    const delay = delays[Math.min(attempt, delays.length - 1)] ?? 0;
    attempt += 1;
    log.info('Reconnecting to question events', { delay, attempt });
    retry = setTimeout(connect, delay);
  }

  function connect() {
    if (closed) return;
    let next: WebSocket;
    try {
      // Arity matters for the signed-out case: see `webSocketSubProtocols`.
      next = open(url, webSocketSubProtocols());
    } catch (err) {
      log.error('Failed to open question events socket', {
        errorName: err instanceof Error ? err.name : typeof err,
      });
      scheduleReconnect();
      return;
    }
    socket = next;

    next.onopen = () => {
      // Reset only once a handshake has succeeded, so a server that accepts and immediately drops us
      // still backs off instead of being hammered at the shortest delay forever.
      attempt = 0;
      log.info('Question events connected');
    };
    next.onmessage = (event) => {
      try {
        handle(event.data as string);
      } catch (err) {
        log.error('Failed to parse question event frame', {
          errorName: err instanceof Error ? err.name : typeof err,
        });
      }
    };
    next.onerror = () => {
      // `onclose` always follows and owns the reconnect; this only records the failure.
      log.warn('Question events socket error');
    };
    next.onclose = (event) => {
      socket = null;
      log.info('Question events closed', { code: event.code, wasClean: event.wasClean });
      scheduleReconnect();
    };
  }

  connect();

  return {
    close() {
      closed = true;
      if (retry !== undefined) clearTimeout(retry);
      retry = undefined;
      const current = socket;
      socket = null;
      if (
        current &&
        (current.readyState === WebSocket.OPEN || current.readyState === WebSocket.CONNECTING)
      ) {
        current.close(1000, 'Client closing');
      }
    },
  };
}
