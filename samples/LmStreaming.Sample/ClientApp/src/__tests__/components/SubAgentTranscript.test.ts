import { describe, it, expect, vi } from 'vitest';
import { inject } from 'vue';
import { mount } from '@vue/test-utils';
import SubAgentTranscript from '@/components/SubAgentTranscript.vue';
import { GET_RESULT_FOR_TOOL_CALL } from '@/composables/useToolResult';
import { SUBMIT_CLIENT_TOOL_RESULT, type ClientToolSubmitFn } from '@/composables/useClientToolSubmit';
import { MessageType, type ToolCallResultMessage } from '@/types';

// The real MessageList (mounted in the last describe) observes its scroll container.
(globalThis as any).ResizeObserver = class ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
};

// Stub MessageList so we assert wiring; it also injects the child tool-result resolver AND the
// child-scoped submit function to prove the subtree provide points at the FOCUSED CHILD connection
// (#246 defect 1), not the root chat's SUBMIT_CLIENT_TOOL_RESULT.
const MessageListStub = {
  props: ['displayItems', 'isLoading'],
  setup() {
    const resolver = inject<(id: string | null | undefined) => ToolCallResultMessage | null>(
      GET_RESULT_FOR_TOOL_CALL,
      () => null
    );
    const resolved = resolver('tc-1');
    const submit = inject<ClientToolSubmitFn>(SUBMIT_CLIENT_TOOL_RESULT, () =>
      Promise.resolve({ status: 'error', code: 'not_connected', message: 'no provider' })
    );
    return { marker: resolved ? resolved.result : 'none', submit };
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
      submitClientToolResult: vi.fn(() => Promise.resolve({ status: 'acked', duplicate: false })),
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

  it('provides the CHILD-scoped submitClientToolResult to its subtree (#246 defect 1)', async () => {
    const childSubmit = vi.fn<ClientToolSubmitFn>(() =>
      Promise.resolve({ status: 'acked', duplicate: false })
    );
    mount(SubAgentTranscript, {
      props: {
        activeAgentId: 'a1',
        focusedAgentId: 'a1',
        displayItems: [],
        isStreaming: false,
        error: null,
        getResultForToolCall: vi.fn(() => null),
        submitClientToolResult: childSubmit,
      } as never,
      global: {
        stubs: {
          MessageList: {
            setup() {
              const submit = inject<ClientToolSubmitFn>(SUBMIT_CLIENT_TOOL_RESULT);
              // Prove it is the CHILD-scoped function passed in as a prop, not some root fallback.
              void submit?.('call-1', '{}', false);
              return {};
            },
            template: '<div />',
          },
          ChatInput: ChatInputStub,
        },
      },
    });
    expect(childSubmit).toHaveBeenCalledWith('call-1', '{}', false);
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

  // Every other test here stubs MessageList, which is exactly how the streaming-highlight opt-out
  // shipped broken for this consumer: MessageList's own tests were green, but they all had a user
  // message, and a sub-agent transcript never does. This one mounts the REAL MessageList (and the
  // real TextMessage under it) so the second consumer is covered end-to-end.
  describe('streaming highlight opt-out (real MessageList)', () => {
    const FENCE = ['```csharp', 'var s = "a & b";', 'if (x is null) { }', '```'].join('\n');
    const tokenSpans = (html: string) => (html.match(/class="hljs-/g) || []).length;

    const assistantItems = () => [
      {
        id: 'c-1',
        type: 'assistant-message',
        content: { $type: MessageType.Text, role: 'assistant', text: FENCE, isThinking: false },
      },
    ];

    function mountReal(isStreaming: boolean) {
      return mount(SubAgentTranscript, {
        props: {
          activeAgentId: 'a1',
          focusedAgentId: 'a1',
          displayItems: assistantItems(),
          isStreaming,
          error: null,
          getResultForToolCall: vi.fn(() => null),
          submitClientToolResult: vi.fn(() => Promise.resolve({ status: 'acked', duplicate: false })),
        } as never,
        global: { stubs: { ChatInput: ChatInputStub } },
      });
    }

    it('renders the growing child bubble UNHIGHLIGHTED while the sub-agent streams', () => {
      const wrapper = mountReal(true);
      // The block itself still renders; only the per-token spans are skipped.
      expect(wrapper.html()).toContain('hljs language-csharp');
      expect(tokenSpans(wrapper.html())).toBe(0);
    });

    it('highlights the child bubble once the sub-agent run ends', () => {
      expect(tokenSpans(mountReal(false).html())).toBeGreaterThan(0);
    });
  });
});
