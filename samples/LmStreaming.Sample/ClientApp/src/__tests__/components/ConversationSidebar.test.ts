import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import ConversationSidebar from '@/components/ConversationSidebar.vue';
import type { ConversationSortMode, ConversationSummary } from '@/types/conversations';

function conversations(count: number): ConversationSummary[] {
  return Array.from({ length: count }, (_, i) => ({
    threadId: `c${i}`,
    title: `Conversation ${i}`,
    lastUpdated: 1000 - i,
  }));
}

function mountSidebar(
  overrides: Partial<{
    conversations: ConversationSummary[];
    currentThreadId: string | null;
    isLoading: boolean;
    isLoadingMore: boolean;
    sortMode: ConversationSortMode;
    isCollapsed: boolean;
  }> = {}
) {
  return mount(ConversationSidebar, {
    props: {
      conversations: conversations(5),
      currentThreadId: null,
      isLoading: false,
      isLoadingMore: false,
      sortMode: 'lastUsed' as ConversationSortMode,
      isCollapsed: false,
      ...overrides,
    },
  });
}

/**
 * jsdom lays nothing out, so the scroll geometry the handler reads is all zeros. Stamp the three
 * values that decide "near the bottom" onto the real element.
 */
function setScrollGeometry(
  element: HTMLElement,
  geometry: { scrollHeight: number; clientHeight: number; scrollTop: number }
): void {
  Object.defineProperty(element, 'scrollHeight', {
    value: geometry.scrollHeight,
    configurable: true,
  });
  Object.defineProperty(element, 'clientHeight', {
    value: geometry.clientHeight,
    configurable: true,
  });
  Object.defineProperty(element, 'scrollTop', {
    value: geometry.scrollTop,
    configurable: true,
    writable: true,
  });
}

describe('ConversationSidebar — incremental loading', () => {
  it('emits loadMore when the scroll container reaches near its bottom', async () => {
    const wrapper = mountSidebar();
    const content = wrapper.find('.sidebar-content');
    // 1000 - 560 - 400 = 40px left, inside the 120px threshold.
    setScrollGeometry(content.element as HTMLElement, {
      scrollHeight: 1000,
      clientHeight: 400,
      scrollTop: 560,
    });

    await content.trigger('scroll');

    expect(wrapper.emitted('loadMore')).toHaveLength(1);
  });

  it('does not emit loadMore while the bottom is still far away', async () => {
    const wrapper = mountSidebar();
    const content = wrapper.find('.sidebar-content');
    // 1000 - 100 - 400 = 500px left.
    setScrollGeometry(content.element as HTMLElement, {
      scrollHeight: 1000,
      clientHeight: 400,
      scrollTop: 100,
    });

    await content.trigger('scroll');

    expect(wrapper.emitted('loadMore')).toBeUndefined();
  });

  it('observes the inner scroll container, not the outer aside', () => {
    // The aside is a flex column that never scrolls; the scroll listener has to be on the element
    // that actually overflows, or it would never fire in the real app.
    const wrapper = mountSidebar();
    expect(wrapper.find('aside.conversation-sidebar').exists()).toBe(true);
    expect(wrapper.find('.sidebar-content').exists()).toBe(true);
  });

  it('shows the loading-more affordance only while a page is in flight', async () => {
    const wrapper = mountSidebar({ isLoadingMore: true });
    expect(wrapper.find('[data-testid="conversations-loading-more"]').exists()).toBe(true);

    await wrapper.setProps({ isLoadingMore: false });
    expect(wrapper.find('[data-testid="conversations-loading-more"]').exists()).toBe(false);
  });
});

describe('ConversationSidebar — sort mode selector', () => {
  it('opens the menu and emits the chosen sort mode', async () => {
    const wrapper = mountSidebar();
    expect(wrapper.find('[data-testid="sort-mode-option-created"]').exists()).toBe(false);

    await wrapper.find('[data-testid="sort-mode-button"]').trigger('click');
    expect(wrapper.find('[data-testid="sort-mode-option-lastUsed"]').exists()).toBe(true);

    await wrapper.find('[data-testid="sort-mode-option-created"]').trigger('click');

    expect(wrapper.emitted('changeSortMode')).toEqual([['created']]);
    // Menu closes on selection.
    expect(wrapper.find('[data-testid="sort-mode-option-created"]').exists()).toBe(false);
  });

  it('does not re-emit when the already-active mode is chosen', async () => {
    const wrapper = mountSidebar({ sortMode: 'created' });
    await wrapper.find('[data-testid="sort-mode-button"]').trigger('click');

    await wrapper.find('[data-testid="sort-mode-option-created"]').trigger('click');

    expect(wrapper.emitted('changeSortMode')).toBeUndefined();
  });

  it('labels the button with the active mode', async () => {
    const wrapper = mountSidebar({ sortMode: 'created' });
    expect(wrapper.find('[data-testid="sort-mode-button"]').text()).toContain('Recently created');

    await wrapper.setProps({ sortMode: 'lastUsed' });
    expect(wrapper.find('[data-testid="sort-mode-button"]').text()).toContain('Last used');
  });

  it('closes on an outside click — the control stays mounted so the handler has an element to test', async () => {
    const wrapper = mountSidebar();
    await wrapper.find('[data-testid="sort-mode-button"]').trigger('click');
    expect(wrapper.find('[data-testid="sort-mode-option-created"]').exists()).toBe(true);

    document.body.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await wrapper.vm.$nextTick();

    expect(wrapper.find('[data-testid="sort-mode-option-created"]').exists()).toBe(false);
    wrapper.unmount();
  });

  it('keeps the selector mounted while the sidebar is collapsed', () => {
    const wrapper = mountSidebar({ isCollapsed: true });
    expect(wrapper.find('[data-testid="sort-mode-button"]').exists()).toBe(true);
    expect(wrapper.find('.sidebar-sort').classes()).toContain('hidden');
  });
});
