import { describe, it, expect, beforeEach } from 'vitest';
import { useMessageMerger } from '@/composables/useMessageMerger';
import {
  type TextUpdateMessage,
  type ReasoningUpdateMessage,
  type TextMessage,
  type ReasoningMessage,
  MessageType,
} from '@/types';
import {
  responseSampleRaw,
  splitSSEEvents,
  expectedFinalText,
  expectedGenerationId,
} from '../fixtures/responseSample';
import { parseSSEChunk, parseMessageFromSSE, isDoneEvent } from '@/api/sseParser';

describe('useMessageMerger', () => {
  let merger: ReturnType<typeof useMessageMerger>;

  beforeEach(() => {
    merger = useMessageMerger();
  });

  describe('processUpdate - text updates', () => {
    it('should concatenate text updates', () => {
      const update1: TextUpdateMessage = {
        $type: MessageType.TextUpdate,
        text: 'Hello',
        isUpdate: true,
        role: 'assistant',
        generationId: 'gen-123',
      };

      const result1 = merger.processUpdate(update1) as TextMessage;
      expect(result1.$type).toBe(MessageType.Text);
      expect(result1.text).toBe('Hello');

      const update2: TextUpdateMessage = {
        $type: MessageType.TextUpdate,
        text: ' World',
        isUpdate: true,
        role: 'assistant',
        generationId: 'gen-123',
      };

      const result2 = merger.processUpdate(update2) as TextMessage;
      // Text updates are now concatenated (not replaced)
      expect(result2.text).toBe('Hello World');
    });

    it('should track isThinking state', () => {
      const update: TextUpdateMessage = {
        $type: MessageType.TextUpdate,
        text: 'thinking...',
        isUpdate: true,
        isThinking: true,
        role: 'assistant',
        generationId: 'gen-123',
      };

      const result = merger.processUpdate(update) as TextMessage;
      expect(result.isThinking).toBe(true);
    });

    it('should maintain separate accumulators for different generationIds', () => {
      const update1: TextUpdateMessage = {
        $type: MessageType.TextUpdate,
        text: 'First gen',
        isUpdate: true,
        role: 'assistant',
        generationId: 'gen-1',
      };

      const update2: TextUpdateMessage = {
        $type: MessageType.TextUpdate,
        text: 'Second gen',
        isUpdate: true,
        role: 'assistant',
        generationId: 'gen-2',
      };

      const result1 = merger.processUpdate(update1) as TextMessage;
      const result2 = merger.processUpdate(update2) as TextMessage;

      expect(result1.text).toBe('First gen');
      expect(result1.generationId).toBe('gen-1');
      expect(result2.text).toBe('Second gen');
      expect(result2.generationId).toBe('gen-2');
    });
  });

  describe('processUpdate - reasoning updates', () => {
    it('should concatenate reasoning updates', () => {
      const update1: ReasoningUpdateMessage = {
        $type: MessageType.ReasoningUpdate,
        reasoning: 'First ',
        isUpdate: true,
        visibility: 'Plain',
        role: 'assistant',
        generationId: 'gen-123',
      };

      const result1 = merger.processUpdate(update1) as ReasoningMessage;
      expect(result1.$type).toBe(MessageType.Reasoning);
      expect(result1.reasoning).toBe('First ');

      const update2: ReasoningUpdateMessage = {
        $type: MessageType.ReasoningUpdate,
        reasoning: 'Second ',
        isUpdate: true,
        visibility: 'Plain',
        role: 'assistant',
        generationId: 'gen-123',
      };

      const result2 = merger.processUpdate(update2) as ReasoningMessage;
      expect(result2.reasoning).toBe('First Second ');

      const update3: ReasoningUpdateMessage = {
        $type: MessageType.ReasoningUpdate,
        reasoning: 'Third',
        isUpdate: true,
        visibility: 'Plain',
        role: 'assistant',
        generationId: 'gen-123',
      };

      const result3 = merger.processUpdate(update3) as ReasoningMessage;
      expect(result3.reasoning).toBe('First Second Third');
    });

    it('should preserve visibility from first update', () => {
      const update: ReasoningUpdateMessage = {
        $type: MessageType.ReasoningUpdate,
        reasoning: 'thinking',
        isUpdate: true,
        visibility: 'Encrypted',
        role: 'assistant',
        generationId: 'gen-123',
      };

      const result = merger.processUpdate(update) as ReasoningMessage;
      expect(result.visibility).toBe('Encrypted');
    });
  });

  describe('processUpdate - non-update messages', () => {
    it('should pass through non-update messages directly', () => {
      const textMessage: TextMessage = {
        $type: MessageType.Text,
        text: 'Final message',
        role: 'assistant',
        generationId: 'gen-123',
      };

      const result = merger.processUpdate(textMessage);
      expect(result).toBe(textMessage);
    });
  });

  describe('finalize', () => {
    it('should clear accumulator for specific generationId', () => {
      const update: TextUpdateMessage = {
        $type: MessageType.TextUpdate,
        text: 'Hello',
        isUpdate: true,
        role: 'assistant',
        generationId: 'gen-123',
      };

      merger.processUpdate(update);
      merger.finalize('gen-123');

      // After finalize, a new update should start fresh
      const newUpdate: TextUpdateMessage = {
        $type: MessageType.TextUpdate,
        text: 'New',
        isUpdate: true,
        role: 'assistant',
        generationId: 'gen-123',
      };

      const result = merger.processUpdate(newUpdate) as TextMessage;
      expect(result.text).toBe('New');
    });
  });

  describe('reset', () => {
    it('should clear all accumulators', () => {
      const update1: TextUpdateMessage = {
        $type: MessageType.TextUpdate,
        text: 'First',
        isUpdate: true,
        role: 'assistant',
        generationId: 'gen-1',
      };

      const update2: TextUpdateMessage = {
        $type: MessageType.TextUpdate,
        text: 'Second',
        isUpdate: true,
        role: 'assistant',
        generationId: 'gen-2',
      };

      merger.processUpdate(update1);
      merger.processUpdate(update2);
      merger.reset();

      // After reset, both should start fresh
      const result1 = merger.processUpdate({
        ...update1,
        text: 'New First',
      }) as TextMessage;
      const result2 = merger.processUpdate({
        ...update2,
        text: 'New Second',
      }) as TextMessage;

      expect(result1.text).toBe('New First');
      expect(result2.text).toBe('New Second');
    });
  });

  describe('ResponseSample integration', () => {
    it('should process all events from ResponseSample', () => {
      const events = splitSSEEvents(responseSampleRaw);
      let lastTextMessage: TextMessage | null = null;
      let lastReasoningMessage: ReasoningMessage | null = null;
      let reasoningUpdateCount = 0;
      let textUpdateCount = 0;

      for (const chunk of events) {
        const sseEvent = parseSSEChunk(chunk);
        if (!sseEvent || isDoneEvent(sseEvent)) continue;

        const message = parseMessageFromSSE(sseEvent.data);
        if (!message) continue;

        const result = merger.processUpdate(message);

        if (result.$type === MessageType.Text) {
          lastTextMessage = result as TextMessage;
          if (message.$type === MessageType.TextUpdate) {
            textUpdateCount++;
          }
        } else if (result.$type === MessageType.Reasoning) {
          lastReasoningMessage = result as ReasoningMessage;
          if (message.$type === MessageType.ReasoningUpdate) {
            reasoningUpdateCount++;
          }
        }
      }

      // Verify counts (updated for new fixture with messageOrderIdx/chunkIdx)
      expect(reasoningUpdateCount).toBe(26);
      expect(textUpdateCount).toBe(39);

      // Verify final text (all text_update chunks are concatenated)
      expect(lastTextMessage).not.toBeNull();
      expect(lastTextMessage?.text).toBe(expectedFinalText);
      expect(lastTextMessage?.generationId).toBe(expectedGenerationId);

      // Verify reasoning was accumulated (concatenated)
      expect(lastReasoningMessage).not.toBeNull();
      expect(lastReasoningMessage?.reasoning.length).toBeGreaterThan(100);
    });

    it('should handle transition from reasoning to text updates', () => {
      const events = splitSSEEvents(responseSampleRaw);
      const messageTypes: string[] = [];

      for (const chunk of events) {
        const sseEvent = parseSSEChunk(chunk);
        if (!sseEvent || isDoneEvent(sseEvent)) continue;

        const message = parseMessageFromSSE(sseEvent.data);
        if (!message) continue;

        const result = merger.processUpdate(message);
        messageTypes.push(result.$type);
      }

      // Should have reasoning types first, then text types
      const firstTextIndex = messageTypes.indexOf(MessageType.Text);
      const lastReasoningIndex = messageTypes.lastIndexOf(MessageType.Reasoning);

      // Reasoning messages should come before text messages
      expect(lastReasoningIndex).toBeLessThan(firstTextIndex);
    });
  });
});
