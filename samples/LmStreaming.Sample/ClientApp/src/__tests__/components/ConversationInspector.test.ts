import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ConversationInspector from '@/components/ConversationInspector.vue';
import { TodoStatus, type TodoTask } from '@/types/todo';
import type { SubAgentSummary } from '@/api/subAgentsApi';

const task: TodoTask = {
  id: '1', status: TodoStatus.Completed, title: 'Finished work', notes: [], artifacts: ['report.md'], subTasks: [],
};
const child: SubAgentSummary = {
  agentId: 'agent-1', name: 'Reviewer', template: 'review', task: 'Review it', status: 'running', threadId: 'child-1', lastActivityUtc: null,
};

function mountInspector(overrides: Record<string, unknown> = {}) {
  return mount(ConversationInspector, {
    attachTo: document.body,
    props: {
      open: true,
      activeSection: 'work',
      tasks: [task],
      hasWork: true,
      children: [child],
      activeConversationTabId: 'main',
      ...overrides,
    },
  });
}

describe('ConversationInspector', () => {
  beforeEach(() => {
    Object.defineProperty(window, 'innerWidth', { configurable: true, writable: true, value: 1200 });
  });
  afterEach(() => document.body.replaceChildren());

  it('renders no rail at all while closed', () => {
    const wrapper = mountInspector({ open: false });
    expect(wrapper.find('[data-testid="conversation-inspector"]').exists()).toBe(false);
  });

  it('uses an icon-only close control with a clear accessible name and tooltip', () => {
    const wrapper = mountInspector();
    const close = wrapper.get('.inspector-close');

    expect(close.attributes('aria-label')).toBe('Close Work and agents');
    expect(close.attributes('title')).toBe('Close Work and agents');
    expect(close.text()).toBe('');
    expect(close.find('svg[aria-hidden="true"]').exists()).toBe(true);
  });

  it('shows exactly one embedded renderer and preserves artifact events', async () => {
    const wrapper = mountInspector();
    expect(wrapper.find('[data-testid="todo-panel"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="todo-panel-toggle"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="subagent-panel"]').exists()).toBe(false);
    await wrapper.get('[data-testid="todo-artifact-chip"]').trigger('click');
    expect(wrapper.emitted('openArtifact')).toEqual([['report.md']]);
  });

  it('supports roving tab focus with arrows, Home and End', async () => {
    const wrapper = mountInspector();
    const tabs = wrapper.findAll('[role="tab"]');
    expect(tabs[0].attributes('aria-selected')).toBe('true');
    await tabs[0].trigger('keydown', { key: 'ArrowRight' });
    expect(wrapper.emitted('selectSection')?.at(-1)).toEqual(['agents']);
    await wrapper.setProps({ activeSection: 'agents' });
    expect(document.activeElement).toBe(wrapper.findAll('[role="tab"]')[1].element);
    await wrapper.findAll('[role="tab"]')[1].trigger('keydown', { key: 'Home' });
    expect(wrapper.emitted('selectSection')?.at(-1)).toEqual(['work']);
    await wrapper.findAll('[role="tab"]')[1].trigger('keydown', { key: 'End' });
    expect(wrapper.emitted('selectSection')?.at(-1)).toEqual(['agents']);
  });

  it('uses a non-modal dock on wide screens and a modal drawer at 1100px', async () => {
    const wrapper = mountInspector();
    expect(wrapper.get('[data-testid="conversation-inspector"]').attributes('role')).toBeUndefined();
    Object.defineProperty(window, 'innerWidth', { configurable: true, writable: true, value: 1100 });
    window.dispatchEvent(new Event('resize'));
    await wrapper.vm.$nextTick();
    expect(wrapper.get('[data-testid="conversation-inspector"]').attributes('role')).toBe('dialog');
    expect(wrapper.get('[data-testid="conversation-inspector"]').attributes('aria-modal')).toBe('true');
    expect(document.activeElement).toBe(wrapper.findAll('[role="tab"]')[0].element);
    expect(wrapper.get('[data-testid="conversation-inspector-backdrop"]').attributes('tabindex')).toBe('-1');
    await wrapper.get('[data-testid="conversation-inspector-backdrop"]').trigger('click');
    expect(wrapper.emitted('close')).toHaveLength(1);
  });

  it('contains forward and reverse focus in the empty overlay pane', async () => {
    Object.defineProperty(window, 'innerWidth', { configurable: true, writable: true, value: 900 });
    const wrapper = mountInspector({ tasks: [], hasWork: false });
    await wrapper.vm.$nextTick();
    const activeTab = wrapper.findAll('[role="tab"]')[0];
    const close = wrapper.get('.inspector-close');
    expect(document.activeElement).toBe(activeTab.element);
    await activeTab.trigger('keydown', { key: 'Tab' });
    expect(document.activeElement).toBe(close.element);
    await close.trigger('keydown', { key: 'Tab', shiftKey: true });
    expect(document.activeElement).toBe(activeTab.element);
  });

  it('closes on Escape and emits selected agents through the existing row', async () => {
    const wrapper = mountInspector({ activeSection: 'agents' });
    await wrapper.get('[data-testid="conversation-inspector"]').trigger('keydown', { key: 'Escape' });
    expect(wrapper.emitted('close')).toHaveLength(1);
    await wrapper.get('[data-testid="subagent-focus-button"]').trigger('click');
    expect(wrapper.emitted('selectAgent')).toEqual([['agent-1', false]]);
  });

  it('marks an agent selection for drawer close only in the narrow overlay', async () => {
    Object.defineProperty(window, 'innerWidth', { configurable: true, writable: true, value: 900 });
    const wrapper = mountInspector({ activeSection: 'agents' });
    await wrapper.get('[data-testid="subagent-focus-button"]').trigger('click');
    expect(wrapper.emitted('selectAgent')).toEqual([['agent-1', true]]);
  });

  it('renders clean empty states and live counts', async () => {
    const wrapper = mountInspector({ tasks: [], hasWork: false, children: [] });
    expect(wrapper.text()).toContain('No work yet.');
    expect(wrapper.findAll('[role="tab"]')[1].text()).toContain('0');
    await wrapper.setProps({ activeSection: 'agents' });
    expect(wrapper.text()).toContain('No sub-agents yet.');
  });
});
