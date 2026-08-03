import { describe, it, expect } from 'vitest';
import { defineComponent, h } from 'vue';
import { mount } from '@vue/test-utils';
import {
  useClientToolSubmit,
  SUBMIT_CLIENT_TOOL_RESULT,
  type ClientToolSubmitFn,
  type ClientToolSubmitOutcome,
} from '@/composables/useClientToolSubmit';

function harness(provideFn?: ClientToolSubmitFn) {
  const captured: { submit?: ClientToolSubmitFn } = {};
  const Comp = defineComponent({
    setup() {
      captured.submit = useClientToolSubmit().submit;
      return () => h('div');
    },
  });
  mount(Comp, provideFn ? { global: { provide: { [SUBMIT_CLIENT_TOOL_RESULT]: provideFn } } } : undefined);
  return captured;
}

describe('useClientToolSubmit (#246)', () => {
  it('delegates to the injected submit function and returns its outcome', async () => {
    const acked: ClientToolSubmitOutcome = { status: 'acked', duplicate: false };
    const fn: ClientToolSubmitFn = async (toolCallId, result, isError) => {
      expect(toolCallId).toBe('call-1');
      expect(result).toBe('{"answers":[]}');
      expect(isError).toBeUndefined();
      return acked;
    };
    const c = harness(fn);
    const outcome = await c.submit!('call-1', '{"answers":[]}');
    expect(outcome).toEqual(acked);
  });

  it('falls back to a rejection-shaped error outcome when no provider is present (fails loudly, never hangs)', async () => {
    const c = harness();
    const outcome = await c.submit!('call-1', '{}');
    expect(outcome).toEqual({
      status: 'error',
      code: 'not_connected',
      message: 'No submit handler provided',
    });
  });
});
