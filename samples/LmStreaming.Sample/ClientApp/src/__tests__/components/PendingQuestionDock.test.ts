import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import PendingQuestionDock from '@/components/PendingQuestionDock.vue';
import { GET_RESULT_FOR_TOOL_CALL } from '@/composables/useToolResult';
import { SUBMIT_CLIENT_TOOL_RESULT } from '@/composables/useClientToolSubmit';
import { MessageType } from '@/types';
import type { DisplayItem, ToolCall, ToolCallResultMessage } from '@/types';

/**
 * The dock is the surface the user actually answers on, so the two things that must hold are:
 * it appears with a LIVE form when a question is pending, and it disappears the moment the
 * canonical result lands (a stale form parked over the input would let someone answer a question
 * that is already closed).
 *
 * It resolves results and submits through INJECTED providers, never props — that is what makes it
 * route correctly in both the main chat (root socket) and a sub-agent tab, where
 * SubAgentTranscript shadows both providers with the focused child's own.
 */
describe('PendingQuestionDock', () => {
  const ARGS = JSON.stringify({
    context: 'Need your input',
    questions: [{ prompt: 'Pick a colour', options: [{ label: 'Blue', value: 'blue' }] }],
  });

  function call(id: string): ToolCall {
    return { tool_call_id: id, function_name: 'AskUserQuestion', function_args: ARGS };
  }

  function pill(id: string, ...toolCalls: ToolCall[]): DisplayItem {
    return {
      type: 'pill',
      id,
      items: [{ $type: MessageType.ToolsCall, role: 'assistant', tool_calls: toolCalls } as never],
    } as DisplayItem;
  }

  function deferred(id: string): ToolCallResultMessage {
    return {
      $type: MessageType.ToolCallResult,
      tool_call_id: id,
      result: '',
      is_error: false,
      is_deferred: true,
      role: 'tool',
    };
  }

  function mountDock(
    displayItems: DisplayItem[],
    results: Record<string, ToolCallResultMessage>,
    submit = async () => ({ status: 'acked' as const })
  ) {
    return mount(PendingQuestionDock, {
      props: { displayItems },
      global: {
        provide: {
          [GET_RESULT_FOR_TOOL_CALL]: (id: string | null | undefined) =>
            (id ? results[id] : null) ?? null,
          [SUBMIT_CLIENT_TOOL_RESULT]: submit,
        },
      },
    });
  }

  it('renders nothing at all when no question is pending', () => {
    const w = mountDock([pill('p1', call('q1'))], {});
    expect(w.find('[data-testid="question-dock"]').exists()).toBe(false);
    // Not merely hidden: an empty dock must cost zero vertical space above the input.
    expect(w.html()).toBe('<!--v-if-->');
  });

  it('renders a live, answerable form for a pending question', () => {
    const w = mountDock([pill('p1', call('q1'))], { q1: deferred('q1') });
    expect(w.find('[data-testid="question-dock"]').exists()).toBe(true);
    expect(w.find('[data-testid="question-form"]').exists()).toBe(true);
    expect(w.find('[data-testid="question-option-blue"]').exists()).toBe(true);
    expect(w.text()).toContain('Pick a colour');
  });

  it('disappears once the canonical result resolves the question', async () => {
    const results: Record<string, ToolCallResultMessage> = { q1: deferred('q1') };
    const w = mountDock([pill('p1', call('q1'))], results);
    expect(w.find('[data-testid="question-dock"]').exists()).toBe(true);

    // Same tool_call_id republished with the real answer — the placeholder is overwritten.
    results.q1 = {
      ...deferred('q1'),
      result: JSON.stringify({ answers: [{ questionId: 'q0', selectedValues: ['blue'], otherText: '', skipped: false }] }),
      is_deferred: false,
    };
    await w.setProps({ displayItems: [pill('p1', call('q1'))] });
    expect(w.find('[data-testid="question-dock"]').exists()).toBe(false);
  });

  it('submits through the INJECTED client-tool function, with the answering call id', async () => {
    // This is the routing guarantee in miniature: whichever provider is in scope receives the
    // answer. In a sub-agent tab that provider is the focused child's socket, not the root's —
    // the root does not know a descendant's toolCallId and would reply `not_found`.
    const seen: Array<{ id: string; payload: string; isError?: boolean }> = [];
    const submit = async (id: string, payload: string, isError?: boolean) => {
      seen.push({ id, payload, isError });
      return { status: 'acked' as const };
    };
    const w = mountDock([pill('p1', call('q1'))], { q1: deferred('q1') }, submit);

    await w.get('[data-testid="question-option-blue"] input').setValue(true);
    await w.get('[data-testid="question-submit"]').trigger('click');

    expect(seen).toHaveLength(1);
    expect(seen[0].id).toBe('q1');
    expect(JSON.parse(seen[0].payload).answers[0].selectedValues).toEqual(['blue']);
  });

  it('docks every distinct pending question, oldest first', () => {
    const w = mountDock([pill('p1', call('q1')), pill('p2', call('q2'))], {
      q1: deferred('q1'),
      q2: deferred('q2'),
    });
    expect(w.findAll('[data-testid="question-rich"]')).toHaveLength(2);
  });
});
