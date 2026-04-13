import type { Message } from '@/types';

/**
 * Parsed SSE event
 */
export interface SSEEvent {
  event?: string;
  data: string;
}

/**
 * Parse a single SSE chunk into an event.
 * SSE format: event: name\ndata: {json}\n\n
 */
export function parseSSEChunk(chunk: string): SSEEvent | null {
  const lines = chunk.trim().split('\n');
  if (lines.length === 0) return null;

  let event: string | undefined;
  let data = '';

  for (const line of lines) {
    if (line.startsWith('event:')) {
      event = line.slice(6).trim();
    } else if (line.startsWith('data:')) {
      data = line.slice(5).trim();
    }
  }

  if (!data) return null;
  return { event, data };
}

/**
 * Parse SSE data payload into a Message object
 */
export function parseMessageFromSSE(data: string): Message | null {
  try {
    return JSON.parse(data) as Message;
  } catch (e) {
    console.error('Failed to parse SSE message:', data, e);
    return null;
  }
}

/**
 * SSE event types
 */
export const SSEEventType = {
  Done: 'done',
  Error: 'error',
} as const;

/**
 * Check if SSE event is a done event
 */
export function isDoneEvent(event: SSEEvent): boolean {
  return event.event === SSEEventType.Done;
}

/**
 * Check if SSE event is an error event
 */
export function isErrorEvent(event: SSEEvent): boolean {
  return event.event === SSEEventType.Error;
}

/**
 * Parse error data from an SSE error event
 */
export function parseErrorFromSSE(data: string): string {
  try {
    const parsed = JSON.parse(data) as { error?: string };
    return parsed.error || 'Unknown error';
  } catch {
    return data || 'Unknown error';
  }
}
