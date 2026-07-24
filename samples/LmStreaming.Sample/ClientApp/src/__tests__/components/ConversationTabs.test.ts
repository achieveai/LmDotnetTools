import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import ConversationTabs from '@/components/ConversationTabs.vue';
import type { ConversationTab } from '@/composables/useConversationTabs';

const TABS: ConversationTab[] = [
  { id: 'main', label: 'main', kind: 'main', color: null },
  { id: 'a1', label: 'Researcher', kind: 'subagent', color: '#2563eb', status: 'running' },
  { id: 'a2', label: 'Planner', kind: 'subagent', color: '#0d9488', status: 'completed' },
  { id: 'wf1', label: 'Nightly report', kind: 'workflow', color: '#7c3aed', status: 'running' },
];

describe('ConversationTabs', () => {
  it('renders one tab per entry with labels and tab ids', () => {
    const wrapper = mount(ConversationTabs, { props: { tabs: TABS, activeTabId: 'main' } });
    const tabs = wrapper.findAll('[data-testid="conversation-tab"]');
    expect(tabs).toHaveLength(4);
    expect(tabs.map((t) => t.attributes('data-tab-id'))).toEqual(['main', 'a1', 'a2', 'wf1']);
    expect(tabs[1].text()).toContain('Researcher');
  });

  it('badges only the workflow tab and tags its kind', () => {
    const wrapper = mount(ConversationTabs, { props: { tabs: TABS, activeTabId: 'main' } });
    // Exactly one workflow badge, on the workflow tab; sub-agent/main tabs have none.
    const badges = wrapper.findAll('[data-testid="workflow-tab-badge"]');
    expect(badges).toHaveLength(1);
    const wfTab = wrapper.get('[data-tab-id="wf1"]');
    expect(wfTab.attributes('data-tab-kind')).toBe('workflow');
    expect(wfTab.find('[data-testid="workflow-tab-badge"]').exists()).toBe(true);
    expect(wfTab.attributes('title')).toContain('Workflow:');
    // A plain sub-agent tab is not badged and keeps its own kind.
    const subTab = wrapper.get('[data-tab-id="a1"]');
    expect(subTab.attributes('data-tab-kind')).toBe('subagent');
    expect(subTab.find('[data-testid="workflow-tab-badge"]').exists()).toBe(false);
  });

  it('marks the active tab and reflects it in aria-selected', () => {
    const wrapper = mount(ConversationTabs, { props: { tabs: TABS, activeTabId: 'a1' } });
    const active = wrapper.get('[data-tab-id="a1"]');
    expect(active.classes()).toContain('active');
    expect(active.attributes('aria-selected')).toBe('true');
    expect(wrapper.get('[data-tab-id="main"]').attributes('aria-selected')).toBe('false');
  });

  it('applies the assigned hue to the color dot', () => {
    const wrapper = mount(ConversationTabs, { props: { tabs: TABS, activeTabId: 'main' } });
    const dot = wrapper.get('[data-tab-id="a1"] .conversation-tab__dot');
    // jsdom normalizes the hex to rgb.
    expect(dot.attributes('style')).toContain('background');
  });

  it('emits select with the tab id on click', async () => {
    const wrapper = mount(ConversationTabs, { props: { tabs: TABS, activeTabId: 'main' } });
    await wrapper.get('[data-tab-id="a2"]').trigger('click');
    expect(wrapper.emitted('select')).toEqual([['a2']]);
  });
});
