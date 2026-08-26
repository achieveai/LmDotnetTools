import { ref, computed } from 'vue';
import type {
  Message,
  TextMessage,
  UsageMessage,
  ToolCallResultMessage,
  DisplayItem,
  MessageStatus,
  ToolsCallMessage,
  ToolCallMessage,
  AuthEvent,
  AuthRequiredEvent,
} from '@/types';
import {
  MessageType,
  isUsageMessage,
  isRunAssignmentMessage,
  isRunCompletedMessage,
  isToolCallResultMessage,
  isTextMessage,
  isTextUpdateMessage,
  isReasoningMessage,
  isReasoningUpdateMessage,
  isToolsCallMessage,
  isToolsCallUpdateMessage,
  isToolCallUpdateMessage,
  isToolCallMessage,
  isServerToolUseMessage,
  isServerToolResultMessage,
  isTextWithCitationsMessage,
  isNotifyMessage,
  isAgentMessage,
  isConversationUsageMessage,
  normalizeReasoningVisibility,
} from '@/types';
import { sendChatMessage } from '@/api/chatClient';
import type { ConversationUsageAggregate } from '@/api/conversationsApi';
import { useMessageMerger } from './useMessageMerger';
import { getMergeKey } from './messageMergeKey';
import { createStreamResyncCoordinator } from './streamResync';
import { buildDisplayItems } from './messageDisplay';
import {
  serverToolUseToToolsCall,
  serverToolResultToToolCallResult,
  textWithCitationsToText,
} from './messageConversions';
import { logger } from '@/utils';

const log = logger.forComponent('useChat');

/**
 * The WebSocket client module, imported once. Every socket operation below needs it and `import()`
 * hands back the same module every time, so re-importing per call buys nothing — while two imports
 * of it in flight at once (the close a drop starts overlapping the open of the next send) is a
 * hazard worth simply not having.
 */
let wsClientModule: Promise<typeof import('@/api/wsClient')> | null = null;
const loadWsClient = (): Promise<typeof import('@/api/wsClient')> =>
  (wsClientModule ??= import('@/api/wsClient').catch((err) => {
    // Only a SUCCESSFUL load is worth keeping: a chunk that failed to arrive (offline, bad deploy)
    // must stay retryable, or one unlucky fetch would disable every socket operation for the session.
    wsClientModule = null;
    throw err;
  }));

/**
 * Transport type for streaming messages
 */
export type TransportType = 'sse' | 'websocket';

/**
 * Internal chat message structure for tracking
 */
interface InternalChatMessage {
  id: string;
  role: 'user' | 'assistant';
  status: MessageStatus;
  content: Message;
  runId?: string | null;
  parentRunId?: string | null;
  generationId?: string | null;
  messageOrderIdx?: number | null;
  timestamp: number;
  isStreaming?: boolean;
}

/**
 * Exported ChatMessage for backward compatibility with tests
 */
export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: Message;
  isStreaming?: boolean;
}

/**
 * Options for useChat composable
 */
export interface UseChatOptions {
  transport?: TransportType;
  getModeId?: () => string | undefined;
  /**
   * Resolves the provider id to send on the WebSocket query string when the
   * connection opens. Returning <c>null</c>/<c>undefined</c> lets the server
   * fall back to its configured default.
   */
  getProviderId?: () => string | null | undefined;
  /**
   * Resolves the workspace id to send on the WebSocket query string when the
   * connection opens. Returning <c>null</c>/<c>undefined</c> lets the server
   * fall back to its configured default.
   */
  getWorkspaceId?: () => string | null | undefined;
  /**
   * Reserves a conversation on the SERVER and resolves to the thread id it minted (#435). Wired by
   * `ChatLayout` to `useConversations.createNewConversation`, which POSTs `/api/conversations`.
   *
   * This composable has no id of its own: under `Identity:Enforce=true` the `/ws` gate refuses a
   * thread id with no metadata row — byte-identically to one owned by somebody else — so a locally
   * minted id can never open a socket. Omitting this hook means the caller has no way to start a
   * NEW conversation; sends into an already-established one (`setThreadId`) never reach it.
   */
  provisionThreadId?: () => Promise<string>;
}

/**
 * Check if message contains test instructions
 */
export function isTestInstruction(text: string): boolean {
  return text.includes('<|instruction_start|>') && text.includes('<|instruction_end|>');
}

/**
 * Get display text for a message, transforming test instructions
 */
export function getDisplayText(text: string): string {
  return isTestInstruction(text) ? '🧪 Test instruction sent' : text;
}

/**
 * Fresh (uncached) input tokens for one usage row. `cacheRead` is a SUBSET of `input` for the OpenAI
 * family, so In = input - cacheRead; when a provider reports `cacheRead >= input` (some report cache reads
 * additively) fall back to the full input so the banner value never goes negative. Shared by the live
 * stream and the reload path so both normalize identically, and applied PER MODEL ROW before summing so a
 * mix of rows (some with cacheRead > input) is handled correctly (#196).
 */
export function uncachedInput(input: number, cacheRead: number): number {
  return cacheRead <= input ? input - cacheRead : input;
}

/**
 * Composable for managing chat state and interactions
 */
export function useChat(options: UseChatOptions = {}) {
  const { transport: initialTransport = 'websocket', getModeId, getProviderId, getWorkspaceId, provisionThreadId } = options;
  const recordEnabled = isRecordingEnabledFromPageQuery();

  // Core state
  const pendingMessages = ref<InternalChatMessage[]>([]);
  const messageIndex = ref<Map<string, InternalChatMessage>>(new Map());
  const messageOrder = ref<string[]>([]); // Order of message IDs for display
  
  const isLoading = ref(false); // Stream is active (receiving messages)
  const isSending = ref(false); // Message send is in progress
  const error = ref<string | null>(null);
  const usage = ref<UsageMessage | null>(null);
  const cumulativeUsage = ref({
    promptTokens: 0,
    // Fresh (uncached) input tokens — promptTokens minus the cached read. For the OpenAI family
    // cachedTokens is a SUBSET of promptTokens, so In/Cached/Out are disjoint and sum to Total.
    uncachedInputTokens: 0,
    completionTokens: 0,
    totalTokens: 0,
    cachedTokens: 0,
    cacheCreationTokens: 0,
  });
  // Conversation-wide cost (#196), kept separate from the token tuple so the token-accumulation path stays
  // untouched. Populated only from authoritative folded sources (the live usage frame / the persisted
  // aggregate); null when no contributing model had a known rate (rendered as "unavailable", never $0).
  const cumulativeCost = ref<{
    estimatedCostMicros: number | null;
    providerReportedCostMicros: number | null;
    currency: string;
  }>({ estimatedCostMicros: null, providerReportedCostMicros: null, currency: 'USD' });
  const transport = ref<TransportType>(initialTransport);
  const threadId = ref<string | null>(null);
  const currentRunId = ref<string | null>(null);

  /**
   * Replaces the usage banner with a folded conversation-wide aggregate (#196). The authoritative source
   * for BOTH the reload path and the run-complete reconcile — SET (not accumulate) so the live and reload
   * views agree by construction. Uncached input is normalized PER MODEL ROW before summing (matching the
   * live rule) so a mix of rows (some with cacheRead > input) is handled correctly.
   */
  function applyAggregateToBanner(aggregate: ConversationUsageAggregate): void {
    const input = aggregate.perModel.reduce((sum, m) => sum + m.inputTokens, 0);
    const output = aggregate.perModel.reduce((sum, m) => sum + m.outputTokens, 0);
    const cached = aggregate.perModel.reduce((sum, m) => sum + m.cacheReadTokens, 0);
    const cacheCreation = aggregate.perModel.reduce((sum, m) => sum + m.cacheWriteTokens, 0);
    const uncachedInputTokens = aggregate.perModel.reduce(
      (sum, m) => sum + uncachedInput(m.inputTokens, m.cacheReadTokens),
      0,
    );
    cumulativeUsage.value = {
      promptTokens: input,
      uncachedInputTokens,
      completionTokens: output,
      totalTokens: aggregate.totalTokens,
      cachedTokens: cached,
      cacheCreationTokens: cacheCreation,
    };
    cumulativeCost.value = {
      estimatedCostMicros: aggregate.estimatedPublicCostMicros ?? null,
      providerReportedCostMicros: aggregate.providerReportedCostMicros ?? null,
      currency: aggregate.currency ?? 'USD',
    };
  }

  /**
   * Re-reads the authoritative persisted aggregate after a run completes and reconciles the banner with it
   * (#196, hybrid). Guards against downgrading: applies only when the server's total is >= the current
   * banner, so a still-in-flight fire-and-forget persist (a lower stale read) can never lower a fresher
   * live figure, while a banner left low by a dropped live frame is corrected upward.
   */
  async function reconcileUsageFromServer(id: string): Promise<void> {
    try {
      const { getConversationUsage } = await import('@/api/conversationsApi');
      const aggregate = await getConversationUsage(id);
      if (
        threadId.value === id &&
        aggregate &&
        aggregate.totalTokens >= cumulativeUsage.value.totalTokens
      ) {
        applyAggregateToBanner(aggregate);
      }
    } catch (e) {
      log.warn('Failed to reconcile usage banner after run completion', { error: String(e) });
    }
  }

  // Content turn epoch (BUG #8 + text interleaving). The server now mints a per-turn generationId so
  // live streams are unambiguous, but this stays as defense-in-depth AND to render conversations
  // PERSISTED before that fix — whose reasoning/text reuse one run-scoped generationId with
  // messageOrderIdx reset each turn — without later turns collapsing onto the first block (e.g. text
  // between tool calls pinned to the top instead of interleaving). These are plain closure vars (no
  // reactivity needed) tracking arrival order: bump the epoch whenever content (text/reasoning)
  // resumes after intervening non-content (a tool call), then fold it into the content merge key via
  // getMergeKey AND the merger accumulator key via processUpdate. Reset on conversation clear/load.
  let contentTurnEpoch = 0;
  let sawNonContentSinceContent = true; // seed true so the very first content opens epoch 1

  function isContentMessage(msg: Message): boolean {
    return (
      isReasoningMessage(msg) || isReasoningUpdateMessage(msg) ||
      isTextMessage(msg) || isTextUpdateMessage(msg)
    );
  }

  /**
   * Advance and return the content turn epoch for a message in arrival order. Consecutive content
   * messages of one turn (its thinking + text parts and their finalizations) share one epoch; any
   * intervening non-content (a tool call) marks the next content message as a new turn. Non-content
   * messages only record that boundary.
   */
  function contentTurnSeqFor(msg: Message): number {
    if (isContentMessage(msg)) {
      if (sawNonContentSinceContent) {
        contentTurnEpoch++;
        sawNonContentSinceContent = false;
      }
      return contentTurnEpoch;
    }
    sawNonContentSinceContent = true;
    return contentTurnEpoch;
  }

  function resetContentTurnEpoch(): void {
    contentTurnEpoch = 0;
    sawNonContentSinceContent = true;
  }

  // Tool results map: tool_call_id -> ToolCallResultMessage
  const toolResults = ref<Map<string, ToolCallResultMessage>>(new Map());
  
  // Persistent WebSocket connection for full-duplex communication
  let wsConnection: import('@/api/wsClient').WebSocketConnection | null = null;

  /**
   * Per-connection lifecycle facts the resync path needs. A drop is only recoverable if THIS socket
   * neither completed its run (`doneReceived`) nor was torn down by us (`closedByClient`, e.g. a
   * conversation switch or cancel) — otherwise `onClose` would resurrect streams the user ended.
   * `resyncRequested` funnels the two signals of one logical drop (an explicit `stream_recovery`
   * frame and the close that immediately follows it) into a single request.
   */
  interface StreamSocketState {
    /** Identifies the one physical drop this socket can report, for the coordinator's coalescing. */
    id: string;
    threadId: string;
    epoch: number;
    /** The run the server has assigned to this socket, or null before it has assigned one. */
    runId: string | null;
    doneReceived: boolean;
    resyncRequested: boolean;
    closedByClient: boolean;
    /**
     * The server sent a structured error frame (an `onError` carrying a `code`). That is terminal
     * for the run — the banner is the answer — so the close that follows must not be recovered from.
     * Distinct from `closedByClient`: a code-less transport error is only the SYMPTOM of a drop.
     */
    terminalError: boolean;
    /**
     * The ROUTE this socket was opened for — thread, mode, provider and workspace, exactly as they
     * were read when it connected. A prompt may only be handed to a socket somebody else opened if
     * that socket is serving the same route the send itself asked for; the connection object knows
     * only its thread, so the rest has to be remembered here.
     */
    route: string;
  }
  let activeSocketState: StreamSocketState | null = null;
  let socketSequence = 0;

  /**
   * The in-place history fill started by a `replay_truncated` advisory, and the frames that arrived
   * on the advised socket while it was in flight.
   *
   * `replay_truncated` keeps the socket OPEN (see `onStreamRecovery`), so the live tail goes on
   * arriving throughout an asynchronous refetch whose tail-end is DESTRUCTIVE — it wipes the message
   * index, the merger accumulators and the pending queue before rebuilding from persisted history.
   * A frame applied during that window is therefore wiped by the reload that lands after it, and
   * persisted history cannot bring it back: it is precisely the tail the server has not persisted
   * yet. Holding those frames here and replaying them AFTERWARDS — in arrival order, through the
   * very same `handleMessage` — is what keeps them, without re-implementing a single thing about
   * merge keys, content-turn epochs or accumulation.
   */
  let truncatedReplayRehydrate: {
    socket: StreamSocketState;
    /** The conversation this fill belongs to; a queue outliving its epoch must be discarded. */
    epoch: number;
    deferred: Array<() => void>;
  } | null = null;

  /**
   * Render `apply` now, or hold it until this socket's in-place history fill has finished. Used for
   * everything that PAINTS (`onMessage` for every message kind, and the completion half of `onDone`);
   * per-socket lifecycle bookkeeping stays immediate, because that is a fact about the connection
   * rather than about the transcript.
   */
  function applyOrDefer(socketState: StreamSocketState, apply: () => void): void {
    const rehydrate = truncatedReplayRehydrate;
    if (rehydrate && rehydrate.socket === socketState) {
      rehydrate.deferred.push(apply);
      return;
    }
    apply();
  }

  /**
   * Fill the hole a `replay_truncated` advisory reported, in place, without dropping the socket.
   *
   * Single-flight per socket: the server advises once per resumed subscription, so the SAME hole can
   * be announced repeatedly — a second refetch would restart the destructive reload and throw away
   * whatever the first one is holding. An advisory from a DIFFERENT socket supersedes instead: the
   * superseded record's settle sees that it is no longer installed and drops its queue, which is
   * right, because frames buffered for a socket that is no longer live are re-delivered by whatever
   * replaced it.
   */
  function fillTruncatedReplayHole(socketState: StreamSocketState): void {
    // Same guard the error path applies: a socket that is no longer the live one, has already asked
    // for recovery, or belongs to a conversation the user has left must not trigger a reload of the
    // conversation now on screen.
    if (
      activeSocketState !== socketState ||
      socketState.resyncRequested ||
      socketState.epoch !== conversationEpoch
    ) {
      log.debug('Ignoring a truncated-replay advisory from a socket that is no longer the live one', {
        threadId: socketState.threadId,
      });
      return;
    }
    if (truncatedReplayRehydrate?.socket === socketState) return;

    const rehydrate = { socket: socketState, epoch: conversationEpoch, deferred: [] as Array<() => void> };
    truncatedReplayRehydrate = rehydrate;

    // `preservePending`: unlike a conversation switch, this fills a hole in the conversation still on
    // screen — the prompts queued against it are still going to be sent and must not be discarded.
    void loadMessagesFromBackend(socketState.threadId, { preservePending: true })
      .catch((err) => {
        // Swallowed, not rethrown: this chain is void-ed, so a rejection escaping it would surface as
        // an unhandled rejection. A failed fill is recoverable — the next frame still renders.
        log.warn('Failed to rehydrate history after a truncated replay', {
          threadId: socketState.threadId,
          error: err instanceof Error ? err.name : 'unknown',
        });
      })
      .finally(() => {
        // A newer advisory took over; its record owns the socket now and this queue is not its.
        if (truncatedReplayRehydrate !== rehydrate) return;
        truncatedReplayRehydrate = null;
        // The reload discards its own result on a stale epoch; the frames held beside it go the same
        // way, or a conversation the user left would paint itself onto the one they opened. A socket
        // that has been replaced is equally not the one being rendered.
        if (rehydrate.epoch !== conversationEpoch || activeSocketState !== rehydrate.socket) {
          log.debug('Discarding frames buffered for a stream that is no longer the live one', {
            threadId: rehydrate.socket.threadId,
            bufferedCount: rehydrate.deferred.length,
          });
          return;
        }
        // `finally`, so this also runs when the fetch FAILED: a refetch that threw performed no wipe,
        // so the tail it was holding is still valid and dropping it would freeze the transcript over a
        // transient error. (The catch above independently makes that true today; `finally` keeps the
        // "the queue always drains" invariant local to this line rather than to its neighbour.)
        for (const apply of rehydrate.deferred) apply();
      });
  }

  /** The identity a stream socket is bound to. Compared as a whole; never parsed apart. */
  function streamRoute(
    thread: string,
    modeId: string | undefined,
    providerId: string | null | undefined,
    workspaceId: string | null | undefined
  ): string {
    return JSON.stringify([thread, modeId ?? null, providerId ?? null, workspaceId ?? null]);
  }

  /**
   * A new run is starting on a socket that already carried one — the full-duplex reuse path in
   * `sendMessageViaWebSocket`, where turn 2 goes out on turn 1's connection. `doneReceived` and
   * `terminalError` are facts about the run that ENDED; the connection (and `closedByClient`, which
   * is about the connection) outlives it. Leaving them set makes `requestStreamResync` read a live
   * turn as "this run already finished" and abandon it, so no drop after turn 1 would ever recover.
   *
   * Called from BOTH ends of the run boundary: optimistically at send time, and — authoritatively —
   * when the server's `run_assignment` names a run this socket is not already carrying. The send
   * alone is not enough, because full duplex lets the user send turn 2 while turn 1 is still
   * streaming: turn 1's `done` then lands AFTER that send and re-arms `doneReceived` for the queued
   * turn. Only the server knows when a run actually begins, and `run_assignment` is how it says so.
   *
   * A non-null `wsConnection` implies `activeSocketState` is ITS state — `openStreamConnection`
   * closes and nulls the previous connection before installing the new state.
   */
  function beginRunOnActiveSocket(runId: string | null = null): void {
    const state = activeSocketState;
    if (!state) return;
    // A socket that has already asked for recovery is detached and being replaced. Re-arming it
    // would let a second signal from the same corpse start a SECOND recovery for one physical drop.
    if (state.resyncRequested) return;
    // The same run re-announcing itself is not a new run: the backend replays `run_assignment` at
    // the head of every resumed stream, so a replacement socket sees the id it is already carrying.
    if (runId !== null && state.runId === runId) return;
    state.runId = runId;
    state.doneReceived = false;
    state.terminalError = false;
  }

  /**
   * Conversation generation. Bumped whenever the composable stops being "about" the conversation it
   * was about (thread switch, clear), so a close callback that lands after the switch can be
   * recognised as stale and dropped instead of rehydrating an abandoned conversation.
   */
  let conversationEpoch = 0;

  /** Move to a new conversation generation and abandon any recovery that belonged to the old one. */
  function beginConversationEpoch(): void {
    conversationEpoch += 1;
    resyncCoordinator.invalidate();
  }

  /**
   * Close the active socket. By DEFAULT this is a deliberate client action (conversation switch,
   * cancel, sandbox refresh) and marks the connection so its `onClose` is not mistaken for a
   * server-side drop. Pass `deliberate: false` for cleanup triggered BY the transport: wsClient
   * reports an abnormal drop as `onError` immediately followed by `onClose`, so latching
   * "the client closed this" from the error handler would make that close unrecoverable.
   * Single place for what used to be five copies of the dynamic-import + close + null dance.
   */
  async function closeActiveConnection(options: { deliberate?: boolean } = {}): Promise<void> {
    const connection = wsConnection;
    if (!connection) return;
    // Detach synchronously: a resync request can run during the await below, and must not find
    // (nor re-close) a socket this call already owns.
    wsConnection = null;
    if (options.deliberate !== false && activeSocketState) activeSocketState.closedByClient = true;
    const { closeWebSocketConnection } = await loadWsClient();
    closeWebSocketConnection(connection);
  }

  /**
   * The in-flight close of the socket a drop detached from `wsConnection`. Started at DETACH time
   * rather than as the recovery's first step, because the coordinator REJECTS stale and over-budget
   * requests without running a single step — a socket released only by that step would then stay open
   * forever, still pushing frames from a stream we have declared dead. The step awaits this promise
   * instead, so an ACCEPTED request keeps the strict close → REST → run-state → open ordering.
   *
   * One slot is enough: `request()` starts its operation synchronously, and that operation's first
   * step takes this promise before its own first await, so an accepted request always consumes the
   * close belonging to the socket it detached. Closes that pile up behind a REJECTED request chain
   * onto each other (see `parkDroppedSocketClose`) instead of overwriting one another.
   */
  let droppedSocketClose: Promise<void> | null = null;

  /**
   * Remember the close of a socket a drop just detached. CHAINS onto an unconsumed previous close
   * rather than replacing it: a request the coordinator rejected leaves its close parked, and simply
   * overwriting that promise would let a replacement socket open while the one it replaces is still
   * closing — the overlap the strict close -> REST -> run-state -> open ordering exists to prevent.
   */
  function parkDroppedSocketClose(closing: Promise<void>): void {
    const previous = droppedSocketClose;
    droppedSocketClose = previous ? previous.then(() => closing) : closing;
  }

  /** Wait for the close (or chain of closes) a drop started, if there was one. */
  async function awaitDroppedSocketClose(): Promise<void> {
    const closing = droppedSocketClose;
    droppedSocketClose = null;
    await closing;
  }

  let pendingSandboxRefreshRetry: (() => Promise<void>) | null = null;
  let pendingSandboxRefreshFailure: (() => void) | null = null;
  let sandboxRefreshDeferred = false;
  let sandboxRefreshThreadId: string | null = null;

  function clearSandboxRefreshState(): void {
    pendingSandboxRefreshRetry = null;
    pendingSandboxRefreshFailure = null;
    sandboxRefreshDeferred = false;
    sandboxRefreshThreadId = null;
  }

  // Deferred-auth prompts pushed by the backend while a sandbox webhook call is held
  // (providerId -> auth_required event). Replaced wholesale on change for Vue reactivity.
  const pendingAuth = ref<Map<string, AuthRequiredEvent>>(new Map());

  /** Handle an out-of-band deferred-auth frame from the WebSocket. */
  function handleAuthEvent(event: AuthEvent): void {
    const next = new Map(pendingAuth.value);
    if (event.$type === 'auth_required') {
      log.info('Auth required', { providerId: event.providerId, signinUrl: event.signinUrl });
      next.set(event.providerId, event);
    } else {
      // auth_completed (token landed) or auth_denied (timeout / failed / disabled): both are
      // terminal — dismiss the prompt for that provider.
      log.info('Auth resolved', { providerId: event.providerId, type: event.$type });
      next.delete(event.providerId);
    }
    pendingAuth.value = next;
  }

  /** Dismiss a deferred-auth prompt locally (e.g. user closed it or signed in). */
  function dismissAuthRequest(providerId: string): void {
    if (!pendingAuth.value.has(providerId)) return;
    const next = new Map(pendingAuth.value);
    next.delete(providerId);
    pendingAuth.value = next;
  }

  const pendingAuthRequests = computed(() => [...pendingAuth.value.values()]);

  const { processUpdate, finalize, reset } = useMessageMerger();

  /**
   * Get tool call result by tool_call_id
   */
  function getResultForToolCall(toolCallId: string | null | undefined): ToolCallResultMessage | null {
    if (!toolCallId) return null;
    return toolResults.value.get(toolCallId) || null;
  }

  // #246: a deferred client tool (e.g. AskUserQuestion) leaves a placeholder
  // ToolCallResultMessage (`is_deferred: true`, `result: ''`) in toolResults until the browser
  // submits an answer and the server republishes the SAME tool_call_id with the real result. True
  // whenever ANY tracked tool result is still in that placeholder state.
  const hasPendingClientQuestion = computed(() => {
    for (const result of toolResults.value.values()) {
      if (result.is_deferred) return true;
    }
    return false;
  });

  // #246: resolvers for in-flight submitClientToolResult() calls, keyed by tool_call_id, so the
  // wsClient onClientToolResultAck/onClientToolResultError callbacks (wired into every
  // createWebSocketConnection call below) can settle the right promise.
  const pendingSubmissions = new Map<
    string,
    (outcome: import('./useClientToolSubmit').ClientToolSubmitOutcome) => void
  >();

  /**
   * Settle (and clear) every in-flight submitClientToolResult() promise with the same outcome
   * (#246 defect 2). Called from the connection's onError/onClose so a submission never hangs
   * forever waiting on an ack/error frame that will now never arrive — without this a caller's
   * `finally` (e.g. QuestionRich unlocking its UI) would never run.
   */
  function settlePendingSubmissions(
    outcome: import('./useClientToolSubmit').ClientToolSubmitOutcome
  ): void {
    if (pendingSubmissions.size === 0) return;
    for (const resolve of pendingSubmissions.values()) {
      resolve(outcome);
    }
    pendingSubmissions.clear();
  }

  /**
   * Resolve the thread this chat is on, provisioning one on the SERVER the first time (#435).
   *
   * Deliberately async and deliberately without a local fallback: the id has to exist as a metadata
   * row before `/ws` will accept a handshake for it under `Identity:Enforce=true`, and the gate's
   * refusal for an unknown id is byte-identical to its refusal for one owned by somebody else. A
   * failure propagates to `sendMessage`, which turns it into the visible error banner.
   */
  async function ensureThreadId(): Promise<string> {
    if (threadId.value) {
      return threadId.value;
    }
    if (!provisionThreadId) {
      throw new Error(
        'Cannot start a new conversation: no provisioning hook was supplied to useChat.'
      );
    }
    const provisioned = await provisionThreadId();
    threadId.value = provisioned;
    log.info('Provisioned new thread', { threadId: provisioned });
    return provisioned;
  }

  /**
   * Build run hierarchy and sort messages
   */
  function sortMessages(): InternalChatMessage[] {
    const allMessages: InternalChatMessage[] = [];
    
    // Messages are already received in correct order from the backend
    // Simply collect them in the order they were added to messageOrder
    for (const msgId of messageOrder.value) {
      const msg = messageIndex.value.get(msgId);
      if (msg && msg.status !== 'pending') {
        allMessages.push(msg);
      }
    }

    // No sorting needed - preserve arrival order
    return allMessages;
  }

  /**
   * Transform messages into display items with pill grouping. Delegates to the shared
   * {@link buildDisplayItems} (extracted so the sub-agent panel renders identically) — `sortMessages`
   * already returns non-pending messages in arrival order.
   */
  const displayItems = computed<DisplayItem[]>(() => buildDisplayItems(sortMessages()));

  /**
   * Handle RunAssignment message - activate pending messages
   */
  function handleRunAssignment(msg: Message) {
    if (!isRunAssignmentMessage(msg)) return;

    log.info('RunAssignment raw message', { msg });
    
    const assignment = msg.Assignment;
    // Wire format normalized in wsClient.ts (PascalCase -> camelCase aliases at the
    // deserialize boundary), so we read camelCase directly here.
    const runId = assignment.runId;
    const generationId = assignment.generationId;
    const inputIds = assignment.inputIds ?? [];
    const parentRunId = assignment.parentRunId;
    
    currentRunId.value = runId;
    // The SERVER decides when a run begins, and the connection outlives the run: full duplex sends
    // turn 2 on turn 1's socket, and the server starts that queued turn on its own schedule (after
    // turn 1's `done`). Clearing the previous run's lifecycle flags here — not only at send time —
    // is what keeps a drop on any turn but the first recoverable.
    beginRunOnActiveSocket(runId);
    log.info('Run assignment received', { 
      runId, 
      generationId,
      inputIds,
      inputCount: inputIds.length,
      parentRunId,
      pendingCount: pendingMessages.value.length
    });

    // Activate pending messages in FIFO order
    // The backend sends inputIds for the messages it processed in order
    const activationCount = Math.min(inputIds.length, pendingMessages.value.length);
    
    for (let i = 0; i < activationCount; i++) {
      const inputId = inputIds[i];
      
      // Remove from pending queue FIRST (before mutation)
      const pending = pendingMessages.value.shift();
      
      if (pending) {
        const oldId = pending.id;
        
        // Update the message with real backend ID and metadata
        pending.id = inputId;
        pending.status = 'active';
        pending.runId = runId;
        pending.parentRunId = parentRunId;
        
        // Move to main message index
        messageIndex.value.set(inputId, pending);
        messageOrder.value.push(inputId);
        
        log.info('Activated pending message', { 
          oldId, 
          newId: inputId, 
          runId,
          text: (pending.content as TextMessage).text?.substring(0, 50)
        });
      }
    }
    
    if (inputIds.length > activationCount) {
      log.warn('More inputIds than pending messages', { 
        inputCount: inputIds.length, 
        pendingCount: pendingMessages.value.length,
        extraIds: inputIds.slice(activationCount)
      });
    }
  }

  /**
   * Handle RunCompleted message
   */
  function handleRunCompleted(msg: Message) {
    if (!isRunCompletedMessage(msg)) return;

    // Wire format normalized in wsClient.ts (PascalCase -> camelCase aliases at the
    // deserialize boundary). Read camelCase directly — handlers no longer carry the
    // dual-casing burden.
    const completedRunId = msg.completedRunId;
    const hasPendingMessages = msg.hasPendingMessages;
    const isError = msg.isError;
    const errorMessage = msg.errorMessage;
    const generationId = msg.generationId;

    log.debug('Run completed', {
      runId: completedRunId,
      hasPending: hasPendingMessages,
      isError,
    });

    // If the run ended with an error, add an error message to the chat and
    // surface it on the banner so users see a clear failure state. On a clean
    // completion, clear any stale banner left over from a prior failed run so
    // it doesn't persist across turns.
    if (isError && errorMessage) {
      const errorId = `error-${completedRunId}`;
      const errorMsg: InternalChatMessage = {
        id: errorId,
        role: 'assistant',
        status: 'completed',
        content: {
          $type: MessageType.Text,
          role: 'assistant',
          text: `Error: ${errorMessage}`,
        },
        isStreaming: false,
        runId: completedRunId,
        generationId,
        timestamp: Date.now(),
      };
      messageIndex.value.set(errorId, errorMsg);
      messageOrder.value.push(errorId);
      error.value = errorMessage;
    } else {
      error.value = null;
    }

    // Mark all messages in this run as completed
    for (const message of messageIndex.value.values()) {
      if (message.runId === completedRunId && message.status === 'active') {
        message.status = 'completed';
        message.isStreaming = false;
      }
    }

    // Reconcile the banner with the authoritative persisted aggregate (which includes sub-agent / workflow
    // descendant spend) now that the run is done — the final authority behind the live frames (#196,
    // hybrid). Fire-and-forget: banner reconciliation must not block run-completion handling.
    const reconcileId = threadId.value;
    if (reconcileId) {
      void reconcileUsageFromServer(reconcileId);
    }
  }

  /**
   * Handle a `generation_abandoned` control frame: a provider stream was cut mid-reply, the loop
   * threw that generation away, and the SAME turn is being retried under a fresh generation id on
   * this still-open connection. Without this, the abandoned generation's half-written block stays on
   * screen forever and the retry renders as a SECOND assistant block beside it.
   *
   * Removes exactly the still-streaming blocks of the abandoned generation. A block that already
   * finalized under that generation (`isStreaming === false`) is canonical — the server delivered it
   * whole before the cut — so it is preserved. Deliberately NOT a `reset()`/reconnect: the socket is
   * alive and the run continues; a terminal drop is a different frame entirely.
   *
   * Matching is on the {@link InternalChatMessage.generationId} FIELD, never by parsing the merge-key
   * string: a generationId may itself contain `-` and the key's trailing segments vary by message
   * kind (tool calls append a tool_call_id), so substring matching would both over- and under-match.
   *
   * A no-op when nothing matches, which is what makes a replayed/duplicated frame idempotent.
   */
  function handleGenerationAbandoned(info: {
    threadId?: string;
    runId?: string;
    generationId?: string;
  }): void {
    const abandonedGenerationId = info.generationId;
    if (!abandonedGenerationId) {
      log.warn('generation_abandoned frame without a generationId; ignoring', { runId: info.runId });
      return;
    }

    const abandonedKeys: string[] = [];
    for (const [mergeKey, message] of messageIndex.value.entries()) {
      if (message.generationId === abandonedGenerationId && message.isStreaming) {
        abandonedKeys.push(mergeKey);
      }
    }

    if (abandonedKeys.length > 0) {
      const dropped = new Set(abandonedKeys);
      for (const mergeKey of abandonedKeys) {
        messageIndex.value.delete(mergeKey);
      }
      // messageOrder must drop the same keys: sortMessages() walks it and a dangling id that no
      // longer resolves in messageIndex would leave a hole in the rendered transcript.
      messageOrder.value = messageOrder.value.filter((id) => !dropped.has(id));
    }

    // Clear the merger accumulators for this generation (`genId` plus every `genId::t*` turn-scoped
    // key) so the retry's deltas start from empty instead of concatenating onto the abandoned
    // partial. Safe to call unconditionally — clearing an already-cleared generation is itself a
    // no-op, which keeps a replayed frame idempotent.
    finalize(abandonedGenerationId);

    log.info('Generation abandoned; dropped its unfinalized blocks', {
      generationId: abandonedGenerationId,
      runId: info.runId,
      droppedCount: abandonedKeys.length,
    });
  }

  /**
   * Handle incoming message updates
   */
  function handleMessage(msg: Message) {
    // Handle usage messages
    if (isUsageMessage(msg)) {
      usage.value = msg;
      const u = msg.usage;
      const promptTokens = u.prompt_tokens ?? u.inputTokens ?? 0;
      const completionTokens = u.completion_tokens ?? u.outputTokens ?? 0;
      const totalTokens = u.total_tokens ?? (promptTokens + completionTokens);
      const cachedTokens = u.input_tokens_details?.cached_tokens ?? u.cacheReadTokens ?? 0;
      const cacheCreationTokens = u.cache_creation_input_tokens ?? u.cacheCreationTokens ?? 0;
      // Fresh input for this turn = prompt minus the cached read (never negative; see uncachedInput).
      // Accumulate per-turn so In + Cached + Out == Total holds across the whole conversation.
      const uncachedInputTokens = uncachedInput(promptTokens, cachedTokens);
      cumulativeUsage.value = {
        promptTokens: cumulativeUsage.value.promptTokens + promptTokens,
        uncachedInputTokens: cumulativeUsage.value.uncachedInputTokens + uncachedInputTokens,
        completionTokens: cumulativeUsage.value.completionTokens + completionTokens,
        totalTokens: cumulativeUsage.value.totalTokens + totalTokens,
        cachedTokens: cumulativeUsage.value.cachedTokens + cachedTokens,
        cacheCreationTokens: cumulativeUsage.value.cacheCreationTokens + cacheCreationTokens,
      };
      return;
    }

    // Live conversation-wide usage frame (#196): totals folded across sub-agents / workflow descendants.
    // SET the banner from the pre-computed tuple (authoritative) rather than accumulating — this is what
    // surfaces descendant spend live, and it self-heals any per-turn UsageMessage accumulation drift.
    if (isConversationUsageMessage(msg)) {
      cumulativeUsage.value = {
        promptTokens: msg.promptTokens,
        uncachedInputTokens: msg.uncachedInputTokens,
        completionTokens: msg.completionTokens,
        totalTokens: msg.totalTokens,
        cachedTokens: msg.cachedTokens,
        cacheCreationTokens: msg.cacheCreationTokens,
      };
      cumulativeCost.value = {
        estimatedCostMicros: msg.estimatedCostMicros ?? null,
        providerReportedCostMicros: msg.providerReportedCostMicros ?? null,
        currency: msg.currency ?? 'USD',
      };
      return;
    }

    // Handle lifecycle messages
    if (isRunAssignmentMessage(msg)) {
      handleRunAssignment(msg);
      return;
    }

    if (isRunCompletedMessage(msg)) {
      handleRunCompleted(msg);
      return;
    }

    // Handle tool call results
    if (isToolCallResultMessage(msg)) {
      const tcResult = msg; // narrowed to ToolCallResultMessage
      if (tcResult.tool_call_id) {
        toolResults.value.set(tcResult.tool_call_id, tcResult);
        log.debug('Received tool result', { toolCallId: tcResult.tool_call_id });

        // Find the tool call message and attach the result to it
        for (const chatMsg of messageIndex.value.values()) {
          if (isToolCallMessage(chatMsg.content)) {
            const toolCall = chatMsg.content as ToolCallMessage;
            if (toolCall.tool_call_id === tcResult.tool_call_id) {
              toolCall.result = tcResult.result;
              log.info('Attached result to tool call', {
                toolCallId: tcResult.tool_call_id,
                messageId: chatMsg.id
              });
              break;
            }
          } else if (isToolsCallMessage(chatMsg.content)) {
            const toolsCall = chatMsg.content as ToolsCallMessage;
            const matchingToolCall = toolsCall.tool_calls?.find(tc => tc.tool_call_id === tcResult.tool_call_id);
            if (matchingToolCall) {
              matchingToolCall.result = tcResult.result;
              log.info('Attached result to tool call in ToolsCallMessage', {
                toolCallId: tcResult.tool_call_id,
                messageId: chatMsg.id
              });
              break;
            }
          }
        }
      }
      return;
    }

    // Handle server tool result → convert to ToolCallResultMessage and attach
    if (isServerToolResultMessage(msg)) {
      const converted = serverToolResultToToolCallResult(msg);
      const toolUseId = converted.tool_call_id;
      if (toolUseId) {
        toolResults.value.set(toolUseId, converted);
      }
      log.debug('Received server tool result', { toolName: converted.tool_name, toolUseId, isError: converted.is_error });

      // Attach to matching server tool use (converted to ToolsCallMessage)
      if (toolUseId) {
        for (const chatMsg of messageIndex.value.values()) {
          if (isToolsCallMessage(chatMsg.content)) {
            const toolsCall = chatMsg.content as ToolsCallMessage;
            const matchingToolCall = toolsCall.tool_calls?.find(tc => tc.tool_call_id === toolUseId);
            if (matchingToolCall) {
              matchingToolCall.result = converted.result;
              log.info('Attached server tool result to tool call', { toolUseId });
              break;
            }
          }
        }
      } else {
        log.warn('Server tool result missing tool id', { msg });
      }
      return;
    }

    // Handle server tool use → convert to ToolsCallMessage for pill display
    if (isServerToolUseMessage(msg)) {
      const converted = serverToolUseToToolsCall(msg);
      log.debug('Converted server tool use to ToolsCallMessage', {
        toolName: converted.tool_calls[0]?.function_name,
        toolUseId: converted.tool_calls[0]?.tool_call_id,
      });
      msg = converted;
      // Fall through to normal message handling below
    }

    // Handle text with citations → convert to TextMessage with citations as markdown
    if (isTextWithCitationsMessage(msg)) {
      const citationCount = msg.citations?.length ?? 0;
      msg = textWithCitationsToText(msg);
      log.debug('Converted text with citations to TextMessage', { citationCount });
      // Fall through to normal message handling below
    }

    // Normalize reasoning visibility values from backend numeric enums (0/1/2)
    if (isReasoningMessage(msg)) {
      const normalized = normalizeReasoningVisibility(msg.visibility);
      msg = {
        ...msg,
        visibility: normalized ?? msg.visibility,
      };
    } else if (isReasoningUpdateMessage(msg)) {
      const normalized = normalizeReasoningVisibility(msg.visibility);
      msg = {
        ...msg,
        visibility: normalized ?? msg.visibility ?? null,
      };
    }

    // Determine if this is an update message that needs merging
    const isUpdate = isTextUpdateMessage(msg) || isReasoningUpdateMessage(msg) ||
                     isToolsCallUpdateMessage(msg) || isToolCallUpdateMessage(msg);

    // Determine if this is a complete (non-update) content message. A NotifyMessage and an
    // AgentMessage are terminal, non-streamed messages (out-of-band relative to the human's own
    // turn) — routed here so they are NOT dropped as an "unknown message type"; the displayItems
    // notification branch renders both as pills.
    const isCompleteMessage = isTextMessage(msg) || isReasoningMessage(msg) || isToolsCallMessage(msg) || isToolCallMessage(msg) || isNotifyMessage(msg) || isAgentMessage(msg);

    if (!isUpdate && !isCompleteMessage) {
      // Unknown message type - skip
      log.debug('Skipping unknown message type', { type: msg.$type });
      return;
    }

    // Stamp the active run's id onto live content that arrives without one. On the wire only
    // run_assignment carries a runId; text/reasoning/tool-call messages are streamed runId-less, so
    // getMergeKey would key them to 'default'. The PERSISTED copy of the same message, however, is
    // rehydrated with the producing run's id (loadMessagesFromBackend stamps pm.runId), keying it to
    // the real run id. After a switch-away/back resume those two keys diverged ('default' vs the run
    // id), so the replayed message failed to merge with its rehydrated twin and rendered a duplicate,
    // never-resolving pill (the frozen-tool-pill bug). currentRunId is set by the run_assignment that
    // opens (and, on resume, replays first for) every run, so this aligns the live key with the
    // rehydrated one. No run id yet (e.g. no run_assignment) ⇒ unchanged 'default' fallback.
    if (!msg.runId && currentRunId.value) {
      msg = { ...msg, runId: currentRunId.value };
    }

    // Advance the content turn epoch in arrival order (BUG #8 + text interleaving) so multi-turn
    // thinking/text does not collapse onto the first block; non-content kinds just record the turn
    // boundary. The SAME sequence scopes both the merger accumulator (so deltas don't concatenate
    // across turns) and the display merge key (so each turn is a distinct, correctly-ordered block).
    const turnSeq = contentTurnSeqFor(msg);

    // Process through merger (handles both updates and complete messages)
    const mergedMessage = isUpdate ? processUpdate(msg, turnSeq) : msg;
    const mergeKey = getMergeKey(msg, turnSeq);

    // Find or create message in index
    let chatMessage = messageIndex.value.get(mergeKey);
    
    if (!chatMessage) {
      // Create new message
      chatMessage = {
        id: mergeKey,
        role: 'assistant',
        status: 'active',
        content: mergedMessage,
        runId: msg.runId,
        parentRunId: msg.parentRunId,
        generationId: msg.generationId,
        messageOrderIdx: msg.messageOrderIdx,
        timestamp: Date.now(),
        isStreaming: !isCompleteMessage, // Complete messages are not streaming
      };
      messageIndex.value.set(mergeKey, chatMessage);
      messageOrder.value.push(mergeKey);
      
      log.debug('Created new message', { mergeKey, type: msg.$type, isComplete: isCompleteMessage });
    } else {
      // Update existing message
      if (chatMessage.content.$type !== mergedMessage.$type) {
        log.warn('Merge key type transition', {
          mergeKey,
          previousType: chatMessage.content.$type,
          nextType: mergedMessage.$type,
          runId: msg.runId,
          generationId: msg.generationId,
          messageOrderIdx: msg.messageOrderIdx ?? null,
        });
      }

      chatMessage.content = mergedMessage;
      chatMessage.messageOrderIdx = msg.messageOrderIdx ?? chatMessage.messageOrderIdx;
      if (isCompleteMessage) {
        chatMessage.isStreaming = false;
      }
      
      log.trace('Updated message', { mergeKey, type: msg.$type });
    }
  }

  /**
   * Send a message and stream the response
   */
  async function sendMessage(text: string): Promise<void> {
    if (!text.trim()) return;
    
    // Allow sending messages even while streaming (full-duplex)
    if (isSending.value) {
      log.warn('Already sending a message, queueing not yet implemented');
      return;
    }

    log.info('User sending message', { textLength: text.length, transport: transport.value, isStreaming: isLoading.value });

    error.value = null;
    // A new run is a fresh start for stream recovery. The attempt budget is keyed by
    // (threadId, epoch) and a send changes neither, so without this an earlier run that exhausted
    // the budget would leave every later run in the same conversation permanently unrecoverable.
    // Deliberately NOT invalidate(): that would also abandon a recovery still legitimately running.
    resyncCoordinator.resetAttempts();
    isSending.value = true;
    
    // Only set isLoading if not already streaming (backward compatibility)
    if (!isLoading.value) {
      isLoading.value = true;
    }

    // Check if this is a test instruction
    const isTest = isTestInstruction(text);
    const displayText = isTest ? '🧪 Test instruction sent' : text;

    // Create user message WITHOUT an id (backend will assign one)
    // We use a temporary client-side ID for tracking in the pending queue
    const tempId = `temp-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
    const userMessage: InternalChatMessage = {
      id: tempId,
      role: 'user',
      status: 'pending',
      content: {
        $type: MessageType.Text,
        text: displayText,
        role: 'user',
      } as TextMessage,
      timestamp: Date.now(),
    };

    // Add to pending queue
    pendingMessages.value.push(userMessage);
    log.debug('Added message to pending queue', { tempId, text: displayText.substring(0, 50) });

    const callbacks = buildStreamCallbacks();

    try {
      if (transport.value === 'websocket') {
        // Use persistent connection or create new one
        await sendMessageViaWebSocket(text, callbacks);
      } else {
        await sendChatMessage(text, callbacks);
      }
      
      isSending.value = false;
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Unknown error';
      // Nothing reached the wire (every throw above is raised BEFORE the send), so this prompt
      // starts no run. Leaving it queued would let the next run's `run_assignment` activate it as
      // that run's input — the queue is consumed positionally against `inputIds`. The banner is the
      // user's record of it; the queue must only hold prompts that are still going to be sent.
      pendingMessages.value = pendingMessages.value.filter(msg => msg.id !== tempId);
      isLoading.value = false;
      isSending.value = false;
    }
  }
  
  /**
   * Recovery policy for a stream the server dropped without finishing it. The steps below are the
   * EXISTING conversation-restore path — nothing about how a conversation is rehydrated or resumed
   * is duplicated here; the coordinator only decides whether, when and how often to run it.
   */
  const resyncCoordinator = createStreamResyncCoordinator({
    isCurrent: (thread, epoch) => epoch === conversationEpoch && threadId.value === thread,
    // The dead socket itself, plus the merger's unfinalized accumulators (a half-received delta from
    // it) and the content turn epoch that keys them — NOT messageIndex/messageOrder, which stay on
    // screen and are re-merged by stable identity when the REST history lands. The close was STARTED
    // when the drop detached the socket (so a request this coordinator rejects cannot leak it), and
    // WAITING for it here is what stops the replacement overlapping the socket it replaces.
    // loadMessagesFromBackend repeats the accumulator reset; doing it here too protects the path
    // where that load throws.
    discardDroppedStream: async () => {
      await awaitDroppedSocketClose();
      resetContentTurnEpoch();
      reset();
    },
    // `preservePending`, for the same reason the in-place `replay_truncated` fill passes it: a drop
    // recovery rehydrates the conversation STILL ON SCREEN, at the same epoch, so the prompts queued
    // against it are still going to be sent. Only a conversation SWITCH — the reload's default caller
    // — legitimately leaves the queue behind, so the default stays as it is.
    loadHistory: (thread) => loadMessagesFromBackend(thread, { preservePending: true }),
    // Consults authoritative run state and opens a subscribe-only socket only while the run is
    // genuinely in flight; settles the UI to idle (markStreamIdle) when it is not.
    resubscribe: resumeStreamIfActive,
    reportFailure: (message) => {
      error.value = message;
      markStreamIdle();
    },
  });

  /**
   * Funnel every signal of one logical drop into the single-flight coordinator.
   * Ignores closes we caused, closes after a completed run, and repeats for the same socket.
   */
  function requestStreamResync(socketState: StreamSocketState, reason: string): void {
    if (
      socketState.closedByClient ||
      socketState.doneReceived ||
      socketState.terminalError ||
      socketState.resyncRequested
    ) {
      return;
    }
    socketState.resyncRequested = true;

    if (activeSocketState === socketState) {
      // This socket is gone (or about to be — the recovery frame is always followed by a close).
      // `closeActiveConnection` detaches the reference before its first await, so the subscribe-only
      // reopen is not short-circuited by resumeStreamIfActive's "already connected" guard — and it
      // starts the close NOW rather than in the recovery's first step: a `stream_recovery` frame
      // arrives while the socket is still OPEN, one left open keeps pushing frames from the stream we
      // just declared dead, and the request below may be rejected (stale epoch, or the attempt budget
      // is spent) without ever running a recovery step. `deliberate: false` because we did not choose
      // this close — the drop did.
      parkDroppedSocketClose(closeActiveConnection({ deliberate: false }));
      // The transport may have surfaced "closed unexpectedly" moments ago; a drop we are actively
      // recovering from is not a user-facing failure. If recovery gives up, reportFailure replaces
      // this with one actionable message.
      error.value = null;
    }

    // The socket id doubles as the DROP id: the guard above lets one socket report at most one drop,
    // so a request bearing a new id is a genuinely new drop — including the replacement socket that
    // dies while the very recovery which created it is still running.
    void resyncCoordinator.request(socketState.threadId, socketState.epoch, reason, socketState.id);
  }

  /**
   * Build the stream callbacks (message handler + completion/error handling) shared by the
   * send path and the resume path. Extracted so `resumeStreamIfActive` can re-attach the exact
   * same rendering pipeline to a reconnected, subscribe-only socket.
   */
  function buildStreamCallbacks(): {
    onMessage: (msg: Message) => void;
    onDone: () => void;
    onError: (err: string) => void;
  } {
    return {
      onMessage: handleMessage,
      onDone: () => {
        log.info('Stream completed', { transport: transport.value });

        // Mark all streaming messages as completed
        for (const message of messageIndex.value.values()) {
          if (message.isStreaming) {
            message.isStreaming = false;
            if (message.status === 'active') {
              message.status = 'completed';
            }
          }
        }

        finalize();
        isLoading.value = false;
      },
      onError: (err: string) => {
        log.error('Stream error', { error: err, transport: transport.value });
        error.value = err;
        isLoading.value = false;
      },
    };
  }

  /**
   * Settles when the open that STARTED most recently has finished — installed its connection, been
   * superseded, or failed. A send whose own open was superseded needs this: at the instant it finds
   * out, the winner is usually still in flight and `wsConnection` is null, so "is there a connection
   * I may hand my prompt to?" has no answer yet. Only ever AWAITED after an open has returned, so a
   * loser can never await its own (already settled) promise and deadlock.
   */
  let activeOpenSettled: Promise<void> = Promise.resolve();

  /**
   * Open (or replace) the persistent WebSocket for a thread and wire the stream callbacks.
   * Does NOT send anything — callers send afterwards (new message) or leave it subscribe-only
   * (resume). Shared by `sendMessageViaWebSocket` and `resumeStreamIfActive`.
   *
   * Returns the connection it INSTALLED, or `null` when a later open superseded this one while it
   * was in flight. Callers that need to talk on the socket they asked for (a send) must use the
   * returned reference rather than `wsConnection`, which by then belongs to the winner.
   */
  async function openStreamConnection(
    effectiveThreadId: string,
    callbacks: { onMessage: (msg: Message) => void; onDone: () => void; onError: (err: string) => void }
  ): Promise<import('@/api/wsClient').WebSocketConnection | null> {
    let markSettled!: () => void;
    activeOpenSettled = new Promise<void>((resolve) => {
      markSettled = resolve;
    });
    try {
      return await openStreamConnectionCore(effectiveThreadId, callbacks);
    } finally {
      // Every exit — installed, superseded, or thrown — must release whoever is waiting on us.
      markSettled();
    }
  }

  /**
   * The connection a superseded send may hand its prompt to. Losing the race releases the send's own
   * socket, but the prompt is already on screen: dropping it costs the user their message, while the
   * winner is serving the very stream the prompt belongs to. Safe ONLY when the winner is the
   * installed, currently-owned socket for exactly the route the send asked for — same thread, mode,
   * provider and workspace — and has not itself been declared dropped. Anything else (no winner, a
   * different route, a stale generation) leaves the send to fail loudly instead.
   */
  async function adoptOwnedConnection(
    effectiveThreadId: string,
    route: string
  ): Promise<import('@/api/wsClient').WebSocketConnection | null> {
    await activeOpenSettled;
    const connection = wsConnection;
    const state = activeSocketState;
    // A non-null `wsConnection` implies `activeSocketState` is ITS state (see beginRunOnActiveSocket),
    // and the connection's own `threadId` is checked too rather than trusted through that invariant.
    if (
      !connection ||
      !connection.isConnected ||
      connection.socket.readyState !== WebSocket.OPEN ||
      connection.threadId !== effectiveThreadId ||
      !state ||
      state.route !== route ||
      state.epoch !== conversationEpoch ||
      state.resyncRequested ||
      state.closedByClient
    ) {
      log.debug('No connection the superseded send may safely reuse', { threadId: effectiveThreadId });
      return null;
    }
    return connection;
  }

  async function openStreamConnectionCore(
    effectiveThreadId: string,
    callbacks: { onMessage: (msg: Message) => void; onDone: () => void; onError: (err: string) => void }
  ): Promise<import('@/api/wsClient').WebSocketConnection | null> {
    const currentModeId = getModeId?.();
    const currentProviderId = getProviderId?.() ?? null;
    const currentWorkspaceId = getWorkspaceId?.() ?? null;
    log.info('Creating new WebSocket connection', {
      threadId: effectiveThreadId,
      modeId: currentModeId,
      providerId: currentProviderId,
      workspaceId: currentWorkspaceId,
      recordEnabled,
    });

    // Close old connection if exists
    await closeActiveConnection();

    const { createWebSocketConnection } = await loadWsClient();

    // Lifecycle bookkeeping for THIS socket, captured by the callbacks below.
    const socketState: StreamSocketState = {
      id: `socket-${++socketSequence}`,
      threadId: effectiveThreadId,
      epoch: conversationEpoch,
      runId: null,
      doneReceived: false,
      resyncRequested: false,
      closedByClient: false,
      terminalError: false,
      route: streamRoute(effectiveThreadId, currentModeId, currentProviderId, currentWorkspaceId),
    };
    activeSocketState = socketState;

    const connection = await createWebSocketConnection({
      threadId: effectiveThreadId,
      modeId: currentModeId,
      providerId: currentProviderId,
      workspaceId: currentWorkspaceId,
      record: recordEnabled,
      ...callbacks,
      // Overrides the spread above. Every message kind reaches the transcript through here — text,
      // reasoning, tool calls and their results, run lifecycle, usage — so ONE gate covers them all
      // while an in-place `replay_truncated` fill is rewriting the index underneath us.
      onMessage: (msg: Message) => applyOrDefer(socketState, () => callbacks.onMessage(msg)),
      onAuthEvent: handleAuthEvent,
      onGenerationAbandoned: handleGenerationAbandoned,
      onSandboxSessionRefresh: async (deferred) => {
        if (sandboxRefreshThreadId !== effectiveThreadId || threadId.value !== effectiveThreadId) {
          clearSandboxRefreshState();
          return;
        }
        if (deferred) {
          sandboxRefreshDeferred = true;
          return;
        }

        const retry = pendingSandboxRefreshRetry;
        const fail = pendingSandboxRefreshFailure;
        pendingSandboxRefreshRetry = null;
        pendingSandboxRefreshFailure = null;
        await closeActiveConnection();
        if (retry) {
          await retry();
        } else {
          fail?.();
        }
      },
      onDone: () => {
        log.debug('WebSocket stream done signal received');
        // The run completed on this socket: a later close is a normal shutdown, not a drop, and the
        // resync attempt budget starts fresh for whatever comes next.
        socketState.doneReceived = true;
        // `doneReceived` is per-socket and safe to record either way, but the coordinator is SHARED:
        // only the live socket may reset it. A `done` from a socket whose recovery already opened a
        // replacement would otherwise abandon that replacement's in-flight rehydrate — and a socket
        // we have DECLARED DROPPED is equally not the live one, it merely stays `activeSocketState`
        // until its replacement is installed. Its own late `done` (a queued turn completing after
        // the drop) must not abandon the recovery it started; `resubscribe` consults authoritative
        // run state, so continuing is self-correcting when the run really did finish.
        if (activeSocketState === socketState && !socketState.resyncRequested) resyncCoordinator.invalidate();
        // `done` means "everything before me is rendered". Letting it through while the frames it
        // completes are still held by an in-place fill would mark the transcript finished and then
        // append a block nothing ever settles, so it queues behind them.
        applyOrDefer(socketState, callbacks.onDone);
        if (
          sandboxRefreshDeferred &&
          sandboxRefreshThreadId === effectiveThreadId &&
          threadId.value === effectiveThreadId
        ) {
          sandboxRefreshDeferred = false;
          const retry = pendingSandboxRefreshRetry;
          const fail = pendingSandboxRefreshFailure;
          pendingSandboxRefreshRetry = null;
          pendingSandboxRefreshFailure = null;
          void (async () => {
            await closeActiveConnection();
            if (retry) {
              await retry();
            } else {
              fail?.();
            }
          })();
        }
        // Keep connection open for next message (don't close)
      },
      onError: async (error, code) => {
        log.error('WebSocket error', { error, code });
        // A `code` means the SERVER sent a structured error frame: the run is over and the banner is
        // the answer, so the close that follows must not be recovered from. A code-less error is a
        // TRANSPORT failure — merely the first half of an abnormal drop that onClose recovers from.
        // Per-SOCKET state, so it is safe to record even for a socket that is no longer active.
        if (code) socketState.terminalError = true;
        // Everything below mutates state SHARED by every connection. A socket whose recovery already
        // opened a REPLACEMENT can still report its own death afterwards; acting on that here would
        // tear down the live replacement and put a banner on a run that is streaming perfectly well.
        // A socket we have already declared DROPPED is equally not the live one: it stays
        // `activeSocketState` until its replacement is installed, so without the second clause a
        // late transport error would repaint the banner and drop the spinner mid-recovery.
        if (activeSocketState !== socketState || socketState.resyncRequested) {
          log.debug('Ignoring an error reported by a socket that is no longer the live one');
          return;
        }
        clearSandboxRefreshState();
        // #246 defect 2: settle any in-flight submitClientToolResult() as a retryable error —
        // it will never receive an ack/error frame on a connection that just errored, and without
        // this the caller's promise (and e.g. QuestionRich's `finally`) would hang forever.
        settlePendingSubmissions({
          status: 'error',
          code: 'not_connected',
          message: error || 'WebSocket connection error',
        });
        callbacks.onError(error);
        // Cleanup — but NOT as a deliberate client close: wsClient emits onError then onClose for an
        // abnormal drop, and marking this socket client-closed would make that close unrecoverable.
        await closeActiveConnection({ deliberate: false });
      },
      // #246 defect 2: mirrors the onError settle above — a clean or unclean close with no ack ever
      // sent must also unlock a pending submission rather than leaking its resolver forever.
      onClose: (info) => {
        settlePendingSubmissions({
          status: 'error',
          code: 'not_connected',
          message: 'WebSocket connection closed',
        });
        // A close with no preceding `done` means the run is still out there — the server drops slow
        // consumers with a CLEAN close (reason `resync_required`), so `wasClean` proves nothing.
        requestStreamResync(socketState, info.reason || `closed_${info.code}`);
      },
      // The server announced the drop up front; recover without waiting for the close that follows.
      onStreamRecovery: (info) => {
        // `replay_truncated` is NOT a drop: only the run's already-published PREFIX is missing from the
        // replay buffer, and THIS socket still carries the live tail. Fill the hole from authoritative
        // history in place and keep the socket. Routing it through requestStreamResync would close and
        // reopen, land on the same still-truncated buffer, and be advised again for the rest of the
        // run — a reconnect storm.
        if (info.reason === 'replay_truncated') {
          fillTruncatedReplayHole(socketState);
          return;
        }
        requestStreamResync(socketState, info.reason || 'stream_recovery');
      },
      // #246: settle whichever submitClientToolResult() call is waiting on this toolCallId.
      // Wired here (not per-call) so ANY caller opening the connection — send, resume, or a
      // dedicated subscribe-only submit connection — gets ack/error routing uniformly.
      onClientToolResultAck: (toolCallId, duplicate) => {
        const resolve = pendingSubmissions.get(toolCallId);
        if (resolve) {
          pendingSubmissions.delete(toolCallId);
          resolve({ status: 'acked', duplicate });
        } else {
          log.debug('Received client_tool_result_ack for an untracked toolCallId', { toolCallId, duplicate });
        }
      },
      onClientToolResultError: (toolCallId, code, message) => {
        // A malformed inbound frame may arrive without a toolCallId; if exactly one submission is
        // in flight it's an unambiguous correlation, otherwise it can't be attributed safely.
        const id = toolCallId ?? (pendingSubmissions.size === 1 ? [...pendingSubmissions.keys()][0] : undefined);
        const resolve = id ? pendingSubmissions.get(id) : undefined;
        if (resolve && id) {
          pendingSubmissions.delete(id);
          resolve({ status: 'error', code, message });
        } else {
          log.warn('Received client_tool_result_error that could not be correlated to a pending submission', {
            toolCallId,
            code,
            message,
          });
        }
      },
    });

    // OWNERSHIP, not arrival order. `activeSocketState` is claimed synchronously above, so the
    // LAST open to start is the one the app wants; `wsConnection` is assigned only now, so without
    // this guard the last open to RESOLVE would win instead. Those differ whenever two opens
    // overlap — a recovery reopen racing a user send is the everyday case — and the loser would
    // clobber the winner's connection, leaving the winner's socket unreachable (and its prompt
    // delivered on a socket nobody is listening to).
    if (activeSocketState !== socketState) {
      log.debug('Discarding a stream connection that a newer open superseded while it was in flight');
      // We chose this close, so its `onClose` must not be mistaken for a drop and start a recovery
      // for a socket that never carried a run.
      socketState.closedByClient = true;
      const { closeWebSocketConnection } = await loadWsClient();
      closeWebSocketConnection(connection);
      return null;
    }
    wsConnection = connection;

    // The socket can be declared dead DURING its own open: a close (or a `stream_recovery` frame)
    // delivered before `createWebSocketConnection` hands the reference back finds `wsConnection`
    // still null, so `requestStreamResync` had nothing to detach. Honour it now that the reference
    // exists — otherwise the corpse stays installed as the active connection, every later recovery
    // attempt short-circuits on resumeStreamIfActive's "already connected" guard, and the spinner
    // never comes down.
    if (socketState.resyncRequested) {
      log.debug('Releasing a replacement socket that was dropped before it was installed');
      parkDroppedSocketClose(closeActiveConnection({ deliberate: false }));
      return null;
    }

    return connection;
  }

  /**
   * Send message via WebSocket (persistent or new connection)
   */
  async function sendMessageViaWebSocket(
    text: string,
    callbacks: { onMessage: (msg: Message) => void; onDone: () => void; onError: (err: string) => void },
    sandboxRefreshRetried = false
  ): Promise<void> {
    const effectiveThreadId = await ensureThreadId();

    sandboxRefreshThreadId = effectiveThreadId;
    pendingSandboxRefreshRetry = sandboxRefreshRetried
      ? null
      : () =>
          threadId.value === effectiveThreadId
            ? sendMessageViaWebSocket(text, callbacks, true)
            : Promise.resolve();
    pendingSandboxRefreshFailure = sandboxRefreshRetried
      ? () => callbacks.onError('The sandbox session changed again while reconnecting. Please retry.')
      : null;

    // Check if we have an open connection that belongs to the current thread.
    // A socket bound to a previously-viewed conversation must not be reused for a
    // different thread (e.g. after switching conversations); close it and fall
    // through to create a fresh connection with the current callbacks instead.
    if (
      wsConnection &&
      wsConnection.isConnected &&
      wsConnection.socket.readyState === WebSocket.OPEN &&
      wsConnection.threadId === threadId.value
    ) {
      log.info('Reusing existing WebSocket connection', {
        connectionId: wsConnection.connectionId,
        threadId: wsConnection.threadId
      });

      // The connection outlives the run it was opened for, so this send starts a NEW run on it.
      beginRunOnActiveSocket();

      // Send message on existing connection
      const { sendWebSocketMessage } = await loadWsClient();
      sendWebSocketMessage(wsConnection, text);
    } else {
      const route = streamRoute(effectiveThreadId, getModeId?.(), getProviderId?.(), getWorkspaceId?.());
      const opened = await openStreamConnection(effectiveThreadId, callbacks);
      // Send on the socket THIS call opened, never on whatever is installed now. When a later open
      // superseded ours the prompt is not lost: hand it to the winner, but only while the winner is
      // serving exactly the route we asked for. With no safe socket the send fails loudly and
      // `sendMessage`'s catch turns it into one actionable banner.
      const connection = opened ?? (await adoptOwnedConnection(effectiveThreadId, route));
      if (!connection) {
        throw new Error('The connection was replaced before the message could be sent. Please try again.');
      }
      // An adopted socket was opened to SUBSCRIBE to the stream; this prompt starts a new run on it,
      // exactly like the reuse path above.
      if (!opened) beginRunOnActiveSocket();

      // Send message on new connection
      const { sendWebSocketMessage } = await loadWsClient();
      sendWebSocketMessage(connection, text);
    }
  }

  /**
   * Ensure a live WebSocket for the current thread exists before submitting a client-tool result
   * (#246). Reuses the same open-connection check as `sendMessageViaWebSocket`; when there is none
   * (e.g. the run finished/disconnected while a question stayed unanswered) opens a fresh
   * subscribe-only connection — no message is sent, so this never raises `isLoading`/`isSending`.
   */
  async function ensureClientToolSubmitConnection(): Promise<void> {
    const effectiveThreadId = await ensureThreadId();
    if (
      wsConnection &&
      wsConnection.isConnected &&
      wsConnection.socket.readyState === WebSocket.OPEN &&
      wsConnection.threadId === effectiveThreadId
    ) {
      return;
    }
    await openStreamConnection(effectiveThreadId, buildStreamCallbacks());
  }

  /**
   * Submit the browser's answer for a deferred client tool call (#246, e.g. `AskUserQuestion`) —
   * the function `ChatLayout` provides via `SUBMIT_CLIENT_TOOL_RESULT` for `QuestionRich.vue` (and
   * any future rich tool component) to call. Reuses/opens the persistent WebSocket, sends
   * `{ $type: 'client_tool_result', toolCallId, result, isError? }`, and resolves once the
   * matching `client_tool_result_ack` / `client_tool_result_error` frame arrives (see the
   * `onClientToolResultAck`/`onClientToolResultError` handlers wired into every
   * `createWebSocketConnection` call in `openStreamConnection`). The resolved value itself always
   * arrives separately as an ordinary `ToolCallResultMessage` — this promise settles the SUBMIT
   * outcome only, not the answer's eventual rendering.
   */
  async function submitClientToolResult(
    toolCallId: string,
    result: string,
    isError?: boolean
  ): Promise<import('./useClientToolSubmit').ClientToolSubmitOutcome> {
    try {
      await ensureClientToolSubmitConnection();
    } catch (err) {
      return {
        status: 'error',
        code: 'not_connected',
        message: err instanceof Error ? err.message : 'Failed to open connection',
      };
    }
    if (!wsConnection) {
      return { status: 'error', code: 'not_connected', message: 'No active connection' };
    }
    const { sendClientToolResult } = await loadWsClient();
    return new Promise((resolve) => {
      pendingSubmissions.set(toolCallId, resolve);
      try {
        sendClientToolResult(wsConnection!, toolCallId, result, isError);
      } catch (err) {
        pendingSubmissions.delete(toolCallId);
        resolve({
          status: 'error',
          code: 'not_connected',
          message: err instanceof Error ? err.message : 'Failed to send',
        });
      }
    });
  }

  /**
   * Resume an in-flight stream after returning to a conversation (switch-back or refresh).
   *
   * The backend run keeps running after the client disconnects (the agent is pooled), so when a
   * conversation still has an in-flight run we re-open the WebSocket in subscribe-only mode (no
   * send). The backend replays the in-flight run's already-emitted messages and then continues
   * delivering live deltas, which merge with the persisted history just loaded (the merge key
   * kind-runId-generationId-messageOrderIdx dedups replay vs history). Without this, returning to
   * a streaming conversation showed the partial frozen at the last persisted point.
   */
  /**
   * Lower the streaming flags to their idle state. Called when a conversation switch (or new chat)
   * lands on a target with no resumable in-flight run, so the Send/Stop control returns to "Send"
   * (BUG 1). It is deliberately NOT done inside clearMessages: clearMessages runs at the START of
   * every switch, BEFORE the awaited loadMessagesFromBackend + resumeStreamIfActive, so lowering the
   * flag there produced a transient "idle" window mid switch-back — which a resumed run would flash
   * through (and which raced the E2E's stream-idle wait into reading the transcript before the
   * resumed final text arrived). Deciding idle-vs-streaming only once the run state is known keeps a
   * genuine resume continuously "streaming" with no flicker.
   */
  function markStreamIdle(): void {
    isLoading.value = false;
    isSending.value = false;
  }

  /**
   * Raise the streaming flag while a conversation switch loads and probes the target's run state.
   * Selecting a conversation runs clearMessages → loadMessagesFromBackend → resumeStreamIfActive; the
   * caller sets this right before the load so the Send/Stop control stays "Stop" continuously when the
   * target turns out to still be streaming (resumeStreamIfActive keeps it raised), instead of flashing
   * to "Send" during the awaited load and only snapping back to "Stop" on resume. A target that is
   * actually idle resolves to Send via markStreamIdle() in resumeStreamIfActive's no-run branches.
   */
  function markStreamLoading(): void {
    isLoading.value = true;
  }

  async function resumeStreamIfActive(existingThreadId: string): Promise<void> {
    // Only the WebSocket transport maintains a resumable live backend run.
    if (transport.value !== 'websocket') {
      markStreamIdle();
      return;
    }

    // Already streaming this thread on an open socket — nothing to resume.
    if (
      wsConnection &&
      wsConnection.isConnected &&
      wsConnection.socket.readyState === WebSocket.OPEN &&
      wsConnection.threadId === existingThreadId
    ) {
      return;
    }

    let runState;
    try {
      const { getRunState } = await import('@/api/conversationsApi');
      runState = await getRunState(existingThreadId);
    } catch (err) {
      log.warn('Failed to query run state for resume', { threadId: existingThreadId, error: err });
      markStreamIdle();
      return;
    }

    // The active conversation may have changed while getRunState was in flight (rapid switching).
    // Binding this thread's stream to whatever conversation is now current would contaminate it,
    // so abort if we've moved on. Do NOT touch the streaming flags here — a newer switch already
    // owns them; clearing them would stomp the conversation we just moved to.
    if (threadId.value !== existingThreadId) {
      log.debug('Thread changed during resume check; aborting', {
        requested: existingThreadId,
        current: threadId.value,
      });
      return;
    }

    if (!runState?.isInProgress) {
      // #246: the run itself may have finished (or was never in-flight from the server's point of
      // view) while a client tool question is still unanswered — e.g. resolution is deferred to a
      // human and the agent turn completed around it. Re-open a subscribe-only connection so
      // submitClientToolResult() has a live socket to answer on; deliberately stays idle (does NOT
      // call markStreamLoading/raise isLoading) since no run is actually streaming.
      if (hasPendingClientQuestion.value) {
        log.debug('No in-flight run, but a pending client question exists; opening a subscribe-only connection', {
          threadId: existingThreadId,
        });
        try {
          await ensureClientToolSubmitConnection();
        } catch (err) {
          log.warn('Failed to open connection for pending client question', { threadId: existingThreadId, error: err });
        }
        markStreamIdle();
        return;
      }
      log.debug('No in-flight run to resume', { threadId: existingThreadId });
      markStreamIdle();
      return;
    }

    log.info('Resuming in-flight stream', {
      threadId: existingThreadId,
      runId: runState.currentRunId,
    });

    // Re-align the content turn epoch with the just-rehydrated history BEFORE the replay. The backend
    // replays the in-flight run's already-emitted messages from the start, and those are the SAME
    // messages loadMessagesFromBackend just keyed. contentTurnSeqFor is stateful and was left advanced
    // by that reload; without resetting it here the replayed reasoning/text would re-key with a HIGHER
    // turn epoch than their rehydrated twins (…-t1 → …-t3, …-t2 → …-t4), fail to merge, and pile up as
    // duplicates at the bottom — scrambling multi-turn order (BUG 2). Resetting lets the replay
    // re-derive the SAME epoch sequence as the reload (both walk the run in production order), so each
    // replayed message merges in place with its twin. Tool calls are unaffected (their key carries
    // tool_call_id) — which is why only thinking/text scrambled. reset() clears the (already-empty)
    // merger accumulators so the update path re-aligns identically.
    resetContentTurnEpoch();
    reset();

    // Reflect the live run in the UI (spinner / stop button) while the replayed + live deltas arrive.
    isLoading.value = true;
    try {
      // No send: this is a subscribe-only resume.
      await openStreamConnection(existingThreadId, buildStreamCallbacks());
    } catch (err) {
      // A failure opening the resume socket must not leave the UI stuck "streaming" forever.
      isLoading.value = false;
      log.error('Failed to open resume connection', { threadId: existingThreadId, error: err });
    }
  }

  /**
   * Set the transport type (sse or websocket)
   */
  function setTransport(newTransport: TransportType): void {
    log.info('Changing transport', { from: transport.value, to: newTransport });
    transport.value = newTransport;
  }

  /**
   * Clear all messages and reset state
   */
  async function clearMessages(): Promise<void> {
    log.info('Clearing all messages');
    pendingMessages.value = [];
    messageIndex.value.clear();
    messageOrder.value = [];
    usage.value = null;
    cumulativeUsage.value = { promptTokens: 0, uncachedInputTokens: 0, completionTokens: 0, totalTokens: 0, cachedTokens: 0, cacheCreationTokens: 0 };
    cumulativeCost.value = { estimatedCostMicros: null, providerReportedCostMicros: null, currency: 'USD' };
    error.value = null;
    threadId.value = null;
    currentRunId.value = null;
    toolResults.value.clear();
    // NB: the streaming flags (isLoading/isSending) are deliberately NOT reset here. clearMessages
    // runs at the START of every switch — BEFORE the awaited loadMessagesFromBackend +
    // resumeStreamIfActive — so lowering them here flashed a transient "idle" through a switch-back
    // that then resumes (BUG 1's regression on the tool-pill resume path). Idle is decided once the
    // run state is known: markStreamIdle() in resumeStreamIfActive's no-run branches (switch to an
    // idle existing conversation) and in handleNewChat (a fresh chat is always idle).
    resetContentTurnEpoch();
    reset();
    clearSandboxRefreshState();
    beginConversationEpoch();

    // Close WebSocket connection
    await disconnectWebSocket();
  }
  
  /**
   * Disconnect persistent WebSocket connection
   */
  async function disconnectWebSocket(): Promise<void> {
    if (wsConnection) {
      log.info('Disconnecting WebSocket', { connectionId: wsConnection.connectionId });
      await closeActiveConnection();
    }
  }

  /**
   * Cancel the active stream (if any) without clearing message history.
   * Closes the active WebSocket so the server stops streaming, and marks
   * any in-flight streaming messages as completed so the UI returns to idle.
   */
  async function cancelStream(): Promise<void> {
    if (!isLoading.value && !isSending.value && !wsConnection) return;
    log.info('Cancelling active stream');

    // The user ended this run: nothing left to recover, and the next drop starts with a full budget.
    resyncCoordinator.invalidate();
    await disconnectWebSocket();
    clearSandboxRefreshState();

    for (const message of messageIndex.value.values()) {
      if (message.isStreaming) {
        message.isStreaming = false;
        if (message.status === 'active') {
          message.status = 'completed';
        }
      }
    }

    finalize();
    isLoading.value = false;
    isSending.value = false;
  }

  /**
   * Set thread ID externally (for conversation switching)
   */
  function setThreadId(newThreadId: string | null): void {
    log.info('Setting thread ID externally', { oldThreadId: threadId.value, newThreadId });
    if (threadId.value !== newThreadId) {
      clearSandboxRefreshState();
      // A different conversation is now current: any recovery in flight (or any close still to
      // land) belongs to the conversation we just left and must not touch this one.
      beginConversationEpoch();
    }
    threadId.value = newThreadId;
  }

  /**
   * Load messages from backend for an existing conversation.
   *
   * `preservePending` keeps the queue of not-yet-sent user prompts. Clearing it is right for the
   * default caller — a conversation SWITCH, where the queue belongs to the conversation being left —
   * but wrong for an in-place fill of the conversation still on screen (`replay_truncated`), whose
   * queued prompts are still going to be sent.
   */
  async function loadMessagesFromBackend(
    existingThreadId: string,
    options: { preservePending?: boolean } = {}
  ): Promise<void> {
    log.info('Loading messages from backend', { threadId: existingThreadId });

    // Everything below the await is DESTRUCTIVE (it wipes the message index and reassigns
    // threadId), so a load whose conversation the user has since left must discard its result
    // BEFORE applying it — checking afterwards would already have overwritten the new conversation.
    // A slow resync rehydrate of the previous thread landing after a switch is exactly this case.
    const epochAtEntry = conversationEpoch;
    const { loadConversationMessages } = await import('@/api/conversationsApi');
    const persistedMessages = await loadConversationMessages(existingThreadId);

    if (epochAtEntry !== conversationEpoch) {
      log.debug('Discarding history for a conversation the user has left', {
        threadId: existingThreadId,
        count: persistedMessages.length,
      });
      return;
    }

    log.debug('Loaded persisted messages', { count: persistedMessages.length });

    // Clear current state
    if (!options.preservePending) pendingMessages.value = [];
    messageIndex.value.clear();
    messageOrder.value = [];
    toolResults.value.clear();
    resetContentTurnEpoch();
    reset();

    // Set the thread ID
    threadId.value = existingThreadId;

    // Convert persisted messages to internal format
    for (const pm of persistedMessages) {
      try {
        const parsedMessage = JSON.parse(pm.messageJson) as Message;

        // Skip lifecycle and usage messages
        if (isRunAssignmentMessage(parsedMessage) ||
            isRunCompletedMessage(parsedMessage) ||
            isUsageMessage(parsedMessage)) {
          continue;
        }

        // Skip tool call results (they're attached to tool calls)
        if (isToolCallResultMessage(parsedMessage)) {
          // Store in toolResults map for lookup
          if (parsedMessage.tool_call_id) {
            toolResults.value.set(parsedMessage.tool_call_id, parsedMessage);
          }
          continue;
        }

        // Determine role
        const role: 'user' | 'assistant' = parsedMessage.role === 'user' ? 'user' : 'assistant';

        // Transform test instruction messages for display
        if (role === 'user' && isTextMessage(parsedMessage)) {
          const textMsg = parsedMessage as TextMessage;
          if (isTestInstruction(textMsg.text)) {
            textMsg.text = '🧪 Test instruction sent';
          }
        }

        // Ensure the parsed message carries the persisted identity fields so the
        // merge key matches what live streaming computes for the same logical message.
        parsedMessage.runId = parsedMessage.runId ?? pm.runId;
        parsedMessage.parentRunId = parsedMessage.parentRunId ?? pm.parentRunId ?? undefined;
        parsedMessage.generationId = parsedMessage.generationId ?? pm.generationId ?? undefined;
        parsedMessage.messageOrderIdx = parsedMessage.messageOrderIdx ?? pm.messageOrderIdx ?? undefined;

        // Index rehydrated messages by the same merge key used by live streaming
        // (kind-runId-generationId-messageOrderIdx) so a subsequent streaming update
        // sharing that identity merges in place instead of creating a duplicate bubble. Replay the
        // content turn epoch (BUG #8 + text interleaving) in persisted order so reloaded multi-turn
        // thinking/text renders as distinct, correctly-ordered blocks too, matching live streaming.
        const turnSeq = contentTurnSeqFor(parsedMessage);
        const mergeKey = getMergeKey(parsedMessage, turnSeq);

        // Create chat message
        const chatMessage: InternalChatMessage = {
          id: mergeKey,
          role,
          status: 'completed',
          content: parsedMessage,
          runId: pm.runId,
          parentRunId: pm.parentRunId,
          generationId: pm.generationId,
          messageOrderIdx: pm.messageOrderIdx,
          timestamp: pm.timestamp,
          isStreaming: false,
        };

        // Stream persistence can hold several records that collapse to one logical merge key
        // (e.g. an intermediate update record beside the finalizing message, same
        // run/generation/messageOrderIdx). Append to messageOrder only on FIRST insert; otherwise
        // overwrite the existing messageIndex entry in place so the final record wins WITHOUT
        // accumulating a duplicate key that would render/sort the same message multiple times.
        const isFirstInsert = !messageIndex.value.has(mergeKey);
        messageIndex.value.set(mergeKey, chatMessage);
        if (isFirstInsert) {
          messageOrder.value.push(mergeKey);
        }
      } catch (e) {
        log.warn('Failed to parse persisted message', { messageId: pm.id, error: e });
      }
    }

    // Attach tool results to tool calls
    for (const [toolCallId, result] of toolResults.value.entries()) {
      for (const chatMsg of messageIndex.value.values()) {
        if (isToolCallMessage(chatMsg.content)) {
          const toolCall = chatMsg.content as ToolCallMessage;
          if (toolCall.tool_call_id === toolCallId) {
            toolCall.result = result.result;
            break;
          }
        } else if (isToolsCallMessage(chatMsg.content)) {
          const toolsCall = chatMsg.content as ToolsCallMessage;
          const matchingToolCall = toolsCall.tool_calls?.find(tc => tc.tool_call_id === toolCallId);
          if (matchingToolCall) {
            matchingToolCall.result = result.result;
            break;
          }
        }
      }
    }

    // Restore the persisted usage banner (#196): the loop above skips UsageMessages, so read the
    // conversation's persisted aggregate — which includes sub-agent/workflow usage — and populate the
    // banner from it instead of leaving it at zero on reload.
    try {
      const { getConversationUsage } = await import('@/api/conversationsApi');
      const usageAggregate = await getConversationUsage(existingThreadId);
      // A SECOND round-trip, so the conversation can change under it exactly as it can under the
      // message load above — and the banner is GLOBAL, not per-conversation. Re-check before
      // painting, or the totals of the conversation the user left land on the one they opened.
      if (usageAggregate && epochAtEntry === conversationEpoch && threadId.value === existingThreadId) {
        applyAggregateToBanner(usageAggregate);
      }
    } catch (e) {
      log.warn('Failed to restore persisted usage banner', { error: String(e) });
    }

    log.info('Loaded messages into chat', {
      messageCount: messageIndex.value.size,
      toolResultCount: toolResults.value.size
    });
  }

  // Computed for exposing pending messages
  const pendingMessagesForQueue = computed(() => {
    return pendingMessages.value.map(msg => ({
      id: msg.id,
      content: msg.content as TextMessage,
      timestamp: msg.timestamp,
    }));
  });

  return {
    displayItems,
    isLoading,
    isSending,
    error,
    usage,
    cumulativeUsage,
    cumulativeCost,
    transport,
    threadId,
    currentRunId,
    toolResults,
    pendingMessages: pendingMessagesForQueue,
    pendingAuthRequests,
    dismissAuthRequest,
    sendMessage,
    clearMessages,
    cancelStream,
    setTransport,
    disconnectWebSocket,
    getResultForToolCall,
    setThreadId,
    loadMessagesFromBackend,
    resumeStreamIfActive,
    markStreamIdle,
    markStreamLoading,
    submitClientToolResult,
    hasPendingClientQuestion,
  };
}

function isRecordingEnabledFromPageQuery(): boolean {
  const recordValue = new URLSearchParams(window.location.search).get('record');
  if (!recordValue) return false;

  const normalized = recordValue.trim().toLowerCase();
  return normalized === '1' || normalized === 'true';
}
