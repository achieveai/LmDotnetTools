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
  
  // For tool call updates, include toolCallIdx
  if (isToolCallUpdateMessage(msg) || isToolsCallUpdateMessage(msg)) {
    if (isToolCallUpdateMessage(msg)) {
      // Individual tool call - use tool_call_id as unique identifier
      return `${runId}-${generationId}-${messageOrderIdx}-${msg.tool_call_id || 'tc'}`;
    } else {
      // ToolsCallUpdate - use messageOrderIdx only (tools are accumulated in array)
      return `${runId}-${generationId}-${messageOrderIdx}`;
    }
  }
  
  return `${runId}-${generationId}-${messageOrderIdx}`;
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

  // Core state
  const pendingMessages = ref<InternalChatMessage[]>([]);
  const messageIndex = ref<Map<string, InternalChatMessage>>(new Map());
  const messageOrder = ref<string[]>([]); // Order of message IDs for display
  
  const isLoading = ref(false); // Stream is active (receiving messages)
  const isSending = ref(false); // Message send is in progress
  const error = ref<string | null>(null);
  const usage = ref<UsageMessage | null>(null);
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
        // Skip encrypted-only reasoning
        const reasoning = msg.content as ReasoningMessage;
        if (reasoning.visibility === 'Encrypted' && !reasoning.reasoning) {
          continue;
        }
        // Add to pill buffer
        pillBuffer.push(msg.content as ReasoningMessage);
        pillRunId = msg.runId || null;
        pillParentRunId = msg.parentRunId || null;
        pillMessageOrderIdx = msg.messageOrderIdx || null;
      } else if (isToolsCallMessage(msg.content)) {
        // Add to pill buffer (multi-tool-call message)
        pillBuffer.push(msg.content as ToolsCallMessage);
        pillRunId = msg.runId || null;
        pillParentRunId = msg.parentRunId || null;
        pillMessageOrderIdx = msg.messageOrderIdx || null;
      } else if (isToolCallMessage(msg.content)) {
        // Add individual tool call to pill buffer
        // Convert ToolCallMessage to ToolsCallMessage format for consistent handling
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
        pillRunId = msg.runId || null;
        pillParentRunId = msg.parentRunId || null;
        pillMessageOrderIdx = msg.messageOrderIdx || null;
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
    // Backend sends PascalCase, so we need to access properties accordingly
    const runId = (assignment as any).RunId || assignment.runId;
    const generationId = (assignment as any).GenerationId || assignment.generationId;
    const inputIds = (assignment as any).InputIds || assignment.inputIds || [];
    const parentRunId = (assignment as any).ParentRunId || assignment.parentRunId;
    
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

    log.debug('Run completed', { 
      runId: msg.completedRunId, 
      hasPending: msg.hasPendingMessages 
    });

    // Mark all messages in this run as completed
    for (const message of messageIndex.value.values()) {
      if (message.runId === msg.completedRunId && message.status === 'active') {
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
      if (msg.tool_call_id) {
        toolResults.value.set(msg.tool_call_id, msg);
        log.debug('Received tool result', { toolCallId: msg.tool_call_id });
        
        // Find the tool call message and attach the result to it
        // Search through all messages to find the one with matching tool_call_id
        for (const chatMsg of messageIndex.value.values()) {
          if (isToolCallMessage(chatMsg.content)) {
            const toolCall = chatMsg.content as ToolCallMessage;
            if (toolCall.tool_call_id === msg.tool_call_id) {
              // Attach the result to the tool call message
              toolCall.result = msg.result;
              log.info('Attached result to tool call', { 
                toolCallId: msg.tool_call_id,
                messageId: chatMsg.id
              });
              break;
            }
          } else if (isToolsCallMessage(chatMsg.content)) {
            const toolsCall = chatMsg.content as ToolsCallMessage;
            // Check if any of the tool calls match
            const matchingToolCall = toolsCall.tool_calls?.find(tc => tc.tool_call_id === msg.tool_call_id);
            if (matchingToolCall) {
              // Attach the result to the matching tool call
              matchingToolCall.result = msg.result;
              log.info('Attached result to tool call in ToolsCallMessage', { 
                toolCallId: msg.tool_call_id,
                messageId: chatMsg.id
              });
              break;
            }
          }
        }
      }
      return;
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
      log.info('Creating new WebSocket connection', { threadId: effectiveThreadId, modeId: currentModeId });

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
  function clearMessages(): void {
    log.info('Clearing all messages');
    pendingMessages.value = [];
    messageIndex.value.clear();
    messageOrder.value = [];
    usage.value = null;
    error.value = null;
    threadId.value = null;
    currentRunId.value = null;
    toolResults.value.clear();
    reset();
    
    // Close WebSocket connection
    disconnectWebSocket();
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
    transport,
    threadId,
    currentRunId,
    toolResults,
    pendingMessages: pendingMessagesForQueue,
    sendMessage,
    clearMessages,
    setTransport,
    disconnectWebSocket,
    getResultForToolCall,
    setThreadId,
    loadMessagesFromBackend,
  };
}
