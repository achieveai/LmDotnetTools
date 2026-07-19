import { describe, it, expect, beforeEach, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import SubAgentListPanel from '@/components/SubAgentListPanel.vue';
import type { SubAgentSummary } from '@/api/subAgentsApi';

// The panel is presentation-only: it drives the (separately tested) useSubAgentPanel composable and
// renders its reactive surface. We mock the composable so the component test is deterministic — no
// real fetch/WebSocket — and expose the returned api so a test can mutate its refs.
const panelState = vi.hoisted(() => ({ api: null as any }));

vi.mock('@/composables/useSubAgentPanel', async () => {
  const { ref } = await import('vue');
  const api = {
    children: ref([]),
    focusedAgentId: ref<string | null>(null),
    focusedDisplayItems: ref([]),
    isFocusedStreaming: ref(false),
    error: ref<string | null>(null),
    startPolling: vi.fn(),
    stopPolling: vi.fn(),
    refreshChildren: vi.fn(),
    focusChild: vi.fn(),
    unfocusChild: vi.fn(),
    sendToFocusedChild: vi.fn(),
    getResultForToolCall: vi.fn(() => null),
  };
  panelState.api = api;
  return { useSubAgentPanel: () => api };
});

function summary(agentId: string, overrides: Partial<SubAgentSummary> = {}): SubAgentSummary {
  return {
    agentId,
    name: `Agent ${agentId}`,
    template: 'research',
    task: 'do a lot of important work here',
    status: 'running',
    threadId: `subagent-${agentId}`,
    lastActivityUtc: null,
    ...overrides,
  };
}

// Stub the heavy child components so the panel test asserts wiring, not their internals.
const MessageListStub = {
  props: ['displayItems', 'isLoading'],
  template:
    '<div data-testid="stub-message-list" :data-count="displayItems.length" :data-loading="String(isLoading)"></div>',
};
const ChatInputStub = {
  props: ['disabled', 'streaming'],
  emits: ['send', 'cancel'],
  template:
    '<div data-testid="stub-chat-input" :data-streaming="String(streaming)">' +
    '<button data-testid="stub-send" @click="$emit(\'send\', \'hello child\')">send</button></div>',
};

function mountPanel(parentThreadId: string | null = 'parent-1') {
  return mount(SubAgentListPanel, {
    props: { parentThreadId },
    global: {
      stubs: { MessageList: MessageListStub, ChatInput: ChatInputStub },
    },
  });
}

beforeEach(() => {
  const api = panelState.api;
  api.children.value = [];
  api.focusedAgentId.value = null;
  api.focusedDisplayItems.value = [];
  api.isFocusedStreaming.value = false;
  api.error.value = null;
  api.startPolling.mockClear();
  api.stopPolling.mockClear();
  api.focusChild.mockClear();
  api.unfocusChild.mockClear();
  api.sendToFocusedChild.mockClear();
});

describe('SubAgentListPanel', () => {
  it('starts polling for sub-agents on mount', () => {
    mountPanel();
    expect(panelState.api.startPolling).toHaveBeenCalledTimes(1);
  });

  it('is collapsed by default: shows the toggle with the child count, panel hidden', () => {
    panelState.api.children.value = [summary('a1'), summary('a2')];
    const wrapper = mountPanel();

    const toggle = wrapper.get('[data-testid="subagent-panel-toggle"]');
    expect(toggle.text()).toContain('Sub-agents (2)');
    expect(wrapper.find('[data-testid="subagent-panel"]').exists()).toBe(false);
  });

  it('expands to show the list of children on toggle', async () => {
    panelState.api.children.value = [summary('a1'), summary('a2', { name: null, template: 'planner' })];
    const wrapper = mountPanel();

    await wrapper.get('[data-testid="subagent-panel-toggle"]').trigger('click');

    expect(wrapper.find('[data-testid="subagent-panel"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="subagent-list"]').exists()).toBe(true);

    const items = wrapper.findAll('[data-testid="subagent-item"]');
    expect(items).toHaveLength(2);
    expect(items[0].attributes('data-agent-id')).toBe('a1');
    expect(items[0].text()).toContain('Agent a1');
    expect(items[0].text()).toContain('running');
    // name falls back to the template when name is null.
    expect(items[1].text()).toContain('planner');
  });

  it('clicking a row focus button calls focusChild with the agent id', async () => {
    panelState.api.children.value = [summary('a1')];
    const wrapper = mountPanel();
    await wrapper.get('[data-testid="subagent-panel-toggle"]').trigger('click');

    await wrapper.get('[data-testid="subagent-focus-button"]').trigger('click');
    expect(panelState.api.focusChild).toHaveBeenCalledWith('a1');
  });

  it('renders the transcript and a send-only input when a child is focused', async () => {
    panelState.api.children.value = [summary('a1')];
    panelState.api.focusedDisplayItems.value = [{ type: 'user-message' }, { type: 'assistant-message' }];
    panelState.api.isFocusedStreaming.value = true;
    const wrapper = mountPanel();
    await wrapper.get('[data-testid="subagent-panel-toggle"]').trigger('click');

    // Focus a child (simulate the composable setting focusedAgentId).
    panelState.api.focusedAgentId.value = 'a1';
    await nextTick();

    expect(wrapper.find('[data-testid="subagent-unfocus-button"]').exists()).toBe(true);

    const transcript = wrapper.get('[data-testid="subagent-transcript"]');
    const list = transcript.get('[data-testid="stub-message-list"]');
    expect(list.attributes('data-count')).toBe('2');
    expect(list.attributes('data-loading')).toBe('true');

    // The input is send-only: ChatInput is not put into streaming mode, so it never shows Stop.
    const input = wrapper.get('[data-testid="subagent-input"]');
    expect(input.get('[data-testid="stub-chat-input"]').attributes('data-streaming')).toBe('false');
    expect(wrapper.find('[data-testid="stop-button"]').exists()).toBe(false);
    // The focused row is highlighted.
    expect(wrapper.get('[data-testid="subagent-item"]').classes()).toContain('focused');
  });

  it('@send from the focused input forwards text to sendToFocusedChild', async () => {
    panelState.api.children.value = [summary('a1')];
    const wrapper = mountPanel();
    await wrapper.get('[data-testid="subagent-panel-toggle"]').trigger('click');
    panelState.api.focusedAgentId.value = 'a1';
    await nextTick();

    await wrapper.get('[data-testid="subagent-input"] [data-testid="stub-send"]').trigger('click');
    expect(panelState.api.sendToFocusedChild).toHaveBeenCalledWith('hello child');
  });

  it('the unfocus button calls unfocusChild', async () => {
    panelState.api.children.value = [summary('a1')];
    const wrapper = mountPanel();
    await wrapper.get('[data-testid="subagent-panel-toggle"]').trigger('click');
    panelState.api.focusedAgentId.value = 'a1';
    await nextTick();

    await wrapper.get('[data-testid="subagent-unfocus-button"]').trigger('click');
    expect(panelState.api.unfocusChild).toHaveBeenCalledTimes(1);
  });

  it('never renders a Stop control (the child panel is send-only)', async () => {
    panelState.api.children.value = [summary('a1')];
    const wrapper = mountPanel();
    await wrapper.get('[data-testid="subagent-panel-toggle"]').trigger('click');
    panelState.api.focusedAgentId.value = 'a1';
    await nextTick();

    expect(wrapper.html()).not.toContain('data-testid="stop-button"');
  });

  it('surfaces the composable error when present', async () => {
    const wrapper = mountPanel();
    await wrapper.get('[data-testid="subagent-panel-toggle"]').trigger('click');
    expect(wrapper.find('[data-testid="subagent-error"]').exists()).toBe(false);

    panelState.api.error.value = 'Failed to list sub-agents: boom';
    await nextTick();

    const banner = wrapper.get('[data-testid="subagent-error"]');
    expect(banner.text()).toContain('Failed to list sub-agents: boom');
  });

  it('surfaces a relay_failed stream error in the banner (FINDING C)', async () => {
    const wrapper = mountPanel();
    await wrapper.get('[data-testid="subagent-panel-toggle"]').trigger('click');
    expect(wrapper.find('[data-testid="subagent-error"]').exists()).toBe(false);

    // The composable copies a relay_failed stream error into its public `error` ref.
    panelState.api.error.value = "Failed to relay the message to sub-agent 'a1'. Please retry.";
    await nextTick();

    const banner = wrapper.get('[data-testid="subagent-error"]');
    expect(banner.text()).toContain('Failed to relay the message');
  });
});
