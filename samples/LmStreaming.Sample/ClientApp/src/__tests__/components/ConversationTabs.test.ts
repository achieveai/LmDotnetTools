import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
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

// Models a real pointer click the way Chromium sequences one, because the popover's close-on-blur
// race only exists in that sequencing. The browser moves focus as the DEFAULT ACTION of `mousedown`
// (so `preventDefault` suppresses it), fires `blur`/`focusout` on the old element first — leaving
// `document.activeElement === document.body` — and performs a microtask checkpoint between listener
// invocations, so anything a `focusout` handler defers to `nextTick` runs BEFORE focus lands on the
// clicked control. jsdom's own `focus()` runs that whole sequence in one stack frame with no
// checkpoint in between, so the interleaving has to be spelled out here. Recorded against Chromium:
// `.claude/scratchpad/conversation_memories/bug-batch-agent-picker/investigation.md`.
async function pointerClick(element: HTMLElement): Promise<void> {
  const previous = document.activeElement as HTMLElement | null;
  const focusMoves = element.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
  if (focusMoves && previous && previous !== element) {
    previous.blur();
    await nextTick();
    if (element.isConnected) element.focus();
  }
  // Chromium never delivers the click when the target was detached mid-gesture, which is exactly
  // what happens when the popover closes on mousedown — so don't hand the handler a click it would
  // not have received.
  if (element.isConnected) element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
  await nextTick();
}

describe('ConversationTabs agent picker', () => {
  it('keeps stable Main and current-agent anchors for existing navigation', async () => {
    const wrapper = mount(ConversationTabs, { props: { tabs: TABS, activeTabId: 'a1' } });
    const anchors = wrapper.findAll('[data-testid="conversation-tab"]');
    expect(anchors).toHaveLength(2);
    expect(anchors[0].attributes('data-tab-id')).toBe('main');
    expect(anchors[0].text()).toContain('Main conversation');
    expect(anchors[1].attributes('data-tab-id')).toBe('a1');
    expect(anchors[1].text()).toContain('Agent a1');
    expect(anchors[1].text()).toContain('Running');
    expect(anchors[1].text()).toContain('Agents 5');
    const navigation = wrapper.get('nav');
    const main = anchors[0];
    const agent = anchors[1];

    expect(navigation.attributes('aria-label')).toBe('Conversation views');
    expect(main.attributes('id')).toBe('conversation-main-selector');
    expect(main.attributes('aria-controls')).toBe('conversation-main-view');
    expect(agent.attributes('id')).toBe('conversation-agent-selector-a1');
    expect(agent.attributes('aria-current')).toBe('page');
    expect(agent.attributes('aria-controls')).toBe('conversation-agent-view-a1');

    await agent.trigger('click');
    expect(agent.attributes('aria-controls')).toBe('conversation-agent-view-a1 agent-picker-list');
    const selected = wrapper.get('[role="option"][aria-selected="true"]');
    expect(selected.attributes('aria-controls')).toBe('conversation-agent-view-a1');
    expect(wrapper.get('[data-agent-id="a2"]').attributes('aria-controls')).toBeUndefined();
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

  it('stays open and applies the filter when a pill is clicked with a real pointer', async () => {
    const wrapper = mount(ConversationTabs, { attachTo: document.body, props: { tabs: TABS, activeTabId: 'main', pendingQuestionAgentIds: [] } });
    await wrapper.findAll('[data-testid="conversation-tab"]')[1].trigger('click');
    await nextTick();
    expect(document.activeElement).toBe(wrapper.get('[data-testid="agent-picker-search"]').element);

    await pointerClick(wrapper.get('[data-testid="agent-filter-running"]').element as HTMLElement);

    expect(wrapper.find('[data-testid="agent-picker-popover"]').exists()).toBe(true);
    expect(wrapper.get('[data-testid="agent-filter-running"]').attributes('aria-pressed')).toBe('true');
    expect(wrapper.findAll('[data-testid="agent-picker-option"]').map((row) => row.attributes('data-agent-id')))
      .toEqual(['a1', 'wf']);
    // Focus never left the search box, which is the mechanism that keeps the popover open.
    expect(document.activeElement).toBe(wrapper.get('[data-testid="agent-picker-search"]').element);
    wrapper.unmount();
  });

  it('stays open when picking an option is preceded by a pointer press and when typing', async () => {
    const wrapper = mount(ConversationTabs, { attachTo: document.body, props: { tabs: TABS, activeTabId: 'main' } });
    await wrapper.findAll('[data-testid="conversation-tab"]')[1].trigger('click');
    await nextTick();
    const search = wrapper.get('[data-testid="agent-picker-search"]');
    await search.setValue('nightly');
    await nextTick();
    expect(wrapper.find('[data-testid="agent-picker-popover"]').exists()).toBe(true);
    expect(wrapper.findAll('[data-testid="agent-picker-option"]')).toHaveLength(1);

    await search.setValue('');
    await pointerClick(wrapper.get('[data-agent-id="a2"]').element as HTMLElement);
    expect(wrapper.emitted('select')).toEqual([['a2']]);
    wrapper.unmount();
  });

  it('still closes on a pointer press outside the picker', async () => {
    const wrapper = mount(ConversationTabs, { attachTo: document.body, props: { tabs: TABS, activeTabId: 'main' } });
    await wrapper.findAll('[data-testid="conversation-tab"]')[1].trigger('click');
    await nextTick();
    const outside = document.createElement('button');
    document.body.appendChild(outside);
    outside.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    await nextTick();
    expect(wrapper.find('[data-testid="agent-picker-popover"]').exists()).toBe(false);
    wrapper.unmount();
    outside.remove();
  });

  // Layout itself is not assertable here (scoped CSS is not applied under jsdom); the right-aligned
  // popover was verified in a real browser. This pins only the markup half: a real icon, not a glyph.
  it('renders the chevron as an inline svg icon rather than a literal glyph', () => {
    const wrapper = mount(ConversationTabs, { props: { tabs: TABS, activeTabId: 'main' } });
    const chevron = wrapper.get('.agent-picker__chevron');
    expect(chevron.element.tagName.toLowerCase()).toBe('svg');
    expect(chevron.attributes('aria-hidden')).toBe('true');
    expect(wrapper.find('.agent-picker__chevron path').exists()).toBe(true);
    expect(wrapper.text()).not.toContain('⌄');
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
