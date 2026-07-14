import { describe, it, expect } from 'vitest';
import { defineComponent, h } from 'vue';
import { mount } from '@vue/test-utils';
import { useToolResult, GET_RESULT_FOR_TOOL_CALL } from '@/composables/useToolResult';
import { MessageType, type ToolCall, type ToolCallResultMessage } from '@/types';

const result: ToolCallResultMessage = {
  $type: MessageType.ToolCallResult,
  tool_call_id: 'abc',
  result: 'the result',
  role: 'tool',
};

function harness(provideFn: unknown) {
  const captured: { getResult?: (tc: ToolCall) => ToolCallResultMessage | null } = {};
  const Comp = defineComponent({
    setup() {
      const { getResult } = useToolResult();
      captured.getResult = getResult;
      return () => h('div');
    },
  });
  mount(Comp, { global: { provide: { [GET_RESULT_FOR_TOOL_CALL]: provideFn } } });
  return captured;
}

describe('useToolResult', () => {
  it('resolves a result by tool_call_id via the injected lookup', () => {
    const c = harness((id: string | null | undefined) => (id === 'abc' ? result : null));
    expect(c.getResult!({ tool_call_id: 'abc' })).toBe(result);
    expect(c.getResult!({ tool_call_id: 'other' })).toBeNull();
  });

  it('defaults to null when no provider is present (no throw)', () => {
    const captured: { getResult?: (tc: ToolCall) => ToolCallResultMessage | null } = {};
    const Comp = defineComponent({
      setup() {
        captured.getResult = useToolResult().getResult;
        return () => h('div');
      },
    });
    mount(Comp);
    expect(captured.getResult!({ tool_call_id: 'abc' })).toBeNull();
  });
});
