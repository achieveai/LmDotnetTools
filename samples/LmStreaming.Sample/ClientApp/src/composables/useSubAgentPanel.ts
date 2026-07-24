import { ref, watch, onScopeDispose } from 'vue';
import type {
  Message,
  DisplayItem,
  ToolCallResultMessage,
  ToolCallMessage,
  ToolsCallMessage,
  RunAssignmentMessage,
} from '@/types';
import {
  isRunAssignmentMessage,
  isRunCompletedMessage,
  isUsageMessage,
  isToolCallResultMessage,
  isToolCallMessage,
  isToolsCallMessage,
  isTextMessage,
  isTextUpdateMessage,
  isReasoningMessage,
  isReasoningUpdateMessage,
  isToolsCallUpdateMessage,
  isToolCallUpdateMessage,
  isNotifyMessage,
  isServerToolUseMessage,
  isServerToolResultMessage,
  isTextWithCitationsMessage,
} from '@/types';
import { loadConversationMessages, type PersistedMessage } from '@/api/conversationsApi';
import { listSubAgents, type SubAgentSummary } from '@/api/subAgentsApi';
import { connectSubAgent } from '@/api/subAgentWsClient';
import { sendWebSocketMessage, closeWebSocketConnection, type WebSocketConnection } from '@/api/wsClient';
import { useMessageMerger } from './useMessageMerger';
import { getMergeKey } from './messageMergeKey';
import {
  serverToolUseToToolsCall,
  serverToolResultToToolCallResult,
  textWithCitationsToText,
} from './messageConversions';
import { buildDisplayItems, type DisplayableMessage } from './messageDisplay';
import { logger } from '@/utils';

const log = logger.forComponent('useSubAgentPanel');

const POLL_INTERVAL_MS = 3000;

/**
 * Upper bound on the subscribe-first live buffer. While a focus loads persisted history it BUFFERS
 * live frames to close the snapshot→subscribe gap. History loading is not abortable, so a
 * pathological child (or a slow history fetch) could grow this buffer without bound. When the buffer
 * reaches this many entries the composable ABANDONS the buffered-merge and instead reloads history
 * once the connection is established (see focusChild), reconciling anything persisted during the
 * overflow window. Documented tradeoff: frames that arrive after the reconcile snapshot but before
 * live handling resumes are recovered on the next refocus rather than mid-stream — the priority is a
 * bounded buffer with no silent, unbounded growth. Exported as an internal seam for testing.
 */
export const LIVE_BUFFER_MAX = 1000;

/**
 * Upper bound on overflow→reconcile cycles for a single focus. When the subscribe-first live buffer
 * overflows we abandon it, reload persisted history to reconcile, and drain a FRESH bounded buffer of
 * frames that arrived during the reload. A pathologically fast child could overflow that fresh buffer
 * again during each reload; we retry the reconcile at most this many times, then fall back to draining
 * whatever is currently buffered (bounded) rather than looping forever — a subsequent refocus fully
 * reconciles. Exported as an internal seam for testing.
 */
export const MAX_OVERFLOW_RECONCILES = 3;

/**
 * Presentation-only sub-agent panel: list a conversation's sub-agents, focus one to view its
 * transcript (persisted history + live stream), and send input to the focused child. Deliberately
 * decoupled from `useChat` — it maintains its OWN per-agent message index, merger, tool-result map
 * and connection so focusing a child never perturbs the parent chat's state.
 *
 * @param getParentThreadId resolves the parent conversation's thread id (or null when none is active).
 */
export function useSubAgentPanel(getParentThreadId: () => string | null) {
  // Public reactive surface.
  const children = ref<SubAgentSummary[]>([]);
  const focusedAgentId = ref<string | null>(null);
  const focusedDisplayItems = ref<DisplayItem[]>([]);
  const isFocusedStreaming = ref(false);
  // Last HTTP/connect failure surfaced to the view; cleared on the next successful refresh/focus.
  const error = ref<string | null>(null);

  // Per-focus state (rebuilt on every focusChild; never shared with useChat).
  let focusedIndex = new Map<string, DisplayableMessage>();
  let focusedOrder: string[] = [];
  const toolResults = ref<Map<string, ToolCallResultMessage>>(new Map());
  let childCurrentRunId: string | null = null;
  let focusedConnection: WebSocketConnection | null = null;
  let merger = useMessageMerger();
  // Monotonic supersession token. Each focusChild invocation claims the next value; a call whose
  // token is no longer the latest has been superseded (even by a concurrent re-focus of the SAME
  // agent) and must abandon its rebuild / close its late connection. Keyed on the token, not the
  // agentId, so two overlapping focusChild(sameAgent) calls can't both pass the guard.
  let focusSeq = 0;
  // Suppresses the onClose auto-resume when WE close the socket (unfocus / refocus), so only an
  // UNEXPECTED server/drop close triggers a resume.
  let intentionalClose = false;
  // One-shot auto-resume budget per user-initiated focus: the server closes the socket after a
  // backpressure drop expecting a reconnect+replay. We resume once; a second unexpected close ends
  // in a terminal (non-streaming) state rather than looping.
  let autoResumeUsed = false;

  // Lifecycle-generation model. TWO monotonically-increasing tokens supersede stale async work so a
  // parent switch, an unfocus (Back/unmount) or scope disposal can never let an in-flight
  // focus/refresh/reconnect adopt a socket or publish state after teardown:
  //   * `focusSeq` — focus-lifecycle epoch. Claimed by focusChild (AFTER its own teardown) and bumped
  //     by unfocusChild (hence also by refocus, parent-change invalidation and scope disposal, which
  //     all unfocus). Every await in focusChild re-checks it and, if superseded, closes any socket it
  //     just opened instead of adopting it.
  //   * `parentGen` — parent-list epoch. Bumped only when the observed parent changes (invalidation)
  //     or on disposal. Guards the `children.value` assignment so a late list response for a
  //     superseded parent is discarded and cannot overwrite the new parent's list.
  let parentGen = 0;
  // Per-request sequence for refreshChildren, independent of parentGen. Two overlapping refreshes for
  // the SAME parent share one parentGen, so parentGen alone cannot tell them apart: an older request
  // finishing last could overwrite a newer list or surface a stale error after a newer success. Each
  // refresh captures the next value and applies its result (or error) only if it is still the latest
  // issued, so out-of-order completions from the same parent resolve to the newest response.
  let refreshSeq = 0;
  // The parent thread id whose children/focus are currently applied. A refresh that observes a
  // different parent first invalidates (advance parentGen, unfocus, clear children) before loading.
  let lastAppliedParent: string | null = getParentThreadId();

  let pollTimer: ReturnType<typeof setInterval> | null = null;

  function errorMessage(e: unknown): string {
    return e instanceof Error ? e.message : String(e);
  }

  /**
   * Invalidate everything tied to the previous parent conversation before switching to `newParent`:
   * advance the parent epoch (so a late list response for the old parent is dropped), tear down any
   * focused child (close its socket, clear the transcript — this also bumps `focusSeq`, superseding a
   * pending focus), and clear the stale child list.
   */
  async function invalidateForParentChange(newParent: string | null): Promise<void> {
    parentGen++;
    lastAppliedParent = newParent;
    await unfocusChild();
    children.value = [];
  }

  /**
   * Refresh the sub-agent list for the active parent conversation. If the observed parent differs
   * from the last-applied one (the user switched the main conversation), first INVALIDATE so the
   * transcript/socket/input never stay attached to the old parent's child. No-op when there is no
   * parent (nothing to enumerate yet). Failures surface via {@link error} instead of rejecting, and a
   * response that returns after the parent changed again is discarded.
   */
  async function refreshChildren(): Promise<void> {
    const parent = getParentThreadId();
    if (parent !== lastAppliedParent) {
      await invalidateForParentChange(parent);
    }
    if (!parent) return;

    // Capture a per-request sequence so overlapping same-parent refreshes stay ordered: the latest
    // issued request always wins, regardless of which one's list/error settles last.
    const requestGen = parentGen;
    const requestSeq = ++refreshSeq;
    let list: SubAgentSummary[];
    try {
      list = await listSubAgents(parent);
    } catch (e) {
      // Only surface the failure if this is still the latest request for the current parent/epoch, so
      // a stale error cannot overwrite a newer success (or a newer error).
      if (parentGen === requestGen && requestSeq === refreshSeq && getParentThreadId() === parent) {
        log.error('Failed to list sub-agents', { parent, error: e });
        error.value = `Failed to list sub-agents: ${errorMessage(e)}`;
      }
      return;
    }
    // Discard a late response for a superseded parent/epoch or a superseded (older) same-parent
    // request so it can't overwrite the newest list.
    if (parentGen !== requestGen || requestSeq !== refreshSeq || getParentThreadId() !== parent) return;
    error.value = null;
    children.value = list;
  }

  /** Begin polling the sub-agent list every 3s (fires an immediate refresh on start). */
  function startPolling(): void {
    if (pollTimer !== null) return;
    void refreshChildren();
    pollTimer = setInterval(() => {
      void refreshChildren();
    }, POLL_INTERVAL_MS);
  }

  /** Stop polling the sub-agent list. */
  function stopPolling(): void {
    if (pollTimer !== null) {
      clearInterval(pollTimer);
      pollTimer = null;
    }
  }

  /**
   * Rebuild the focused transcript's display items from the per-agent index in arrival order,
   * skipping pending entries (children never have any). Reuses the SAME pill-grouping used by the
   * parent chat via the shared {@link buildDisplayItems}.
   */
  function rebuildFocusedDisplayItems(): void {
    const ordered: DisplayableMessage[] = [];
    for (const key of focusedOrder) {
      const m = focusedIndex.get(key);
      if (m && m.status !== 'pending') {
        ordered.push(m);
      }
    }
    focusedDisplayItems.value = buildDisplayItems(ordered);
  }

  /** Clear the per-focus message state and its merger/tool-results (used on focus + unfocus). */
  function resetFocusState(): void {
    focusedIndex = new Map();
    focusedOrder = [];
    toolResults.value = new Map();
    childCurrentRunId = null;
    focusedDisplayItems.value = [];
    merger.reset();
  }

  /**
   * Insert or update a message in the per-agent index by its shared merge key, then rebuild the
   * transcript. Mirrors useChat's upsert so a live delta merges in place with its rehydrated twin.
   */
  function upsertFocusedMessage(mergeKey: string, content: Message, source: Message, isComplete: boolean): void {
    const existing = focusedIndex.get(mergeKey);
    if (!existing) {
      focusedIndex.set(mergeKey, {
        id: mergeKey,
        role: 'assistant',
        status: isComplete ? 'completed' : 'active',
        content,
        runId: source.runId,
        parentRunId: source.parentRunId,
        messageOrderIdx: source.messageOrderIdx,
        timestamp: Date.now(),
      });
      focusedOrder.push(mergeKey);
    } else {
      existing.content = content;
      existing.messageOrderIdx = source.messageOrderIdx ?? existing.messageOrderIdx;
      if (isComplete) {
        existing.status = 'completed';
      }
    }
    rebuildFocusedDisplayItems();
  }

  /** Record a tool result and attach it to any matching tool call already in the index. */
  function captureToolResult(result: ToolCallResultMessage): void {
    if (!result.tool_call_id) return;
    toolResults.value.set(result.tool_call_id, result);
    for (const chatMsg of focusedIndex.values()) {
      if (isToolCallMessage(chatMsg.content)) {
        const toolCall = chatMsg.content as ToolCallMessage;
        if (toolCall.tool_call_id === result.tool_call_id) {
          toolCall.result = result.result;
          break;
        }
      } else if (isToolsCallMessage(chatMsg.content)) {
        const toolsCall = chatMsg.content as ToolsCallMessage;
        const match = toolsCall.tool_calls?.find((tc) => tc.tool_call_id === result.tool_call_id);
        if (match) {
          match.result = result.result;
          break;
        }
      }
    }
  }

  /**
   * Attach every captured tool result to its matching tool call already in the index. Used after a
   * (re)hydrate pass so a result persisted before its call is rendered still resolves the pill.
   */
  function attachPersistedToolResults(): void {
    for (const [id, result] of toolResults.value.entries()) {
      for (const chatMsg of focusedIndex.values()) {
        if (isToolCallMessage(chatMsg.content) && (chatMsg.content as ToolCallMessage).tool_call_id === id) {
          (chatMsg.content as ToolCallMessage).result = result.result;
          break;
        } else if (isToolsCallMessage(chatMsg.content)) {
          const match = (chatMsg.content as ToolsCallMessage).tool_calls?.find((tc) => tc.tool_call_id === id);
          if (match) {
            match.result = result.result;
            break;
          }
        }
      }
    }
  }

  /**
   * Handle a live message from the focused child's stream. Mirrors the essentials of
   * useChat.handleMessage: track the run id from run_assignment, capture tool results, and merge
   * content deltas by the shared merge key.
   *
   * NOTE: sub-agents are streamed with a per-turn generationId (server-minted) and have NO
   * transcripts persisted before that fix, so the legacy contentTurnEpoch defense is intentionally
   * OMITTED here — turnSeq is always 0. The runId-stamping (below) and the tool_call_id
   * disambiguation baked into getMergeKey are still REQUIRED and preserved.
   */
  function handleChildMessage(msg: Message): void {
    if (isRunAssignmentMessage(msg)) {
      childCurrentRunId = (msg as RunAssignmentMessage).Assignment?.runId ?? childCurrentRunId;
      return;
    }
    if (isRunCompletedMessage(msg) || isUsageMessage(msg)) {
      // Lifecycle/usage frames do not render in the child transcript; onDone/onError drive streaming.
      return;
    }
    if (isToolCallResultMessage(msg)) {
      captureToolResult(msg);
      return;
    }

    // Provider-side frames (server-tool use/result, citation text) are not renderable by
    // buildDisplayItems in their raw shape — the pill/text grouping only understands the canonical
    // ToolsCall / ToolCallResult / Text shapes. Normalize them to those shapes here (via the shared
    // converters) so a focused child transcript renders and merges them identically to the parent
    // chat. A server_tool_result is a result frame: capture it like a plain tool result (it attaches
    // to its tool-use pill and resolves via getResultForToolCall). A server_tool_use / citation text
    // is content: normalize it, then fall through to the same stamp/merge path as any other frame.
    if (isServerToolResultMessage(msg)) {
      captureToolResult(serverToolResultToToolCallResult(msg));
      return;
    }
    let normalized: Message = msg;
    if (isServerToolUseMessage(msg)) {
      normalized = serverToolUseToToolsCall(msg);
    } else if (isTextWithCitationsMessage(msg)) {
      normalized = textWithCitationsToText(msg);
    }

    // Stamp the active run's id onto runId-less live content BEFORE keying. On the wire only
    // run_assignment carries a runId; finalized tool_call / tool_call_result / text arrive
    // runId-less. The rehydrated persisted twin carries the real run id, so without this stamp the
    // live copy keys to 'default' and duplicates instead of merging (the frozen-pill bug).
    let stamped = normalized;
    if (!stamped.runId && childCurrentRunId) {
      stamped = { ...stamped, runId: childCurrentRunId };
    }

    const isUpdate =
      isTextUpdateMessage(stamped) || isReasoningUpdateMessage(stamped) ||
      isToolsCallUpdateMessage(stamped) || isToolCallUpdateMessage(stamped);
    const isComplete =
      isTextMessage(stamped) || isReasoningMessage(stamped) || isToolsCallMessage(stamped) ||
      isToolCallMessage(stamped) || isNotifyMessage(stamped);

    if (!isUpdate && !isComplete) {
      log.debug('Skipping unknown child message type', { type: stamped.$type });
      return;
    }

    const merged = isUpdate ? merger.processUpdate(stamped, 0) : stamped;
    const mergeKey = getMergeKey(stamped, 0);
    upsertFocusedMessage(mergeKey, merged, stamped, isComplete);
  }

  /** Convert a persisted child message into the index, mirroring useChat.loadMessagesFromBackend. */
  function rehydratePersisted(pm: PersistedMessage): void {
    let parsed: Message;
    try {
      parsed = JSON.parse(pm.messageJson) as Message;
    } catch (e) {
      log.warn('Failed to parse persisted child message', { messageId: pm.id, error: e });
      return;
    }

    if (isRunAssignmentMessage(parsed) || isRunCompletedMessage(parsed) || isUsageMessage(parsed)) {
      return;
    }
    if (isToolCallResultMessage(parsed)) {
      if (parsed.tool_call_id) {
        toolResults.value.set(parsed.tool_call_id, parsed);
      }
      return;
    }

    // Provider-side frames persisted from history need the same normalization the live path applies,
    // so a rehydrated server_tool_use/result/citation renders (and merges with its live twin) exactly
    // like the parent chat. A persisted server_tool_result is captured like a plain tool result;
    // server_tool_use / citation text is converted in place and continues through identity stamping.
    if (isServerToolResultMessage(parsed)) {
      const converted = serverToolResultToToolCallResult(parsed);
      if (converted.tool_call_id) {
        toolResults.value.set(converted.tool_call_id, converted);
      }
      return;
    }
    if (isServerToolUseMessage(parsed)) {
      parsed = serverToolUseToToolsCall(parsed);
    } else if (isTextWithCitationsMessage(parsed)) {
      parsed = textWithCitationsToText(parsed);
    }

    // Stamp persisted identity so the merge key matches what live streaming computes.
    parsed.runId = parsed.runId ?? pm.runId;
    parsed.parentRunId = parsed.parentRunId ?? pm.parentRunId ?? undefined;
    parsed.generationId = parsed.generationId ?? pm.generationId ?? undefined;
    parsed.messageOrderIdx = parsed.messageOrderIdx ?? pm.messageOrderIdx ?? undefined;

    const role: 'user' | 'assistant' = parsed.role === 'user' ? 'user' : 'assistant';
    const mergeKey = getMergeKey(parsed, 0);
    const isFirstInsert = !focusedIndex.has(mergeKey);
    focusedIndex.set(mergeKey, {
      id: mergeKey,
      role,
      status: 'completed',
      content: parsed,
      runId: pm.runId,
      parentRunId: pm.parentRunId,
      messageOrderIdx: pm.messageOrderIdx,
      timestamp: pm.timestamp,
    });
    if (isFirstInsert) {
      focusedOrder.push(mergeKey);
    }
  }

  /**
   * Focus a sub-agent: tear down any prior focus, open the live stream (buffering), load the child's
   * persisted transcript, then drain the buffered live messages on top. Opening the socket BEFORE the
   * history fetch closes the snapshot→subscribe gap; history merges with the live stream by the shared
   * merge key (runId-stamped), so a still-running child resumes without duplicating pills. Any await
   * re-checks the focus token: a supersession (refocus / unfocus / parent switch / disposal) closes a
   * just-opened socket instead of adopting it, and a connect/history failure clears the focus state
   * and surfaces {@link error}.
   *
   * @param agentId the child to focus.
   * @param isAutoResume internal: true when invoked by the onClose auto-resume (a backpressure-drop
   *   reconnect), so it does NOT reset the one-shot resume budget. User-initiated focus omits it.
   */
  async function focusChild(agentId: string, isAutoResume = false): Promise<void> {
    if (!isAutoResume) {
      autoResumeUsed = false;
    }
    // Tear down any prior focus FIRST; unfocusChild bumps focusSeq, so claim our token AFTER it so we
    // don't immediately supersede ourselves.
    await unfocusChild();
    const token = ++focusSeq;
    // unfocusChild set intentionalClose to close the prior socket; clear it so the NEW connection's
    // close is treated as unexpected (and can auto-resume) rather than suppressed.
    intentionalClose = false;

    const summary = children.value.find((c) => c.agentId === agentId);
    if (!summary) {
      log.warn('focusChild: no summary for agent', { agentId });
      return;
    }

    const parent = getParentThreadId();
    if (!parent) {
      log.warn('focusChild: no parent thread id', { agentId });
      return;
    }

    focusedAgentId.value = agentId;
    merger = useMessageMerger();
    resetFocusState();
    error.value = null;

    // Live/history handoff: open the subscription FIRST and BUFFER incoming live messages, THEN
    // load persisted history, then drain the buffer on top (merge/dedup by the shared key). This
    // closes the REST-snapshot→subscribe gap — a child message emitted after the history snapshot but
    // before the subscription is buffered here rather than lost until a refocus.
    let liveBuffer: Message[] = [];
    let bufferingLive = true;
    // Set when the live buffer overflows while history is still loading. Rather than
    // permanently DROPPING later frames (which lost transcript + could discard run_assignment), we
    // abandon the current buffer to a FRESH bounded one and reconcile via a post-connect history
    // reload; this flag signals that reload is needed and is re-set if the fresh buffer overflows too.
    let liveBufferOverflowed = false;
    // Set by THIS focus's onClose when its socket closes unexpectedly. After the
    // history-load await we must not adopt a connection that already closed — even when the one-shot
    // auto-resume budget is spent, in which case onClose does NOT advance focusSeq to supersede us.
    let socketClosedDuringFocus = false;
    // Set when THIS focus sees a structured application error (onError with a `code`, e.g.
    // subagent_unavailable / subagent_stream_failed / relay_failed). Such errors are terminal for
    // auto-resume purposes — a subsequent close must NOT silently retry the child and clear the error.
    let terminalErrorSeen = false;
    // Set once the freshly-connected socket is registered as focusedConnection (provisional
    // ownership, BEFORE the non-abortable history load). Distinguishes "teardown already closed our
    // socket" (skip a redundant close) from "superseded before we owned it" (we must close it).
    let provisionalOwned = false;
    let openedConnection: WebSocketConnection | null = null;

    try {
      const connection = await connectSubAgent(parent, agentId, {
        onMessage: (m: Message) => {
          // Ignore late frames for a superseded focus.
          if (focusSeq !== token) return;
          // Preserve the active run identity INDEPENDENTLY of the content buffer. Only
          // run_assignment carries a runId on the wire; if it were lost in an overflow buffer clear,
          // later runId-less content would key under 'default' and diverge from its persisted twin.
          // Track it here so it survives any buffer reset below.
          if (isRunAssignmentMessage(m)) {
            childCurrentRunId = (m as RunAssignmentMessage).Assignment?.runId ?? childCurrentRunId;
          }
          if (!bufferingLive) {
            handleChildMessage(m);
            return;
          }
          liveBuffer.push(m);
          if (liveBuffer.length > LIVE_BUFFER_MAX) {
            // Overflow — abandon the current bounded buffer (its frames are captured by the
            // post-connect reconciliation history reload) and START A FRESH bounded buffer so frames
            // arriving during reconciliation are NOT dropped into a gap. Repeated overflow re-sets the
            // flag and triggers another reconcile pass (bounded by MAX_OVERFLOW_RECONCILES).
            liveBufferOverflowed = true;
            liveBuffer = [];
            log.warn('Sub-agent live buffer overflow; reconciling via history reload after connect', {
              agentId,
              max: LIVE_BUFFER_MAX,
            });
          }
        },
        onDone: () => {
          if (focusSeq === token) isFocusedStreaming.value = false;
        },
        onError: (err: string, code?: string) => {
          if (focusSeq !== token) return;
          log.error('Sub-agent stream error', { agentId, code });
          isFocusedStreaming.value = false;
          // A structured error code marks a terminal application failure — record it so a
          // following close does not auto-resume (which would retry an unavailable child + clear
          // this error). Parse/socket errors arrive without a code and stay auto-resume-eligible.
          if (code) {
            terminalErrorSeen = true;
          }
          // Copy the failure into the public error ref so the panel's error banner shows
          // feedback (a relay_failed / subagent_unavailable / parse / socket error). The focus-token
          // guard above already prevents a stale focus from clobbering a newer focus's error.
          error.value = err || 'Sub-agent stream error.';
        },
        onClose: (info) => {
          // Ignore closes for a superseded focus, or a close WE initiated (unfocus/refocus).
          if (focusSeq !== token || intentionalClose) {
            return;
          }
          // Record the close so a pending history-load await does not adopt this now-dead socket
          // even if the auto-resume budget below is already spent.
          socketClosedDuringFocus = true;
          // Drop the dead socket and stop the spinner so the view can't hang and sendToFocusedChild
          // can't post to a dead socket.
          focusedConnection = null;
          isFocusedStreaming.value = false;
          // Auto-resume ONLY for a recoverable backpressure drop — a CLEAN close with NO terminal
          // application error seen this focus. A terminal error or an abnormal (!wasClean) close is
          // non-recoverable: leave the error surfaced, spinner off, connection nulled, and do NOT loop.
          if (terminalErrorSeen || !info.wasClean) {
            log.info('Sub-agent focus socket closed terminally; not auto-resuming', {
              agentId,
              wasClean: info.wasClean,
              terminalErrorSeen,
            });
            return;
          }
          // The server closes after a backpressure drop expecting a reconnect+replay, so resume ONCE;
          // a second close is terminal (budget spent).
          if (!autoResumeUsed) {
            autoResumeUsed = true;
            log.info('Sub-agent focus socket closed unexpectedly; resuming once', { agentId });
            void focusChild(agentId, true).catch((e) => {
              log.error('Sub-agent auto-resume failed', { agentId, error: e });
            });
          }
        },
      });
      openedConnection = connection;

      // Lifecycle guard: a newer focus, an unfocus, a parent switch or disposal superseded us while
      // the socket was opening. We do NOT yet own it (provisional assignment is below), so the
      // superseding teardown could not have closed it — close it ourselves and bail.
      if (focusSeq !== token) {
        log.debug('focusChild superseded during connect; closing stale connection', { agentId });
        closeWebSocketConnection(connection);
        return;
      }

      // Register PROVISIONAL ownership IMMEDIATELY — before the non-abortable history load — so an
      // unfocus / parent-switch / scope-dispose can close this socket instead of leaking it while the
      // history request is in flight. From here on the superseding teardown owns the close, so the
      // post-await guards below must NOT close the socket again (double-close).
      focusedConnection = connection;
      provisionalOwned = true;

      // Load persisted history (live messages arriving now are buffered above).
      const persisted = await loadConversationMessages(summary.threadId);
      if (focusSeq !== token) {
        // We registered provisional ownership before this await, so the superseding teardown
        // (unfocus / parent-switch / dispose) already closed the socket and nulled focusedConnection.
        // Do NOT close again (double-close) — just abandon this stale focus.
        log.debug('focusChild superseded during history load; teardown owns the socket', { agentId });
        return;
      }
      // The socket may have closed terminally DURING the history-load await. Do NOT adopt
      // the dead connection nor drain the buffer onto it — the onClose handler already stopped the
      // spinner and (when budget remained) started the one-shot auto-resume, so just bail without
      // double-resuming.
      if (socketClosedDuringFocus || connection.socket.readyState !== WebSocket.OPEN) {
        // Exception: a COMPLETED read-only child (e.g. a workflow delegate whose controller loop was
        // released, so the live stream reports subagent_unavailable) has no socket to adopt, but its
        // transcript still lives in the store. Render that persisted history read-only instead of
        // leaving the tab blank + a scary "unavailable" banner. Scoped to the terminal-error case
        // (terminalErrorSeen ⇒ no auto-resume is pending to render it later) with actual history.
        if (terminalErrorSeen && persisted.length > 0) {
          for (const pm of persisted) {
            rehydratePersisted(pm);
          }
          attachPersistedToolResults();
          rebuildFocusedDisplayItems();
          error.value = null;
          log.debug('focusChild: rendered completed child from persisted history (no live socket)', {
            agentId,
            count: persisted.length,
          });
          return;
        }
        log.debug('focusChild: socket closed during history load; not adopting dead connection', { agentId });
        return;
      }
      for (const pm of persisted) {
        rehydratePersisted(pm);
      }
      // Attach any persisted tool results to their tool calls.
      attachPersistedToolResults();
      rebuildFocusedDisplayItems();

      // If the live buffer overflowed while history was loading, the buffered-merge was
      // abandoned to a FRESH bounded buffer. Reconcile by reloading history (the shared merge key
      // dedups it against what we rehydrated) then drain the fresh buffer on top. A fast child can
      // overflow the fresh buffer again during the reload, so retry the reconcile up to
      // MAX_OVERFLOW_RECONCILES times; each pass re-checks the flag, which onMessage re-sets on a new
      // overflow. This closes the loss window (frames during reconciliation are buffered, not dropped).
      let reconcileAttempts = 0;
      while (liveBufferOverflowed && reconcileAttempts < MAX_OVERFLOW_RECONCILES) {
        liveBufferOverflowed = false; // a fresh overflow DURING this reload will re-set it
        reconcileAttempts += 1;
        const reloaded = await loadConversationMessages(summary.threadId);
        if (focusSeq !== token) {
          // Superseded — the teardown owns the provisionally-registered socket; do not double-close.
          log.debug('focusChild superseded during reconcile reload; teardown owns the socket', { agentId });
          return;
        }
        if (socketClosedDuringFocus || connection.socket.readyState !== WebSocket.OPEN) {
          log.debug('focusChild: socket closed during reconcile reload; not adopting dead connection', { agentId });
          return;
        }
        for (const pm of reloaded) {
          rehydratePersisted(pm);
        }
        attachPersistedToolResults();
        rebuildFocusedDisplayItems();
      }
      if (liveBufferOverflowed) {
        // Fallback: the fresh buffer kept overflowing past the reconcile budget. Do NOT silently drop
        // — keep the last reconciled snapshot and drain whatever is currently buffered (bounded) on
        // top below; a subsequent refocus fully reconciles. Documented tradeoff (see MAX_OVERFLOW_RECONCILES).
        log.warn('Sub-agent live buffer still overflowing after reconcile budget; draining current buffer', {
          agentId,
          attempts: reconcileAttempts,
        });
        liveBufferOverflowed = false;
      }

      // Drain buffered live messages on top of history; the shared merge key dedups a buffered delta
      // against its rehydrated twin (no duplicate pills). Flip buffering off first (synchronously) so
      // a message delivered during the drain is handled directly rather than re-buffered.
      bufferingLive = false;
      const buffered = liveBuffer.splice(0, liveBuffer.length);
      for (const m of buffered) {
        handleChildMessage(m);
      }
      rebuildFocusedDisplayItems();

      // Adopt the connection now that history + gap messages are in place (already provisionally
      // owned since connect; reaffirm and start the spinner).
      focusedConnection = connection;
      isFocusedStreaming.value = true;
    } catch (e) {
      // A superseding focus/unfocus/parent-switch already owns the visible state — don't clobber it.
      if (focusSeq !== token) {
        // Only close the socket ourselves if we never registered provisional ownership (superseded
        // before the history await). Once provisionally owned, the superseding teardown already closed
        // it — closing again would be a double-close.
        if (openedConnection && !provisionalOwned) closeWebSocketConnection(openedConnection);
        return;
      }
      // Failure for the CURRENT focus (connect/history rejected): clear the half-focused state, stop
      // the spinner, close any opened socket, and surface the error.
      log.error('focusChild failed', { agentId, error: e });
      if (openedConnection) {
        intentionalClose = true;
        closeWebSocketConnection(openedConnection);
      }
      focusedConnection = null;
      focusedAgentId.value = null;
      isFocusedStreaming.value = false;
      resetFocusState();
      error.value = `Failed to focus sub-agent: ${errorMessage(e)}`;
    }
  }

  /**
   * Close the focused child's stream and clear all per-focus state. Advances `focusSeq` so any focus
   * still in flight (history-load / socket-open) is superseded and closes its late socket instead of
   * adopting it after this teardown.
   */
  async function unfocusChild(): Promise<void> {
    focusSeq++;
    if (focusedConnection) {
      // Mark the close intentional so its onClose does not trigger an auto-resume.
      intentionalClose = true;
      closeWebSocketConnection(focusedConnection);
      focusedConnection = null;
    }
    focusedAgentId.value = null;
    isFocusedStreaming.value = false;
    resetFocusState();
  }

  /** Send text input to the focused child over its live stream. */
  function sendToFocusedChild(text: string): void {
    if (!focusedConnection || focusedConnection.socket.readyState !== WebSocket.OPEN) {
      log.warn('sendToFocusedChild: no open focused child connection');
      return;
    }
    sendWebSocketMessage(focusedConnection, text);
  }

  /** Look up a captured tool result by tool_call_id (for resolving a focused pill). */
  function getResultForToolCall(toolCallId: string | null | undefined): ToolCallResultMessage | null {
    if (!toolCallId) return null;
    return toolResults.value.get(toolCallId) || null;
  }

  // Immediate parent-change invalidation for reactive callers (the panel passes `() => props
  // .parentThreadId`): when the observed parent id changes, refresh (which detects the change and
  // invalidates) without waiting for the next 3s poll. Constant/non-reactive getters (unit tests)
  // never trigger this. `refreshChildren` swallows its own failures, so this is safe fire-and-forget.
  const stopParentWatch = watch(
    () => getParentThreadId(),
    () => {
      void refreshChildren();
    }
  );

  // Auto-cleanup: when the host component/effect-scope unmounts, tear down the poll interval and the
  // focused child's WebSocket so no timers or sockets leak, and advance the epochs so any in-flight
  // focus/refresh becomes a no-op that closes (not adopts) its socket. `failSilently` avoids a dev
  // warning when the composable is invoked outside an active scope (e.g. unit tests calling it
  // directly).
  onScopeDispose(() => {
    stopParentWatch();
    parentGen++;
    stopPolling();
    void unfocusChild();
  }, true);

  return {
    children,
    focusedAgentId,
    focusedDisplayItems,
    isFocusedStreaming,
    error,
    startPolling,
    stopPolling,
    refreshChildren,
    focusChild,
    unfocusChild,
    sendToFocusedChild,
    getResultForToolCall,
  };
}
