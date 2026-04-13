import type { Message } from '@/types';
import {
  parseSSEChunk,
  parseMessageFromSSE,
  isDoneEvent,
  isErrorEvent,
  parseErrorFromSSE,
} from './sseParser';
import { logger } from '@/utils';

const log = logger.forComponent('ChatClient');

/**
 * Chat request payload matching C# ChatRequest record
 */
export interface ChatRequest {
  Message: string;
}

/**
 * Callbacks for SSE stream events
 */
export interface ChatClientCallbacks {
  onMessage: (message: Message) => void;
  onDone: () => void;
  onError: (error: string) => void;
}

/**
 * Chat client options
 */
export interface ChatClientOptions extends ChatClientCallbacks {
  baseUrl?: string;
}

/**
 * Send a chat message and stream responses via SSE.
 * Uses fetch with streaming body reader to consume SSE.
 */
export async function sendChatMessage(
  message: string,
  options: ChatClientOptions
): Promise<void> {
  const { baseUrl = '', onMessage, onDone, onError } = options;

  log.info('Sending chat message', { messageLength: message.length });

  try {
    const response = await fetch(`${baseUrl}/api/chat`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'text/event-stream',
      },
      body: JSON.stringify({ Message: message } satisfies ChatRequest),
    });

    if (!response.ok) {
      log.error('HTTP error from chat API', { status: response.status, statusText: response.statusText });
      throw new Error(`HTTP ${response.status}: ${response.statusText}`);
    }

    log.debug('SSE stream connected, starting to read');

    const reader = response.body?.getReader();
    if (!reader) {
      throw new Error('Response body is not readable');
    }

    const decoder = new TextDecoder();
    let buffer = '';

    while (true) {
      const { done, value } = await reader.read();

      if (done) {
        log.debug('SSE stream completed');
        // Process any remaining buffer
        if (buffer.trim()) {
          processChunk(buffer, onMessage, onDone, onError);
        }
        onDone();
        break;
      }

      buffer += decoder.decode(value, { stream: true });

      // Process complete SSE events (separated by \n\n)
      const events = buffer.split('\n\n');
      buffer = events.pop() || ''; // Keep incomplete event in buffer

      for (const eventChunk of events) {
        if (!eventChunk.trim()) continue;

        const shouldStop = processChunk(eventChunk, onMessage, onDone, onError);
        if (shouldStop) return;
      }
    }
  } catch (error) {
    log.error('Chat request failed', { error: error instanceof Error ? error.message : error });
    onError(error instanceof Error ? error.message : 'Unknown error');
  }
}

/**
 * Process a single SSE chunk
 * @returns true if processing should stop (done/error)
 */
function processChunk(
  chunk: string,
  onMessage: (message: Message) => void,
  onDone: () => void,
  onError: (error: string) => void
): boolean {
  const sseEvent = parseSSEChunk(chunk);
  if (!sseEvent) return false;

  // Handle done event
  if (isDoneEvent(sseEvent)) {
    log.debug('Received done event');
    onDone();
    return true;
  }

  // Handle error event
  if (isErrorEvent(sseEvent)) {
    const errorMsg = parseErrorFromSSE(sseEvent.data);
    log.error('Received error event', { error: errorMsg });
    onError(errorMsg);
    return true;
  }

  // Parse and emit message
  const message = parseMessageFromSSE(sseEvent.data);
  if (message) {
    log.trace('Received message', { type: message.$type });
    onMessage(message);
  }

  return false;
}

/**
 * Abort controller for cancelling ongoing requests
 */
export function createAbortController(): AbortController {
  return new AbortController();
}
