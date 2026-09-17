import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ConversationTabs from '@/components/ConversationTabs.vue';
import type { ConversationTab } from '@/composables/useConversationTabs';

const tab = (id: string, status: ConversationTab['status'] = 'running', overrides: Partial<ConversationTab> = {}): ConversationTab => ({
  id, label: `Agent ${id}`, kind: 'subagent', color: '#2563eb', status, ...overrides,
});
const TABS: ConversationTab[] = [
  { id: 'main', label: 'main', kind: 'main', color: null },
  tab('a1'), tab('a2', 'completed'), tab('a3', 'error', { failureCode: 'view_exceeds_window' }),
  tab('a4', 'interrupted'), tab('wf', 'running', { kind: 'workflow', label: 'Nightly report' }),
];

describe('ConversationTabs agent picker', () => {
  it('keeps stable Main and current-agent anchors for existing navigation', () => {
    const wrapper = mount(ConversationTabs, { props: { tabs: TABS, activeTabId: 'a1' } });
    const anchors = wrapper.findAll('[data-testid="conversation-tab"]');
    expect(anchors).toHaveLength(2);
    expect(anchors[0].attributes('data-tab-id')).toBe('main');
    expect(anchors[0].text()).toContain('Main conversation');
    expect(anchors[1].attributes('data-tab-id')).toBe('a1');
    expect(anchors[1].text()).toContain('Agent a1');
    expect(anchors[1].text()).toContain('Running');
    expect(anchors[1].text()).toContain('Agents 5');
  });

  it('shows the count on main and selects Main through the existing emit', async () => {
    const wrapper = mount(ConversationTabs, { props: { tabs: TABS, activeTabId: 'main' } });
    expect(wrapper.findAll('[data-testid="conversation-tab"]')[1].text()).toContain('Agents (5)');
    await wrapper.get('[data-tab-id="main"]').trigger('click');
    expect(wrapper.emitted('select')).toEqual([['main']]);
  });

  it('opens with search focused and exposes all 64 agents without truncating list names', async () => {
    const many = [{ id: 'main', label: 'main', kind: 'main', color: null } as ConversationTab,
      ...Array.from({ length: 64 }, (_, index) => tab(`agent-${index}`, 'running', { label: `Long readable agent name ${index}` }))];
    const wrapper = mount(ConversationTabs, { attachTo: document.body, props: { tabs: many, activeTabId: 'main' } });
    await wrapper.findAll('[data-testid="conversation-tab"]')[1].trigger('click');
    expect(wrapper.findAll('[data-testid="agent-picker-option"]')).toHaveLength(64);
    expect(document.activeElement).toBe(wrapper.get('[data-testid="agent-picker-search"]').element);
    expect(wrapper.get('[data-agent-id="agent-63"]').text()).toContain('Long readable agent name 63');
    expect(wrapper.find('[role="option"] button').exists()).toBe(false);
    wrapper.unmount();
  });

  it('searches names and status metadata and reports no matches', async () => {
    const wrapper = mount(ConversationTabs, { props: { tabs: TABS, activeTabId: 'main' } });
    await wrapper.findAll('[data-testid="conversation-tab"]')[1].trigger('click');
    const search = wrapper.get('[data-testid="agent-picker-search"]');
    await search.setValue('nightly');
    expect(wrapper.findAll('[data-testid="agent-picker-option"]')).toHaveLength(1);
    expect(wrapper.get('[data-testid="agent-picker-option"]').text()).toContain('Nightly report');
    await search.setValue('missing');
    expect(wrapper.text()).toContain('No agents match.');
  });

  it('filters running, completed, and attention including pending answers and interrupted agents', async () => {
    const wrapper = mount(ConversationTabs, { props: { tabs: TABS, activeTabId: 'main', pendingQuestionAgentIds: ['a2'] } });
    await wrapper.findAll('[data-testid="conversation-tab"]')[1].trigger('click');
    await wrapper.get('[data-testid="agent-filter-running"]').trigger('click');
    expect(wrapper.findAll('[data-testid="agent-picker-option"]')).toHaveLength(2);
    await wrapper.get('[data-testid="agent-filter-completed"]').trigger('click');
    expect(wrapper.findAll('[data-testid="agent-picker-option"]')).toHaveLength(1);
    expect(wrapper.text()).toContain('Awaiting answer');
    await wrapper.get('[data-testid="agent-filter-attention"]').trigger('click');
    expect(wrapper.findAll('[data-testid="agent-picker-option"]').map((row) => row.attributes('data-agent-id')))
      .toEqual(['a2', 'a3', 'a4']);
    expect(wrapper.text()).toContain('Error · view_exceeds_window');
    expect(wrapper.text()).toContain('Interrupted');
  });

  it('uses arrows and Enter to choose from filtered results, then restores trigger focus', async () => {
    const scrollIntoView = HTMLElement.prototype.scrollIntoView;
    const scrollCalls: unknown[] = [];
    HTMLElement.prototype.scrollIntoView = function (options?: boolean | ScrollIntoViewOptions) { scrollCalls.push(options); };
    const wrapper = mount(ConversationTabs, { attachTo: document.body, props: { tabs: TABS, activeTabId: 'main' } });
    const picker = wrapper.findAll('[data-testid="conversation-tab"]')[1];
    await picker.trigger('click');
    const search = wrapper.get('[data-testid="agent-picker-search"]');
    await search.trigger('keydown', { key: 'ArrowDown' });
    await wrapper.vm.$nextTick();
    expect(scrollCalls).toContainEqual({ block: 'nearest' });
    await search.trigger('keydown', { key: 'Enter' });
    expect(wrapper.emitted('select')).toEqual([['a2']]);
    await wrapper.vm.$nextTick();
    expect(document.activeElement).toBe(picker.element);
    wrapper.unmount();
    HTMLElement.prototype.scrollIntoView = scrollIntoView;
  });

  it('closes on Escape from a filter and when focus leaves the picker', async () => {
    const wrapper = mount(ConversationTabs, { attachTo: document.body, props: { tabs: TABS, activeTabId: 'main' } });
    const picker = wrapper.findAll('[data-testid="conversation-tab"]')[1];
    await picker.trigger('click');
    const filter = wrapper.get('[data-testid="agent-filter-running"]');
    (filter.element as HTMLElement).focus();
    await filter.trigger('keydown', { key: 'Escape' });
    expect(wrapper.find('[data-testid="agent-picker-popover"]').exists()).toBe(false);
    expect(document.activeElement).toBe(picker.element);

    await picker.trigger('click');
    await wrapper.vm.$nextTick();
    const outside = document.createElement('button');
    document.body.appendChild(outside);
    outside.focus();
    await wrapper.vm.$nextTick();
    expect(wrapper.find('[data-testid="agent-picker-popover"]').exists()).toBe(false);
    wrapper.unmount();
    outside.remove();
  });

  it('Escape closes without selecting and restores focus', async () => {
    const wrapper = mount(ConversationTabs, { attachTo: document.body, props: { tabs: TABS, activeTabId: 'main' } });
    const picker = wrapper.findAll('[data-testid="conversation-tab"]')[1];
    await picker.trigger('click');
    await wrapper.get('[data-testid="agent-picker-search"]').trigger('keydown', { key: 'Escape' });
    expect(wrapper.find('[data-testid="agent-picker-popover"]').exists()).toBe(false);
    expect(wrapper.emitted('select')).toBeUndefined();
    expect(document.activeElement).toBe(picker.element);
    wrapper.unmount();
  });

  it('updates the stable trigger when routing changes externally without opening or stealing focus', async () => {
    const wrapper = mount(ConversationTabs, { props: { tabs: TABS, activeTabId: 'a1' } });
    await wrapper.setProps({ activeTabId: 'wf' });
    const picker = wrapper.findAll('[data-testid="conversation-tab"]')[1];
    expect(picker.attributes('data-tab-id')).toBe('wf');
    expect(picker.text()).toContain('Nightly report');
    expect(wrapper.find('[data-testid="agent-picker-popover"]').exists()).toBe(false);
  });
});
