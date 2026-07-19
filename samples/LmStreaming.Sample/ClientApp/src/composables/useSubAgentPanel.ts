import { ref } from 'vue';
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
} from '@/types';
import { loadConversationMessages, type PersistedMessage } from '@/api/conversationsApi';
import { listSubAgents, type SubAgentSummary } from '@/api/subAgentsApi';
import { connectSubAgent } from '@/api/subAgentWsClient';
import { sendWebSocketMessage, closeWebSocketConnection, type WebSocketConnection } from '@/api/wsClient';
import { useMessageMerger } from './useMessageMerger';
import { getMergeKey } from './messageMergeKey';
import { buildDisplayItems, type DisplayableMessage } from './messageDisplay';
import { logger } from '@/utils';

const log = logger.forComponent('useSubAgentPanel');

const POLL_INTERVAL_MS = 3000;

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

  // Per-focus state (rebuilt on every focusChild; never shared with useChat).
  let focusedIndex = new Map<string, DisplayableMessage>();
  let focusedOrder: string[] = [];
  const toolResults = ref<Map<string, ToolCallResultMessage>>(new Map());
  let childCurrentRunId: string | null = null;
  let focusedConnection: WebSocketConnection | null = null;
  let merger = useMessageMerger();

  let pollTimer: ReturnType<typeof setInterval> | null = null;

  /**
   * Refresh the sub-agent list for the active parent conversation. No-op when there is no parent
   * (nothing to enumerate yet).
   */
  async function refreshChildren(): Promise<void> {
    const parent = getParentThreadId();
    if (!parent) return;
    children.value = await listSubAgents(parent);
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

    // Stamp the active run's id onto runId-less live content BEFORE keying. On the wire only
    // run_assignment carries a runId; finalized tool_call / tool_call_result / text arrive
    // runId-less. The rehydrated persisted twin carries the real run id, so without this stamp the
    // live copy keys to 'default' and duplicates instead of merging (the frozen-pill bug).
    let stamped = msg;
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
   * Focus a sub-agent: tear down any prior focus, load the child's persisted transcript, then open a
   * live stream. History merges with the live stream by the shared merge key (runId-stamped), so a
   * still-running child resumes without duplicating pills.
   */
  async function focusChild(agentId: string): Promise<void> {
    await unfocusChild();

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

    // Load persisted history first so live deltas merge in place with their rehydrated twins.
    const persisted = await loadConversationMessages(summary.threadId);
    for (const pm of persisted) {
      rehydratePersisted(pm);
    }
    // Attach any persisted tool results to their tool calls.
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
    rebuildFocusedDisplayItems();

    // Open the live child stream.
    focusedConnection = await connectSubAgent(parent, agentId, {
      onMessage: handleChildMessage,
      onDone: () => {
        isFocusedStreaming.value = false;
      },
      onError: (err: string) => {
        log.error('Sub-agent stream error', { agentId, error: err });
        isFocusedStreaming.value = false;
      },
    });
    isFocusedStreaming.value = true;
  }

  /** Close the focused child's stream and clear all per-focus state. */
  async function unfocusChild(): Promise<void> {
    if (focusedConnection) {
      closeWebSocketConnection(focusedConnection);
      focusedConnection = null;
    }
    focusedAgentId.value = null;
    isFocusedStreaming.value = false;
    resetFocusState();
  }

  /** Send text input to the focused child over its live stream. */
  function sendToFocusedChild(text: string): void {
    if (!focusedConnection) {
      log.warn('sendToFocusedChild: no focused child connection');
      return;
    }
    sendWebSocketMessage(focusedConnection, text);
  }

  /** Look up a captured tool result by tool_call_id (for resolving a focused pill). */
  function getResultForToolCall(toolCallId: string | null | undefined): ToolCallResultMessage | null {
    if (!toolCallId) return null;
    return toolResults.value.get(toolCallId) || null;
  }

  return {
    children,
    focusedAgentId,
    focusedDisplayItems,
    isFocusedStreaming,
    startPolling,
    stopPolling,
    refreshChildren,
    focusChild,
    unfocusChild,
    sendToFocusedChild,
    getResultForToolCall,
  };
}
