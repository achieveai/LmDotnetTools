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
  isConversationUsageMessage,
  normalizeReasoningVisibility,
} from '@/types';
import { sendChatMessage } from '@/api/chatClient';
import type { ConversationUsageAggregate } from '@/api/conversationsApi';
import { useMessageMerger } from './useMessageMerger';
import { getMergeKey } from './messageMergeKey';
import { buildDisplayItems } from './messageDisplay';
import {
  serverToolUseToToolsCall,
  serverToolResultToToolCallResult,
  textWithCitationsToText,
} from './messageConversions';
import { logger } from '@/utils';

const log = logger.forComponent('useChat');

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
  const { transport: initialTransport = 'websocket', getModeId, getProviderId, getWorkspaceId } = options;
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
      if (aggregate && aggregate.totalTokens >= cumulativeUsage.value.totalTokens) {
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

  /**
   * Generate thread ID on first use (persists across messages for multi-turn)
   */
  function getOrCreateThreadId(): string {
    if (!threadId.value) {
      threadId.value = `thread-${Date.now()}-${Math.random().toString(36).substring(2, 9)}`;
      log.info('Created new thread', { threadId: threadId.value });
    }
    return threadId.value;
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

    // Determine if this is a complete (non-update) content message. A NotifyMessage is a terminal,
    // non-streamed message (out-of-band notification) — routed here so it is NOT dropped as an
    // "unknown message type"; the displayItems notification branch renders it as a pill.
    const isCompleteMessage = isTextMessage(msg) || isReasoningMessage(msg) || isToolsCallMessage(msg) || isToolCallMessage(msg) || isNotifyMessage(msg);

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
      isLoading.value = false;
      isSending.value = false;
    }
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
   * Open (or replace) the persistent WebSocket for a thread and wire the stream callbacks.
   * Does NOT send anything — callers send afterwards (new message) or leave it subscribe-only
   * (resume). Shared by `sendMessageViaWebSocket` and `resumeStreamIfActive`.
   */
  async function openStreamConnection(
    effectiveThreadId: string,
    callbacks: { onMessage: (msg: Message) => void; onDone: () => void; onError: (err: string) => void }
  ): Promise<void> {
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
    if (wsConnection) {
      const { closeWebSocketConnection } = await import('@/api/wsClient');
      closeWebSocketConnection(wsConnection);
      wsConnection = null;
    }

    const { createWebSocketConnection } = await import('@/api/wsClient');

    wsConnection = await createWebSocketConnection({
      threadId: effectiveThreadId,
      modeId: currentModeId,
      providerId: currentProviderId,
      workspaceId: currentWorkspaceId,
      record: recordEnabled,
      ...callbacks,
      onAuthEvent: handleAuthEvent,
      onDone: () => {
        log.debug('WebSocket stream done signal received');
        callbacks.onDone();
        // Keep connection open for next message (don't close)
      },
      onError: async (error) => {
        log.error('WebSocket error', { error });
        callbacks.onError(error);
        // Close and cleanup on error
        if (wsConnection) {
          const { closeWebSocketConnection } = await import('@/api/wsClient');
          closeWebSocketConnection(wsConnection);
          wsConnection = null;
        }
      },
    });
  }

  /**
   * Send message via WebSocket (persistent or new connection)
   */
  async function sendMessageViaWebSocket(
    text: string,
    callbacks: { onMessage: (msg: Message) => void; onDone: () => void; onError: (err: string) => void }
  ): Promise<void> {
    const effectiveThreadId = getOrCreateThreadId();

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

      // Send message on existing connection
      const { sendWebSocketMessage } = await import('@/api/wsClient');
      sendWebSocketMessage(wsConnection, text);
    } else {
      await openStreamConnection(effectiveThreadId, callbacks);

      // Send message on new connection
      const { sendWebSocketMessage } = await import('@/api/wsClient');
      sendWebSocketMessage(wsConnection!, text);
    }
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

    // Close WebSocket connection
    await disconnectWebSocket();
  }
  
  /**
   * Disconnect persistent WebSocket connection
   */
  async function disconnectWebSocket(): Promise<void> {
    if (wsConnection) {
      log.info('Disconnecting WebSocket', { connectionId: wsConnection.connectionId });
      const { closeWebSocketConnection } = await import('@/api/wsClient');
      closeWebSocketConnection(wsConnection);
      wsConnection = null;
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

    await disconnectWebSocket();

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
    threadId.value = newThreadId;
  }

  /**
   * Load messages from backend for an existing conversation
   */
  async function loadMessagesFromBackend(existingThreadId: string): Promise<void> {
    log.info('Loading messages from backend', { threadId: existingThreadId });

    const { loadConversationMessages } = await import('@/api/conversationsApi');
    const persistedMessages = await loadConversationMessages(existingThreadId);

    log.debug('Loaded persisted messages', { count: persistedMessages.length });

    // Clear current state
    pendingMessages.value = [];
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
      if (usageAggregate) {
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
  };
}

function isRecordingEnabledFromPageQuery(): boolean {
  const recordValue = new URLSearchParams(window.location.search).get('record');
  if (!recordValue) return false;

  const normalized = recordValue.trim().toLowerCase();
  return normalized === '1' || normalized === 'true';
}
