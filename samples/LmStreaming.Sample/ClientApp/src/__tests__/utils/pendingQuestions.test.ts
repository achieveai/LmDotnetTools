import { describe, it, expect } from 'vitest';
import {
  collectAnsweredQuestionIds,
  findPendingQuestions,
  isQuestionAwaitingAnswer,
  parseUserAnswerMessage,
} from '@/utils/pendingQuestions';
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

  // Bug #5: when something else arrives while the run is parked on a question, the server settles
  // the call EARLY with a non-deferred placeholder and delivers the real answer later as a
  // `<user-answer …>` user message. The placeholder must not read as "answered".
  describe('early-settled questions', () => {
    function earlySettled(id: string): ToolCallResultMessage {
      return {
        ...result(id, false),
        result: JSON.stringify({ status: 'deferred_to_notification', message: 'Question sent to user' }),
      };
    }

    function userAnswer(id: string): DisplayItem {
      return {
        type: 'user-message',
        id: `u-${id}`,
        content: { $type: MessageType.Text, role: 'user', text: '<user-answer tool="AskUserQuestion" tool-call-id="' + id + '">\n<request>\nctx\n- (q) Pick one\n  options: A | B\n</request>\n<answer>\n{"answers":[{"questionId":"q","selectedValues":["A"]}]}\n</answer>\n</user-answer>' },
      } as DisplayItem;
    }

    it('keeps a question whose placeholder result was settled early — the user still owes an answer', () => {
      const found = findPendingQuestions([pill('p1', call('q1'))], lookup({ q1: earlySettled('q1') }));
      expect(found.map((f) => f.id)).toEqual(['q1']);
    });

    it('drops it once a later user-answer message for the same tool_call_id is in the transcript', () => {
      const items = [pill('p1', call('q1')), userAnswer('q1')];
      expect(findPendingQuestions(items, lookup({ q1: earlySettled('q1') }))).toEqual([]);
    });

    it('does not let an answer to a different question close it', () => {
      const items = [pill('p1', call('q1')), userAnswer('q2')];
      expect(findPendingQuestions(items, lookup({ q1: earlySettled('q1') })).map((f) => f.id)).toEqual(['q1']);
    });

    it('drops it when the caller reports it answered (optimistic close after an acked submit)', () => {
      const items = [pill('p1', call('q1'))];
      expect(findPendingQuestions(items, lookup({ q1: earlySettled('q1') }), (id) => id === 'q1')).toEqual([]);
    });

    it('isQuestionAwaitingAnswer: deferred → open; early-settled → open until answered; real answer → closed', () => {
      expect(isQuestionAwaitingAnswer(result('q1', true))).toBe(true);
      expect(isQuestionAwaitingAnswer(earlySettled('q1'))).toBe(true);
      expect(isQuestionAwaitingAnswer(earlySettled('q1'), (id) => id === 'q1')).toBe(false);
      expect(isQuestionAwaitingAnswer(result('q1', false))).toBe(false);
      expect(isQuestionAwaitingAnswer(null)).toBe(false);
      // A cancellation shaped like text, not the placeholder, is closed.
      expect(isQuestionAwaitingAnswer({ ...result('q1', false), result: 'cancelled', is_error: true })).toBe(false);
    });

    it('parses the injected user-answer envelope into request and answer, and collects answered ids', () => {
      const parsed = parseUserAnswerMessage((userAnswer('q1') as { content: { text: string } }).content.text);
      expect(parsed).toEqual({
        tool: 'AskUserQuestion',
        toolCallId: 'q1',
        request: 'ctx\n- (q) Pick one\n  options: A | B',
        answer: '{"answers":[{"questionId":"q","selectedValues":["A"]}]}',
      });
      expect(parseUserAnswerMessage('hello <user-answer tool-call-id="x">')).toBeNull();
      expect(parseUserAnswerMessage('<user-answer tool-call-id="x">truncated')).toBeNull();
      expect([...collectAnsweredQuestionIds([pill('p1', call('q1')), userAnswer('q1'), userAnswer('q3')])]).toEqual([
        'q1',
        'q3',
      ]);
    });
  });
});
