import { describe, it, expect } from 'vitest';
import { findPendingQuestions } from '@/utils/pendingQuestions';
import { MessageType } from '@/types';
import type { DisplayItem, ToolCall, ToolCallResultMessage } from '@/types';

/**
 * The dock is only as good as this scan: miss a pending question and the user is parked on an
 * invisible prompt with no way to unblock the run; surface a resolved one and a dead form sits
 * over the input forever.
 */
describe('findPendingQuestions', () => {
  const ARGS = JSON.stringify({
    context: 'ctx',
    questions: [{ prompt: 'Pick one', options: [{ label: 'A' }, { label: 'B' }] }],
  });

  function call(id: string, name = 'AskUserQuestion'): ToolCall {
    return { tool_call_id: id, function_name: name, function_args: ARGS };
  }

  function result(id: string, isDeferred: boolean): ToolCallResultMessage {
    return {
      $type: MessageType.ToolCallResult,
      tool_call_id: id,
      result: isDeferred ? '' : JSON.stringify({ answers: [] }),
      is_error: false,
      is_deferred: isDeferred,
      role: 'tool',
    };
  }

  function pill(id: string, ...toolCalls: ToolCall[]): DisplayItem {
    return {
      type: 'pill',
      id,
      items: [{ $type: MessageType.ToolsCall, role: 'assistant', tool_calls: toolCalls } as never],
    } as DisplayItem;
  }

  /** Lookup over a map of id → result; ids absent from the map have no result at all. */
  function lookup(map: Record<string, ToolCallResultMessage>) {
    return (id: string | null | undefined) => (id ? map[id] ?? null : null);
  }

  it('finds a question whose result is still deferred', () => {
    const found = findPendingQuestions([pill('p1', call('q1'))], lookup({ q1: result('q1', true) }));
    expect(found.map((f) => f.id)).toEqual(['q1']);
    expect(found[0].toolCall.function_name).toBe('AskUserQuestion');
  });

  it('drops a question once its real result overwrites the placeholder', () => {
    const found = findPendingQuestions([pill('p1', call('q1'))], lookup({ q1: result('q1', false) }));
    expect(found).toEqual([]);
  });

  it('ignores a question with no result yet — it is still streaming, not awaiting an answer', () => {
    expect(findPendingQuestions([pill('p1', call('q1'))], lookup({}))).toEqual([]);
  });

  it('ignores a deferred result belonging to some other tool family', () => {
    const found = findPendingQuestions(
      [pill('p1', call('t1', 'Bash'))],
      lookup({ t1: result('t1', true) })
    );
    expect(found).toEqual([]);
  });

  it('matches sandbox-prefixed and oddly-cased spellings of the same tool', () => {
    for (const name of ['sandbox-AskUserQuestion', 'askuserquestion', 'SANDBOX-ASKUSERQUESTION']) {
      const found = findPendingQuestions(
        [pill('p1', call('q1', name))],
        lookup({ q1: result('q1', true) })
      );
      expect(found.map((f) => f.id), name).toEqual(['q1']);
    }
  });

  it('skips a tool call with no tool_call_id — it could never be answered', () => {
    const orphan: ToolCall = { function_name: 'AskUserQuestion', function_args: ARGS };
    expect(findPendingQuestions([pill('p1', orphan)], lookup({}))).toEqual([]);
  });

  it('deduplicates the same call replayed across pills, so only ONE live form is docked', () => {
    // Streaming resume replays a pill the transcript already holds; two cards would mean two
    // forms racing to answer the same tool_call_id.
    const found = findPendingQuestions(
      [pill('p1', call('q1')), pill('p2', call('q1'))],
      lookup({ q1: result('q1', true) })
    );
    expect(found.map((f) => f.id)).toEqual(['q1']);
  });

  it('returns multiple distinct questions in transcript order', () => {
    const found = findPendingQuestions(
      [pill('p1', call('q1')), pill('p2', call('q2'))],
      lookup({ q1: result('q1', true), q2: result('q2', true) })
    );
    expect(found.map((f) => f.id)).toEqual(['q1', 'q2']);
  });

  it('ignores non-pill display items and pill items that carry no tool calls', () => {
    const items: DisplayItem[] = [
      { type: 'assistant-message', id: 'a1', content: { text: 'hi' } } as never,
      { type: 'pill', id: 'p1', items: [{ $type: MessageType.Reasoning, role: 'assistant' } as never] } as DisplayItem,
      pill('p2', call('q1')),
    ];
    expect(findPendingQuestions(items, lookup({ q1: result('q1', true) })).map((f) => f.id)).toEqual([
      'q1',
    ]);
  });

  it('returns nothing for an empty transcript', () => {
    expect(findPendingQuestions([], lookup({}))).toEqual([]);
  });
});
