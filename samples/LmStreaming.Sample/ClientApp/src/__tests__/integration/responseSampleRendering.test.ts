import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import TextMessage from '@/components/TextMessage.vue';
import MetadataPill from '@/components/MetadataPill.vue';
import { useMessageMerger } from '@/composables/useMessageMerger';
import { parseSSEChunk, parseMessageFromSSE, isDoneEvent } from '@/api/sseParser';
import {
  type TextMessage as TextMessageType,
  type ReasoningMessage,
  type ReasoningUpdateMessage,
  isTextMessage,
  isReasoningMessage,
} from '@/types';
import {
  responseSampleRaw,
  splitSSEEvents,
  expectedFinalText,
  expectedGenerationId,
  expectedReasoningBase64,
} from '../fixtures/responseSample';

describe('ResponseSample Full Rendering', () => {
  /**
   * Process all SSE events and return the final accumulated messages
   */
  function processAllEvents(): {
    textMessage: TextMessageType | null;
    reasoningMessage: ReasoningMessage | null;
    doneReceived: boolean;
    eventCount: number;
  } {
    const merger = useMessageMerger();
    const events = splitSSEEvents(responseSampleRaw);

    let textMessage: TextMessageType | null = null;
    let reasoningMessage: ReasoningMessage | null = null;
    let doneReceived = false;
    let eventCount = 0;

    for (const chunk of events) {
      const sseEvent = parseSSEChunk(chunk);
      if (!sseEvent) continue;

      eventCount++;

      if (isDoneEvent(sseEvent)) {
        doneReceived = true;
        continue;
      }

      const message = parseMessageFromSSE(sseEvent.data);
      if (!message) continue;

      const result = merger.processUpdate(message);

      if (isTextMessage(result)) {
        textMessage = result;
      } else if (isReasoningMessage(result)) {
        reasoningMessage = result;
      }
    }

    return { textMessage, reasoningMessage, doneReceived, eventCount };
  }

  describe('End-to-end SSE processing', () => {
    it('should parse and process all events from ResponseSample', () => {
      const result = processAllEvents();

      expect(result.eventCount).toBeGreaterThan(40);
      expect(result.doneReceived).toBe(true);
      expect(result.textMessage).not.toBeNull();
      expect(result.reasoningMessage).not.toBeNull();
    });

    it('should accumulate correct final text', () => {
      const { textMessage } = processAllEvents();

      expect(textMessage).not.toBeNull();
      expect(textMessage!.text).toBe(expectedFinalText);
      expect(textMessage!.generationId).toBe(expectedGenerationId);
      expect(textMessage!.role).toBe('assistant');
    });

    it('should accumulate reasoning text by concatenation', () => {
      const { reasoningMessage } = processAllEvents();

      expect(reasoningMessage).not.toBeNull();
      // Reasoning should be concatenated from all reasoning_update chunks
      expect(reasoningMessage!.reasoning.length).toBeGreaterThan(100);
      expect(reasoningMessage!.generationId).toBe(expectedGenerationId);
    });

    it('should detect done event and terminate correctly', () => {
      const { doneReceived } = processAllEvents();
      expect(doneReceived).toBe(true);
    });
  });

  describe('Vue component rendering with processed messages', () => {
    it('should render final text message via TextMessage', () => {
      const { textMessage } = processAllEvents();
      expect(textMessage).not.toBeNull();

      const wrapper = mount(TextMessage, {
        props: { message: textMessage!, isStreaming: false },
      });

      expect(wrapper.find('.text-message').exists()).toBe(true);
      // Text content is rendered through markdown parser, which may alter whitespace
      // Check for key phrases instead of exact match
      const renderedText = wrapper.find('.markdown-content').text();
      expect(renderedText).toContain('Hi there');
      expect(renderedText).toContain('How can that');
      expect(renderedText).toContain('lorem ipsum dolor');
      expect(wrapper.find('.cursor').exists()).toBe(false); // Not streaming
    });

    it('should render reasoning message as a thinking-pill via MetadataPill', () => {
      const { reasoningMessage } = processAllEvents();
      expect(reasoningMessage).not.toBeNull();

      const wrapper = mount(MetadataPill, {
        props: { items: [reasoningMessage!] },
      });

      const pill = wrapper.find('[data-testid="thinking-pill"]');
      expect(pill.exists()).toBe(true);
      expect(pill.text()).toContain('Thinking:');
    });

    it('should show streaming cursor during text streaming', () => {
      const merger = useMessageMerger();
      const events = splitSSEEvents(responseSampleRaw);

      // Process just the first few text updates (simulating mid-stream)
      let textMessage: TextMessageType | null = null;
      let textUpdateCount = 0;

      for (const chunk of events) {
        const sseEvent = parseSSEChunk(chunk);
        if (!sseEvent || isDoneEvent(sseEvent)) continue;

        const message = parseMessageFromSSE(sseEvent.data);
        if (!message) continue;

        const result = merger.processUpdate(message);

        if (isTextMessage(result)) {
          textMessage = result;
          textUpdateCount++;
          if (textUpdateCount >= 3) break; // Stop after 3 text updates
        }
      }

      expect(textMessage).not.toBeNull();

      const wrapper = mount(TextMessage, {
        props: { message: textMessage!, isStreaming: true },
      });

      expect(wrapper.find('.cursor').exists()).toBe(true);
      expect(wrapper.find('.cursor').text()).toBe('|');
    });
  });

  describe('Message visibility handling', () => {
    it('should handle visibility 0 (Plain) for reasoning updates', () => {
      const events = splitSSEEvents(responseSampleRaw);

      for (const chunk of events) {
        const sseEvent = parseSSEChunk(chunk);
        if (!sseEvent) continue;

        const message = parseMessageFromSSE(sseEvent.data);
        if (!message || message.$type !== 'reasoning_update') continue;

        // All reasoning_update messages have visibility 0
        expect((message as ReasoningUpdateMessage).visibility).toBe(0);
        break;
      }
    });

    it('should handle visibility 2 (Encrypted) for final reasoning', () => {
      const events = splitSSEEvents(responseSampleRaw);

      for (const chunk of events) {
        const sseEvent = parseSSEChunk(chunk);
        if (!sseEvent) continue;

        const message = parseMessageFromSSE(sseEvent.data);
        if (!message || message.$type !== 'reasoning') continue;

        // Final reasoning message has visibility 2 and base64 content
        expect((message as ReasoningMessage).visibility).toBe(2);
        expect((message as ReasoningMessage).reasoning).toBe(expectedReasoningBase64);
        break;
      }
    });
  });

  describe('Generation ID consistency', () => {
    it('should maintain same generationId across all messages', () => {
      const events = splitSSEEvents(responseSampleRaw);
      const generationIds = new Set<string>();

      for (const chunk of events) {
        const sseEvent = parseSSEChunk(chunk);
        if (!sseEvent || isDoneEvent(sseEvent)) continue;

        const message = parseMessageFromSSE(sseEvent.data);
        if (!message || !message.generationId) continue;

        generationIds.add(message.generationId);
      }

      // All messages should have the same generationId
      expect(generationIds.size).toBe(1);
      expect(generationIds.has(expectedGenerationId)).toBe(true);
    });
  });
});
