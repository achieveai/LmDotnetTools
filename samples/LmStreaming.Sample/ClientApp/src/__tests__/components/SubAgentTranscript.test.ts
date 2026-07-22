import { describe, it, expect, vi } from 'vitest';
import { inject } from 'vue';
import { mount } from '@vue/test-utils';
import SubAgentTranscript from '@/components/SubAgentTranscript.vue';
import { GET_RESULT_FOR_TOOL_CALL } from '@/composables/useToolResult';
import type { ToolCallResultMessage } from '@/types';

// Stub MessageList so we assert wiring; it also injects the child tool-result resolver to prove the
// subtree provide points at the child (not the parent chat).
const MessageListStub = {
  props: ['displayItems', 'isLoading'],
  setup() {
    const resolver = inject<(id: string | null | undefined) => ToolCallResultMessage | null>(
      GET_RESULT_FOR_TOOL_CALL,
      () => null
    );
    const resolved = resolver('tc-1');
    return { marker: resolved ? resolved.result : 'none' };
  },
  template:
    '<div data-testid="stub-ml" :data-count="displayItems.length" :data-loading="String(isLoading)" :data-marker="marker"></div>',
};

const ChatInputStub = {
  props: ['disabled', 'streaming'],
  emits: ['send', 'cancel'],
  template:
    '<div data-testid="stub-input" :data-disabled="String(disabled)" :data-streaming="String(streaming)">' +
    '<button data-testid="stub-send" @click="$emit(\'send\', \'hi child\')">send</button></div>',
};

function mountView(props: Partial<Record<string, unknown>> = {}) {
  return mount(SubAgentTranscript, {
    props: {
      activeAgentId: 'a1',
      focusedAgentId: 'a1',
      displayItems: [{ type: 'user-message' }, { type: 'assistant-message' }],
      isStreaming: true,
      error: null,
      getResultForToolCall: vi.fn(() => null),
      ...props,
    } as never,
    global: { stubs: { MessageList: MessageListStub, ChatInput: ChatInputStub } },
  });
}

describe('SubAgentTranscript', () => {
  it('renders the transcript MessageList with the display items and streaming state', () => {
    const wrapper = mountView();
    const ml = wrapper.get('[data-testid="subagent-transcript"] [data-testid="stub-ml"]');
    expect(ml.attributes('data-count')).toBe('2');
    expect(ml.attributes('data-loading')).toBe('true');
  });

  it('provides the CHILD tool-result resolver to its subtree (not the parent chat)', () => {
    const childResolver = (id: string | null | undefined): ToolCallResultMessage | null =>
      id === 'tc-1' ? ({ result: 'CHILD-RESULT' } as ToolCallResultMessage) : null;
    const wrapper = mountView({ getResultForToolCall: childResolver });
    expect(wrapper.get('[data-testid="stub-ml"]').attributes('data-marker')).toBe('CHILD-RESULT');
  });

  it('surfaces a relay_failed stream error in the banner (FINDING C, relocated from the panel)', () => {
    const wrapper = mountView({ error: "Failed to relay the message to sub-agent 'a1'. Please retry." });
    const banner = wrapper.get('[data-testid="subagent-error"]');
    expect(banner.text()).toContain('Failed to relay the message');
  });

  it('has no error banner when there is no error', () => {
    const wrapper = mountView({ error: null });
    expect(wrapper.find('[data-testid="subagent-error"]').exists()).toBe(false);
  });

  it('input is send-only (never streaming, so never a Stop control)', () => {
    const wrapper = mountView();
    expect(wrapper.get('[data-testid="stub-input"]').attributes('data-streaming')).toBe('false');
  });

  it('disables the input until the live connection for this tab is attached', () => {
    // focus not yet on this tab -> disabled
    const notReady = mountView({ focusedAgentId: null });
    expect(notReady.get('[data-testid="stub-input"]').attributes('data-disabled')).toBe('true');
    // focused on this exact tab -> enabled
    const ready = mountView({ focusedAgentId: 'a1' });
    expect(ready.get('[data-testid="stub-input"]').attributes('data-disabled')).toBe('false');
  });

  it('forwards @send text to the parent', async () => {
    const wrapper = mountView();
    await wrapper.get('[data-testid="stub-send"]').trigger('click');
    expect(wrapper.emitted('send')).toEqual([['hi child']]);
  });
});
