import { describe, it, expect } from 'vitest';
import { readFileSync } from 'fs';
import { resolve } from 'path';
import { useMessageMerger } from '@/composables/useMessageMerger';
import {
  type Message,
  type TextMessage,
  type ReasoningMessage,
  type ToolCallMessage,
  type ToolCallResultMessage,
  type RunAssignmentMessage,
  type RunCompletedMessage,
  MessageType,
  isTextMessage,
  isReasoningMessage,
  isToolCallMessage,
  isToolCallResultMessage,
  isRunAssignmentMessage,
  isRunCompletedMessage,
} from '@/types';

/**
 * Load the JSONL recording fixture and parse into messages.
 * Each line is a JSON message exactly as sent over the WebSocket.
 */
function loadRecording(): Array<{ raw: string; parsed: Record<string, unknown> }> {
  const filePath = resolve(__dirname, '../fixtures/multiturnToolRecording.jsonl');
  const content = readFileSync(filePath, 'utf-8');
  return content
    .trim()
    .split('\n')
    .map((line) => ({ raw: line, parsed: JSON.parse(line) }));
}

/**
 * Simulate WebSocket message replay: parse each line and dispatch through
 * the message merger, collecting final state.
 */
function replayRecording() {
  const merger = useMessageMerger();
  const recording = loadRecording();

  const results: {
    messages: Message[];
    textMessages: TextMessage[];
    reasoningMessages: ReasoningMessage[];
    toolCallMessages: ToolCallMessage[];
    toolCallResults: ToolCallResultMessage[];
    runAssignments: RunAssignmentMessage[];
    runCompleted: RunCompletedMessage[];
    doneReceived: boolean;
  } = {
    messages: [],
    textMessages: [],
    reasoningMessages: [],
    toolCallMessages: [],
    toolCallResults: [],
    runAssignments: [],
    runCompleted: [],
    doneReceived: false,
  };

  for (const { parsed } of recording) {
    const type = parsed.$type as string;

    // Handle done signal
    if (type === 'done') {
      results.doneReceived = true;
      continue;
    }

    // Cast to Message and process through merger
    const message = parsed as unknown as Message;
    const merged = merger.processUpdate(message);
    results.messages.push(merged);

    if (isTextMessage(merged)) {
      // Track latest text per generationId
      const existing = results.textMessages.findIndex(
        (m) => m.generationId === merged.generationId
      );
      if (existing >= 0) {
        results.textMessages[existing] = merged;
      } else {
        results.textMessages.push(merged);
      }
    } else if (isReasoningMessage(merged)) {
      const existing = results.reasoningMessages.findIndex(
        (m) => m.generationId === merged.generationId
      );
      if (existing >= 0) {
        results.reasoningMessages[existing] = merged;
      } else {
        results.reasoningMessages.push(merged);
      }
    } else if (isToolCallMessage(merged)) {
      const existing = results.toolCallMessages.findIndex(
        (m) => m.tool_call_id === merged.tool_call_id
      );
      if (existing >= 0) {
        results.toolCallMessages[existing] = merged;
      } else {
        results.toolCallMessages.push(merged);
      }
    } else if (isToolCallResultMessage(merged)) {
      results.toolCallResults.push(merged as ToolCallResultMessage);
    } else if (isRunAssignmentMessage(merged)) {
      results.runAssignments.push(merged as RunAssignmentMessage);
    } else if (isRunCompletedMessage(merged)) {
      results.runCompleted.push(merged as RunCompletedMessage);
    }
  }

  return { ...results, recording };
}

describe('WebSocket Recording Replay - Multiturn Tool Calls', () => {
  describe('Recording structure', () => {
    it('should load recording with expected number of messages', () => {
      const recording = loadRecording();
      expect(recording.length).toBe(120);
    });

    it('should contain expected message type distribution', () => {
      const recording = loadRecording();
      const typeCounts = new Map<string, number>();
      for (const { parsed } of recording) {
        const type = parsed.$type as string;
        typeCounts.set(type, (typeCounts.get(type) || 0) + 1);
      }

      expect(typeCounts.get('run_assignment')).toBe(1);
      expect(typeCounts.get('reasoning_update')).toBe(31);
      expect(typeCounts.get('reasoning')).toBe(3);
      expect(typeCounts.get('tool_call_update')).toBe(29);
      expect(typeCounts.get('tool_call_result')).toBe(3);
      expect(typeCounts.get('text_update')).toBe(51);
      expect(typeCounts.get('run_completed')).toBe(1);
      expect(typeCounts.get('done')).toBe(1);
    });

    it('should have consistent threadId across all messages', () => {
      const recording = loadRecording();
      const threadIds = new Set<string>();
      for (const { parsed } of recording) {
        if (parsed.threadId) {
          threadIds.add(parsed.threadId as string);
        }
        if (parsed.ThreadId) {
          threadIds.add(parsed.ThreadId as string);
        }
      }
      // All messages reference the same thread
      expect(threadIds.size).toBe(1);
    });

    it('should have consistent runId across all messages', () => {
      const recording = loadRecording();
      const runIds = new Set<string>();
      for (const { parsed } of recording) {
        if (parsed.runId) {
          runIds.add(parsed.runId as string);
        }
        if (parsed.RunId) {
          runIds.add(parsed.RunId as string);
        }
      }
      expect(runIds.size).toBe(1);
    });
  });

  describe('Message merging through replay', () => {
    it('should receive done signal', () => {
      const results = replayRecording();
      expect(results.doneReceived).toBe(true);
    });

    it('should produce exactly 1 run assignment', () => {
      const results = replayRecording();
      expect(results.runAssignments.length).toBe(1);
    });

    it('should produce exactly 1 run completed', () => {
      const results = replayRecording();
      expect(results.runCompleted.length).toBe(1);
    });

    it('should accumulate 3 distinct tool calls', () => {
      const results = replayRecording();
      expect(results.toolCallMessages.length).toBe(3);

      // All tool calls should be get_weather
      for (const tc of results.toolCallMessages) {
        expect(tc.function_name).toBe('get_weather');
      }
    });

    it('should accumulate valid JSON args for each tool call', () => {
      const results = replayRecording();

      for (const tc of results.toolCallMessages) {
        expect(tc.function_args).toBeTruthy();
        const args = JSON.parse(tc.function_args!);
        expect(args).toHaveProperty('location');
        expect(typeof args.location).toBe('string');
      }
    });

    it('should have tool calls targeting correct locations', () => {
      const results = replayRecording();
      const locations = results.toolCallMessages.map(
        (tc) => JSON.parse(tc.function_args!).location
      );

      // Turn 1: Seattle, San Francisco (parallel); Turn 2: Seattle
      expect(locations).toContain('Seattle');
      expect(locations).toContain('San Francisco');
    });

    it('should produce 3 tool call results', () => {
      const results = replayRecording();
      expect(results.toolCallResults.length).toBe(3);

      // Each result contains double-escaped JSON (string wrapping JSON string)
      for (const result of results.toolCallResults) {
        expect(result.result).toBeTruthy();
        const inner = JSON.parse(result.result);
        const parsed = typeof inner === 'string' ? JSON.parse(inner) : inner;
        expect(parsed).toHaveProperty('location');
        expect(parsed).toHaveProperty('temperature');
        expect(parsed).toHaveProperty('condition');
      }
    });

    it('should match tool call results to tool call IDs', () => {
      const results = replayRecording();
      const toolCallIds = new Set(results.toolCallMessages.map((tc) => tc.tool_call_id));
      const resultIds = new Set(results.toolCallResults.map((r) => r.tool_call_id));

      // Every result should correspond to a tool call
      for (const id of resultIds) {
        expect(toolCallIds.has(id)).toBe(true);
      }
    });
  });

  describe('Reasoning accumulation', () => {
    it('should produce reasoning messages across multiple generationIds', () => {
      const results = replayRecording();
      // 3 turns with reasoning: turn1 (30 words), turn2 (50 words), turn3 (10 words)
      expect(results.reasoningMessages.length).toBe(3);
    });

    it('should accumulate reasoning text by concatenation', () => {
      // Process only reasoning_update messages to verify accumulation
      const merger = useMessageMerger();
      const recording = loadRecording();
      const reasoningByGenId = new Map<string, string>();

      for (const { parsed } of recording) {
        if (parsed.$type !== 'reasoning_update') continue;
        const message = parsed as unknown as Message;
        const merged = merger.processUpdate(message);
        if (isReasoningMessage(merged) && merged.generationId) {
          reasoningByGenId.set(merged.generationId, merged.reasoning);
        }
      }

      // 3 turns of reasoning accumulation
      expect(reasoningByGenId.size).toBe(3);
      for (const text of reasoningByGenId.values()) {
        expect(text.length).toBeGreaterThan(10);
        // Accumulated updates contain plain lorem ipsum text
        expect(text).toContain('lorem');
      }
    });

    it('should have different generationIds for each turn reasoning', () => {
      const results = replayRecording();
      const genIds = results.reasoningMessages.map((r) => r.generationId);
      const uniqueGenIds = new Set(genIds);
      expect(uniqueGenIds.size).toBe(3);
    });
  });

  describe('Text accumulation', () => {
    it('should produce text messages from turns 2 and 3', () => {
      const results = replayRecording();
      // Turn 2 has text_message length=50, Turn 3 has text_message length=100
      expect(results.textMessages.length).toBe(2);
    });

    it('should accumulate text chunks by concatenation', () => {
      const results = replayRecording();

      for (const text of results.textMessages) {
        expect(text.text.length).toBeGreaterThan(10);
        // Text is lorem ipsum generated by TestSseMessageHandler
        expect(text.text).toContain('lorem');
      }
    });

    it('should have shorter text for turn 2 (50 words) and longer for turn 3 (100 words)', () => {
      const results = replayRecording();

      // Sort by text length to identify turn 2 vs turn 3
      const sorted = [...results.textMessages].sort((a, b) => a.text.length - b.text.length);
      // Turn 2 text (50 words) should be shorter than turn 3 (100 words)
      expect(sorted[0].text.length).toBeLessThan(sorted[1].text.length);
    });
  });

  describe('Multi-turn sequencing', () => {
    it('should have correct message ordering across turns', () => {
      const recording = loadRecording();
      const typeSequence = recording.map(({ parsed }) => parsed.$type as string);

      // First message should be run_assignment
      expect(typeSequence[0]).toBe('run_assignment');
      // Last two should be run_completed then done
      expect(typeSequence[typeSequence.length - 2]).toBe('run_completed');
      expect(typeSequence[typeSequence.length - 1]).toBe('done');
    });

    it('should have reasoning before tool calls in turn 1', () => {
      const recording = loadRecording();
      const firstReasoningIdx = recording.findIndex(
        ({ parsed }) => parsed.$type === 'reasoning_update'
      );
      const firstToolCallIdx = recording.findIndex(
        ({ parsed }) => parsed.$type === 'tool_call_update'
      );

      expect(firstReasoningIdx).toBeLessThan(firstToolCallIdx);
    });

    it('should have tool results before turn 2 reasoning', () => {
      const recording = loadRecording();

      // Find the second batch of reasoning updates (turn 2)
      // Turn 1 reasoning has genId from first reasoning_update
      const firstGenId = (recording[1].parsed as Record<string, unknown>).generationId;
      const turn2ReasoningIdx = recording.findIndex(
        ({ parsed }) =>
          parsed.$type === 'reasoning_update' && parsed.generationId !== firstGenId
      );

      // Find last tool_call_result before turn 2 reasoning
      const toolResultsBefore = recording
        .slice(0, turn2ReasoningIdx)
        .filter(({ parsed }) => parsed.$type === 'tool_call_result');

      // Turn 1 should have 2 tool results (Seattle + San Francisco) before turn 2 starts
      expect(toolResultsBefore.length).toBe(2);
    });

    it('should have text updates only in turns 2 and 3', () => {
      const recording = loadRecording();
      const textUpdates = recording.filter(({ parsed }) => parsed.$type === 'text_update');

      // All text updates should have generationIds different from turn 1
      const firstGenId = (recording[1].parsed as Record<string, unknown>).generationId;
      for (const { parsed } of textUpdates) {
        expect(parsed.generationId).not.toBe(firstGenId);
      }
    });
  });

  describe('generationId transitions across turns', () => {
    it('should use distinct generationIds for each turn', () => {
      const recording = loadRecording();
      const genIds = new Set<string>();

      for (const { parsed } of recording) {
        const genId = parsed.generationId as string | undefined;
        if (genId) {
          genIds.add(genId);
        }
      }

      // 3 turns = 3 distinct generation IDs
      expect(genIds.size).toBe(3);
    });

    it('should correctly reset accumulator on generationId change', () => {
      const merger = useMessageMerger();
      const recording = loadRecording();

      const textByGenId = new Map<string, string>();

      for (const { parsed } of recording) {
        if (parsed.$type === 'done') continue;
        const message = parsed as unknown as Message;
        const merged = merger.processUpdate(message);

        if (isTextMessage(merged) && merged.generationId) {
          textByGenId.set(merged.generationId, merged.text);
        }
      }

      // Each generation's text should be independently accumulated
      // They should not bleed into each other
      const texts = [...textByGenId.values()];
      expect(texts.length).toBe(2); // Only turns 2 and 3 have text
      for (const text of texts) {
        // Each text starts with lorem ipsum (not a continuation of prior text)
        expect(text.startsWith('lorem')).toBe(true);
      }
    });
  });
});
