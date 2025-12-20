import { ref } from 'vue';
import type {
  Message,
  TextMessage,
  UsageMessage,
} from '@/types';
import {
  MessageType,
  isUsageMessage,
  isRunAssignmentMessage,
  isRunCompletedMessage,
} from '@/types';
import { sendChatMessage } from '@/api/chatClient';
import { sendChatMessageWs } from '@/api/wsClient';
import { useMessageMerger } from './useMessageMerger';
import { logger } from '@/utils';

const log = logger.forComponent('useChat');

/**
 * Transport type for streaming messages
 */
export type TransportType = 'sse' | 'websocket';

/**
 * Represents a chat message in the UI
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
}

/**
 * Composable for managing chat state and interactions
 */
export function useChat(options: UseChatOptions = {}) {
  const { transport: initialTransport = 'websocket' } = options;

  const messages = ref<ChatMessage[]>([]);
  const isLoading = ref(false);
  const error = ref<string | null>(null);
  const usage = ref<UsageMessage | null>(null);
  const transport = ref<TransportType>(initialTransport);
  const threadId = ref<string | null>(null);
  const currentRunId = ref<string | null>(null);

  const { processUpdate, finalize, reset } = useMessageMerger();

  let currentStreamingId: string | null = null;

  // Generate thread ID on first use (persists across messages for multi-turn)
  function getOrCreateThreadId(): string {
    if (!threadId.value) {
      threadId.value = `thread-${Date.now()}-${Math.random().toString(36).substring(2, 9)}`;
      log.info('Created new thread', { threadId: threadId.value });
    }
    return threadId.value;
  }

  /**
   * Send a message and stream the response
   */
  async function sendMessage(text: string): Promise<void> {
    if (!text.trim() || isLoading.value) return;

    log.info('User sending message', { textLength: text.length, transport: transport.value });

    error.value = null;
    isLoading.value = true;

    // Add user message
    const userMessage: ChatMessage = {
      id: crypto.randomUUID(),
      role: 'user',
      content: {
        $type: MessageType.Text,
        text,
        role: 'user',
      } as TextMessage,
    };
    messages.value.push(userMessage);

    // Prepare streaming assistant message
    currentStreamingId = crypto.randomUUID();
    const assistantMessage: ChatMessage = {
      id: currentStreamingId,
      role: 'assistant',
      content: {
        $type: MessageType.Text,
        text: '',
        role: 'assistant',
      } as TextMessage,
      isStreaming: true,
    };
    messages.value.push(assistantMessage);

    const callbacks = {
      onMessage: (msg: Message) => {
        // Handle usage messages separately
        if (isUsageMessage(msg)) {
          usage.value = msg;
          return;
        }

        // Handle lifecycle messages
        if (isRunAssignmentMessage(msg)) {
          currentRunId.value = msg.assignment.runId;
          log.debug('Run started', { runId: msg.assignment.runId, generationId: msg.assignment.generationId });
          return;
        }

        if (isRunCompletedMessage(msg)) {
          log.debug('Run completed', { runId: msg.completedRunId, hasPending: msg.hasPendingMessages });
          return;
        }

        // Process and merge streaming updates
        const mergedMessage = processUpdate(msg);
        const idx = messages.value.findIndex((m) => m.id === currentStreamingId);
        if (idx !== -1) {
          messages.value[idx].content = mergedMessage;
        }
      },
      onDone: () => {
        log.info('Stream completed', { messageCount: messages.value.length, transport: transport.value });
        const idx = messages.value.findIndex((m) => m.id === currentStreamingId);
        if (idx !== -1) {
          messages.value[idx].isStreaming = false;
        }
        finalize();
        isLoading.value = false;
      },
      onError: (err: string) => {
        log.error('Stream error', { error: err, transport: transport.value });
        error.value = err;
        isLoading.value = false;
        // Remove streaming message on error
        const idx = messages.value.findIndex((m) => m.id === currentStreamingId);
        if (idx !== -1) {
          messages.value.splice(idx, 1);
        }
      },
    };

    try {
      if (transport.value === 'websocket') {
        await sendChatMessageWs(text, { ...callbacks, threadId: getOrCreateThreadId() });
      } else {
        await sendChatMessage(text, callbacks);
      }
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Unknown error';
      isLoading.value = false;
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
    messages.value = [];
    usage.value = null;
    error.value = null;
    threadId.value = null; // Reset thread for new conversation
    currentRunId.value = null;
    reset();
  }

  return {
    messages,
    isLoading,
    error,
    usage,
    transport,
    threadId,
    currentRunId,
    sendMessage,
    clearMessages,
    setTransport,
  };
}
