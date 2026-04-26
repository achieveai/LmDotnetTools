import { ref, computed } from 'vue';
import type {
  Message,
  TextMessage,
  UsageMessage,
  ToolCallResultMessage,
  DisplayItem,
  MessageStatus,
  ReasoningMessage,
  ToolsCallMessage,
  ToolCallMessage,
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
  normalizeReasoningVisibility,
} from '@/types';
import { sendChatMessage } from '@/api/chatClient';
import { useMessageMerger } from './useMessageMerger';
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
}

/**
 * Generate merge key for message updates
 */
function getMergeKey(msg: Message): string {
  const runId = msg.runId || 'default';
  const generationId = msg.generationId || 'default';
  const messageOrderIdx = msg.messageOrderIdx ?? 0;
  const mergeKind = getMergeKind(msg);
  
  // For tool call updates, include toolCallIdx
  if (isToolCallUpdateMessage(msg) || isToolsCallUpdateMessage(msg)) {
    if (isToolCallUpdateMessage(msg)) {
      // Individual tool call - use tool_call_id as unique identifier
      return `${mergeKind}-${runId}-${generationId}-${messageOrderIdx}-${msg.tool_call_id || 'tc'}`;
    } else {
      // ToolsCallUpdate - use messageOrderIdx only (tools are accumulated in array)
      return `${mergeKind}-${runId}-${generationId}-${messageOrderIdx}`;
    }
  }
  
  return `${mergeKind}-${runId}-${generationId}-${messageOrderIdx}`;
}

function getMergeKind(msg: Message): 'text' | 'reasoning' | 'tools' | 'tool' | 'other' {
  if (isTextMessage(msg) || isTextUpdateMessage(msg)) return 'text';
  if (isReasoningMessage(msg) || isReasoningUpdateMessage(msg)) return 'reasoning';
  if (isToolsCallMessage(msg) || isToolsCallUpdateMessage(msg)) return 'tools';
  if (isToolCallMessage(msg) || isToolCallUpdateMessage(msg)) return 'tool';
  return 'other';
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
 * Composable for managing chat state and interactions
 */
export function useChat(options: UseChatOptions = {}) {
  const { transport: initialTransport = 'websocket', getModeId } = options;
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
    completionTokens: 0,
    totalTokens: 0,
    cachedTokens: 0,
    cacheCreationTokens: 0,
  });
  const transport = ref<TransportType>(initialTransport);
  const threadId = ref<string | null>(null);
  const currentRunId = ref<string | null>(null);

  // Tool results map: tool_call_id -> ToolCallResultMessage
  const toolResults = ref<Map<string, ToolCallResultMessage>>(new Map());
  
  // Persistent WebSocket connection for full-duplex communication
  let wsConnection: import('@/api/wsClient').WebSocketConnection | null = null;

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
   * Transform messages into display items with pill grouping
   */
  const displayItems = computed<DisplayItem[]>(() => {
    const sortedMessages = sortMessages();
    const items: DisplayItem[] = [];

    let pillBuffer: Array<ReasoningMessage | ToolsCallMessage> = [];
    let pillRunId: string | null = null;
    let pillParentRunId: string | null = null;
    let pillMessageOrderIdx: number | null = null;

    function flushPill() {
      if (pillBuffer.length > 0) {
        items.push({
          type: 'pill',
          id: `pill-${items.length}`,
          items: [...pillBuffer],
          runId: pillRunId,
          parentRunId: pillParentRunId,
          messageOrderIdx: pillMessageOrderIdx,
        });
        pillBuffer = [];
        pillRunId = null;
        pillParentRunId = null;
        pillMessageOrderIdx = null;
      }
    }

    for (const msg of sortedMessages) {
      if (msg.role === 'user') {
        flushPill();
        items.push({
          type: 'user-message',
          id: msg.id,
          content: msg.content as TextMessage,
          status: msg.status,
          timestamp: msg.timestamp,
        });
      } else if (isReasoningMessage(msg.content)) {
        const reasoning = msg.content as ReasoningMessage;
        const visibility = normalizeReasoningVisibility(reasoning.visibility);

        // Skip encrypted reasoning (just shows "[Encrypted reasoning hidden]" noise)
        if (visibility === 'Encrypted') {
          continue;
        }

        // Skip duplicate plain reasoning with same content already in pill buffer
        // (backend stores both streamed accumulation and final complete message)
        const isDuplicate = pillBuffer.some(
          (item) =>
            isReasoningMessage(item) &&
            (item as ReasoningMessage).generationId === reasoning.generationId &&
            (item as ReasoningMessage).reasoning === reasoning.reasoning
        );
        if (isDuplicate) {
          continue;
        }

        // Add to pill buffer
        pillBuffer.push(reasoning);
        pillRunId = msg.runId ?? null;
        pillParentRunId = msg.parentRunId ?? null;
        pillMessageOrderIdx = msg.messageOrderIdx ?? null;
      } else if (isToolsCallMessage(msg.content)) {
        // Split multi-tool-call messages into individual pills (one per tool call)
        const toolsCall = msg.content as ToolsCallMessage;
        for (const tc of toolsCall.tool_calls) {
          const singleToolMsg: ToolsCallMessage = {
            $type: MessageType.ToolsCall,
            tool_calls: [tc],
            role: toolsCall.role,
            generationId: toolsCall.generationId,
            runId: toolsCall.runId,
            parentRunId: toolsCall.parentRunId,
            threadId: toolsCall.threadId,
            messageOrderIdx: toolsCall.messageOrderIdx,
          };
          pillBuffer.push(singleToolMsg);
        }
        pillRunId = msg.runId ?? null;
        pillParentRunId = msg.parentRunId ?? null;
        pillMessageOrderIdx = msg.messageOrderIdx ?? null;
      } else if (isToolCallMessage(msg.content)) {
        // Wrap individual tool call as its own pill (no merging)
        const toolCall: ToolCallMessage = msg.content as ToolCallMessage;
        const toolsCallMsg: ToolsCallMessage = {
          $type: MessageType.ToolsCall,
          tool_calls: [{
            tool_call_id: toolCall.tool_call_id,
            function_name: toolCall.function_name,
            function_args: toolCall.function_args,
          }],
          role: toolCall.role,
          generationId: toolCall.generationId,
          runId: toolCall.runId,
          parentRunId: toolCall.parentRunId,
          threadId: toolCall.threadId,
          messageOrderIdx: toolCall.messageOrderIdx,
        };
        pillBuffer.push(toolsCallMsg);
        pillRunId = msg.runId ?? null;
        pillParentRunId = msg.parentRunId ?? null;
        pillMessageOrderIdx = msg.messageOrderIdx ?? null;
      } else if (isTextMessage(msg.content)) {
        // Text message - flush pill and add text
        flushPill();
        items.push({
          type: 'assistant-message',
          id: msg.id,
          content: msg.content as TextMessage,
          runId: msg.runId,
          parentRunId: msg.parentRunId,
          messageOrderIdx: msg.messageOrderIdx,
        });
      }
    }

    // Flush any remaining pill items
    flushPill();

    return items;
  });

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
      cumulativeUsage.value = {
        promptTokens: cumulativeUsage.value.promptTokens + promptTokens,
        completionTokens: cumulativeUsage.value.completionTokens + completionTokens,
        totalTokens: cumulativeUsage.value.totalTokens + totalTokens,
        cachedTokens: cumulativeUsage.value.cachedTokens + cachedTokens,
        cacheCreationTokens: cumulativeUsage.value.cacheCreationTokens + cacheCreationTokens,
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
      const stResult = msg as unknown as Record<string, unknown>;
      const toolUseId =
        (typeof stResult.tool_use_id === 'string' ? stResult.tool_use_id : undefined)
        ?? (typeof stResult.tool_call_id === 'string' ? stResult.tool_call_id : undefined);
      const toolName =
        (typeof stResult.tool_name === 'string' ? stResult.tool_name : undefined)
        ?? (typeof stResult.function_name === 'string' ? stResult.function_name : undefined);
      const isError =
        (typeof stResult.is_error === 'boolean' ? stResult.is_error : undefined)
        ?? (typeof stResult.isError === 'boolean' ? stResult.isError : undefined)
        ?? false;
      const errorCode =
        (typeof stResult.error_code === 'string' ? stResult.error_code : undefined)
        ?? (typeof stResult.errorCode === 'string' ? stResult.errorCode : undefined)
        ?? null;
      const rawResult = stResult.result;
      const resultStr = typeof rawResult === 'string' ? rawResult : JSON.stringify(rawResult ?? {});
      const converted: ToolCallResultMessage = {
        $type: MessageType.ToolCallResult,
        tool_call_id: toolUseId,
        tool_name: toolName,
        result: isError ? `Error (${errorCode || 'unknown'}): ${resultStr}` : resultStr,
        is_error: isError,
        error_code: errorCode,
        role: msg.role,
        generationId: msg.generationId,
        runId: msg.runId,
        parentRunId: msg.parentRunId,
        threadId: msg.threadId,
        messageOrderIdx: msg.messageOrderIdx,
      };
      if (toolUseId) {
        toolResults.value.set(toolUseId, converted);
      }
      log.debug('Received server tool result', { toolName, toolUseId, isError });

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
        log.warn('Server tool result missing tool id', { msg: stResult });
      }
      return;
    }

    // Handle server tool use → convert to ToolsCallMessage for pill display
    if (isServerToolUseMessage(msg)) {
      const stUse = msg as unknown as Record<string, unknown>;
      const toolName =
        (typeof stUse.tool_name === 'string' ? stUse.tool_name : undefined)
        ?? (typeof stUse.function_name === 'string' ? stUse.function_name : undefined);
      const toolUseId =
        (typeof stUse.tool_use_id === 'string' ? stUse.tool_use_id : undefined)
        ?? (typeof stUse.tool_call_id === 'string' ? stUse.tool_call_id : undefined);
      const rawInput = stUse.input ?? stUse.function_args ?? {};
      const executionTarget = stUse.execution_target === 'ProviderServer' || stUse.execution_target === 'LocalFunction'
        ? stUse.execution_target
        : undefined;
      const inputStr = typeof rawInput === 'string' ? rawInput : JSON.stringify(rawInput ?? {});
      const converted: ToolsCallMessage = {
        $type: MessageType.ToolsCall,
        tool_calls: [{
          function_name: toolName,
          function_args: inputStr,
          tool_call_id: toolUseId,
          execution_target: executionTarget,
        }],
        role: msg.role,
        fromAgent: msg.fromAgent,
        generationId: msg.generationId,
        runId: msg.runId,
        parentRunId: msg.parentRunId,
        threadId: msg.threadId,
        messageOrderIdx: msg.messageOrderIdx,
      };
      log.debug('Converted server tool use to ToolsCallMessage', { toolName, toolUseId });
      msg = converted;
      // Fall through to normal message handling below
    }

    // Handle text with citations → convert to TextMessage with citations as markdown
    if (isTextWithCitationsMessage(msg)) {
      const citMsg = msg; // narrowed to TextWithCitationsMessage
      let text = citMsg.text;
      if (citMsg.citations?.length) {
        const uniqueUrls = new Map<string, { title: string; url: string }>();
        for (const cite of citMsg.citations) {
          if (cite.url && !uniqueUrls.has(cite.url)) {
            uniqueUrls.set(cite.url, {
              title: cite.title || cite.url,
              url: cite.url,
            });
          }
        }
        if (uniqueUrls.size > 0) {
          text += '\n\n**Sources:**\n';
          for (const { title, url } of uniqueUrls.values()) {
            text += `- [${title}](${url})\n`;
          }
        }
      }
      const converted: TextMessage = {
        $type: MessageType.Text,
        text,
        role: citMsg.role,
        fromAgent: citMsg.fromAgent,
        generationId: citMsg.generationId,
        runId: citMsg.runId,
        parentRunId: citMsg.parentRunId,
        threadId: citMsg.threadId,
        messageOrderIdx: citMsg.messageOrderIdx,
      };
      log.debug('Converted text with citations to TextMessage', { citationCount: citMsg.citations?.length ?? 0 });
      msg = converted;
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

    // Determine if this is a complete (non-update) content message
    const isCompleteMessage = isTextMessage(msg) || isReasoningMessage(msg) || isToolsCallMessage(msg) || isToolCallMessage(msg);

    if (!isUpdate && !isCompleteMessage) {
      // Unknown message type - skip
      log.debug('Skipping unknown message type', { type: msg.$type });
      return;
    }

    // Process through merger (handles both updates and complete messages)
    const mergedMessage = isUpdate ? processUpdate(msg) : msg;
    const mergeKey = getMergeKey(msg);

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

    const callbacks = {
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
   * Send message via WebSocket (persistent or new connection)
   */
  async function sendMessageViaWebSocket(
    text: string,
    callbacks: { onMessage: (msg: Message) => void; onDone: () => void; onError: (err: string) => void }
  ): Promise<void> {
    const effectiveThreadId = getOrCreateThreadId();
    
    // Check if we have an open connection
    if (wsConnection && wsConnection.isConnected && wsConnection.socket.readyState === WebSocket.OPEN) {
      log.info('Reusing existing WebSocket connection', { 
        connectionId: wsConnection.connectionId,
        threadId: wsConnection.threadId 
      });
      
      // Send message on existing connection
      const { sendWebSocketMessage } = await import('@/api/wsClient');
      sendWebSocketMessage(wsConnection, text);
    } else {
      const currentModeId = getModeId?.();
      log.info('Creating new WebSocket connection', {
        threadId: effectiveThreadId,
        modeId: currentModeId,
        recordEnabled,
      });

      // Close old connection if exists
      if (wsConnection) {
        const { closeWebSocketConnection } = await import('@/api/wsClient');
        closeWebSocketConnection(wsConnection);
        wsConnection = null;
      }

      // Create new persistent connection
      const { createWebSocketConnection, sendWebSocketMessage } = await import('@/api/wsClient');

      wsConnection = await createWebSocketConnection({
        threadId: effectiveThreadId,
        modeId: currentModeId,
        record: recordEnabled,
        ...callbacks,
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
      
      // Send message on new connection
      sendWebSocketMessage(wsConnection, text);
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
    cumulativeUsage.value = { promptTokens: 0, completionTokens: 0, totalTokens: 0, cachedTokens: 0, cacheCreationTokens: 0 };
    error.value = null;
    threadId.value = null;
    currentRunId.value = null;
    toolResults.value.clear();
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

        // Create chat message
        const chatMessage: InternalChatMessage = {
          id: pm.id,
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

        messageIndex.value.set(pm.id, chatMessage);
        messageOrder.value.push(pm.id);
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
    transport,
    threadId,
    currentRunId,
    toolResults,
    pendingMessages: pendingMessagesForQueue,
    sendMessage,
    clearMessages,
    cancelStream,
    setTransport,
    disconnectWebSocket,
    getResultForToolCall,
    setThreadId,
    loadMessagesFromBackend,
  };
}

function isRecordingEnabledFromPageQuery(): boolean {
  const recordValue = new URLSearchParams(window.location.search).get('record');
  if (!recordValue) return false;

  const normalized = recordValue.trim().toLowerCase();
  return normalized === '1' || normalized === 'true';
}
