import { describe, it, expect } from 'vitest';
import {
  parseSSEChunk,
  parseMessageFromSSE,
  isDoneEvent,
  isErrorEvent,
  parseErrorFromSSE,
  type SSEEvent,
} from '@/api/sseParser';
import {
  responseSampleRaw,
  splitSSEEvents,
  expectedGenerationId,
} from '../fixtures/responseSample';

describe('sseParser', () => {
  describe('parseSSEChunk', () => {
    it('should parse data-only events', () => {
      const chunk = 'data: {"$type":"text_update","text":"hello"}';
      const result = parseSSEChunk(chunk);

      expect(result).not.toBeNull();
      expect(result?.data).toBe('{"$type":"text_update","text":"hello"}');
      expect(result?.event).toBeUndefined();
    });

    it('should parse event + data events', () => {
      const chunk = 'event: done\ndata: {}';
      const result = parseSSEChunk(chunk);

      expect(result).not.toBeNull();
      expect(result?.event).toBe('done');
      expect(result?.data).toBe('{}');
    });

    it('should return null for empty chunks', () => {
      expect(parseSSEChunk('')).toBeNull();
      expect(parseSSEChunk('   ')).toBeNull();
      expect(parseSSEChunk('\n\n')).toBeNull();
    });

    it('should return null for chunks without data', () => {
      const chunk = 'event: some-event';
      expect(parseSSEChunk(chunk)).toBeNull();
    });

    it('should handle multiline chunks with only data', () => {
      const chunk = 'data: {"key": "value"}';
      const result = parseSSEChunk(chunk);

      expect(result).not.toBeNull();
      expect(result?.data).toBe('{"key": "value"}');
    });
  });

  describe('parseMessageFromSSE', () => {
    it('should parse valid JSON into Message', () => {
      const data = '{"$type":"text_update","text":"hello","role":"assistant","isUpdate":true}';
      const result = parseMessageFromSSE(data);

      expect(result).not.toBeNull();
      expect(result?.$type).toBe('text_update');
      expect((result as { text: string }).text).toBe('hello');
    });

    it('should return null for invalid JSON', () => {
      const result = parseMessageFromSSE('not valid json');
      expect(result).toBeNull();
    });

    it('should return null for empty data', () => {
      const result = parseMessageFromSSE('');
      expect(result).toBeNull();
    });

    it('should parse reasoning_update message', () => {
      const data =
        '{"$type":"reasoning_update","reasoning":"thinking...","isUpdate":true,"visibility":0,"role":"assistant"}';
      const result = parseMessageFromSSE(data);

      expect(result).not.toBeNull();
      expect(result?.$type).toBe('reasoning_update');
      expect((result as { reasoning: string }).reasoning).toBe('thinking...');
    });
  });

  describe('isDoneEvent', () => {
    it('should return true for done events', () => {
      const event: SSEEvent = { event: 'done', data: '{}' };
      expect(isDoneEvent(event)).toBe(true);
    });

    it('should return false for non-done events', () => {
      const event: SSEEvent = { event: 'message', data: '{}' };
      expect(isDoneEvent(event)).toBe(false);
    });

    it('should return false when event is undefined', () => {
      const event: SSEEvent = { data: '{"$type":"text_update"}' };
      expect(isDoneEvent(event)).toBe(false);
    });
  });

  describe('isErrorEvent', () => {
    it('should return true for error events', () => {
      const event: SSEEvent = { event: 'error', data: '{"error":"something went wrong"}' };
      expect(isErrorEvent(event)).toBe(true);
    });

    it('should return false for non-error events', () => {
      const event: SSEEvent = { event: 'done', data: '{}' };
      expect(isErrorEvent(event)).toBe(false);
    });
  });

  describe('parseErrorFromSSE', () => {
    it('should parse error message from JSON', () => {
      const result = parseErrorFromSSE('{"error":"Connection failed"}');
      expect(result).toBe('Connection failed');
    });

    it('should return "Unknown error" when error property is missing', () => {
      const result = parseErrorFromSSE('{}');
      expect(result).toBe('Unknown error');
    });

    it('should return raw data for invalid JSON', () => {
      const result = parseErrorFromSSE('Raw error message');
      expect(result).toBe('Raw error message');
    });
  });

  describe('ResponseSample parsing', () => {
    it('should split SSE stream into individual events', () => {
      const events = splitSSEEvents(responseSampleRaw);
      // Should have 26 reasoning_update + 1 reasoning + 39 text_update + 1 done = 67 events
      expect(events.length).toBeGreaterThan(60);
    });

    it('should parse all events from ResponseSample', () => {
      const events = splitSSEEvents(responseSampleRaw);
      let reasoningUpdateCount = 0;
      let reasoningCount = 0;
      let textUpdateCount = 0;
      let doneCount = 0;

      for (const chunk of events) {
        const sseEvent = parseSSEChunk(chunk);
        if (!sseEvent) continue;

        if (isDoneEvent(sseEvent)) {
          doneCount++;
          continue;
        }

        const message = parseMessageFromSSE(sseEvent.data);
        if (!message) continue;

        switch (message.$type) {
          case 'reasoning_update':
            reasoningUpdateCount++;
            expect(message.generationId).toBe(expectedGenerationId);
            break;
          case 'reasoning':
            reasoningCount++;
            expect(message.generationId).toBe(expectedGenerationId);
            break;
          case 'text_update':
            textUpdateCount++;
            expect(message.generationId).toBe(expectedGenerationId);
            break;
        }
      }

      expect(reasoningUpdateCount).toBe(26);
      expect(reasoningCount).toBe(1);
      expect(textUpdateCount).toBe(39);
      expect(doneCount).toBe(1);
    });

    it('should detect done event at end of stream', () => {
      const events = splitSSEEvents(responseSampleRaw);
      const lastEvent = events[events.length - 1];
      const sseEvent = parseSSEChunk(lastEvent);

      expect(sseEvent).not.toBeNull();
      expect(isDoneEvent(sseEvent!)).toBe(true);
    });
  });
});
