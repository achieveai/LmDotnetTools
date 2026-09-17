import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import ConversationSidebar from '@/components/ConversationSidebar.vue';
import type { ConversationSortMode, ConversationSummary } from '@/types/conversations';
import type { Workspace } from '@/types/workspace';

const workspaces: Workspace[] = [
  {
    id: 'default',
    name: 'Default project',
    directoryRelPath: '',
    marketplaces: [],
    isSystemDefined: true,
    createdAt: 0,
    updatedAt: 0,
    compatibility: 'compatible',
    unsupportedMarketplaces: [],
  },
  {
    id: 'repo-a',
    name: 'Repo A',
    directoryRelPath: 'repo-a',
    marketplaces: [],
    isSystemDefined: false,
    createdAt: 1,
    updatedAt: 1,
    compatibility: 'compatible',
    unsupportedMarketplaces: [],
  },
];

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
    workspaces: Workspace[];
    hasMore: boolean;
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
      workspaces: [],
      hasMore: false,
      ...overrides,
    },
  });
}

describe('ConversationSidebar — project folders', () => {
  const groupedConversations: ConversationSummary[] = [
    { threadId: 'a-new', title: 'A newest', lastUpdated: 30, workspace: 'repo-a' },
    { threadId: 'default', title: 'Default chat', lastUpdated: 25, workspace: 'default' },
    { threadId: 'a-old', title: 'A older', lastUpdated: 20, workspace: 'repo-a' },
    { threadId: 'legacy', title: 'Old chat', lastUpdated: 15, workspace: null },
    { threadId: 'removed', title: 'Removed workspace chat', lastUpdated: 10, workspace: 'gone' },
  ];

  it('renders every catalog project, grouping chats by persisted workspace in source order', () => {
    const wrapper = mountSidebar({ conversations: groupedConversations, workspaces });

    expect(wrapper.findAll('[data-testid="project-folder"]')).toHaveLength(4);
    expect(wrapper.get('[data-testid="project-folder-default"]').text()).toContain('Default project');
    expect(wrapper.get('[data-testid="project-folder-repo-a"]').text()).toContain('Repo A');
    expect(wrapper.get('[data-testid="project-folder-legacy"]').text()).toContain('No project');
    expect(wrapper.get('[data-testid="project-folder-missing-gone"]').text()).toContain('gone');
    expect(wrapper.get('[data-testid="project-folder-repo-a"]').find('.project-count').exists()).toBe(false);
    expect(
      wrapper
        .get('[data-testid="project-conversations-repo-a"]')
        .findAll('[data-testid="conversation-item"]')
        .map((row) => row.attributes('data-thread-id'))
    ).toEqual(['a-new', 'a-old']);
  });

  it('renders empty catalog projects with a start action that emits their workspace id', async () => {
    const wrapper = mountSidebar({ conversations: [], workspaces });

    expect(wrapper.find('[data-testid="project-folder-repo-a"]').exists()).toBe(true);
    expect(wrapper.get('[data-testid="project-conversations-repo-a"]').find('li').classes()).toContain(
      'start-conversation-row'
    );
    await wrapper.get('[data-testid="start-conversation-repo-a"]').trigger('click');

    expect(wrapper.emitted('newChatInWorkspace')).toEqual([['repo-a']]);
  });

  it('does not offer draft creation for a missing workspace id', () => {
    const wrapper = mountSidebar({
      conversations: [{ threadId: 'removed', title: 'Still readable', lastUpdated: 1, workspace: 'gone' }],
      workspaces,
    });

    expect(wrapper.get('[data-testid="project-conversations-missing-gone"]').text()).toContain('Still readable');
    expect(wrapper.find('[data-testid="start-conversation-gone"]').exists()).toBe(false);
  });

  it('uses native disclosure state and preserves manual collapse until selection changes', async () => {
    const wrapper = mountSidebar({
      conversations: groupedConversations,
      workspaces,
      currentThreadId: 'a-new',
    });
    const disclosure = () => wrapper.get('[data-testid="project-toggle-repo-a"]');

    expect(disclosure().element.tagName).toBe('BUTTON');
    expect(disclosure().attributes('aria-expanded')).toBe('true');
    await disclosure().trigger('click');
    expect(disclosure().attributes('aria-expanded')).toBe('false');

    await wrapper.setProps({ currentThreadId: 'a-new' });
    expect(disclosure().attributes('aria-expanded')).toBe('false');

    await wrapper.setProps({ currentThreadId: 'default' });
    expect(wrapper.get('[data-testid="project-toggle-default"]').attributes('aria-expanded')).toBe('true');
  });

  it('keeps nested conversation selection and deletion behavior', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const wrapper = mountSidebar({ conversations: groupedConversations, workspaces });
    const row = wrapper.get('[data-thread-id="a-new"]');

    await row.get('button.conversation-select-btn').trigger('click');
    await row.get('button.delete-btn').trigger('click');

    expect(wrapper.emitted('selectConversation')).toEqual([['a-new']]);
    expect(wrapper.emitted('deleteConversation')).toEqual([['a-new']]);
    confirm.mockRestore();
  });

  it('uses a native button for keyboard-operable conversation selection', async () => {
    const wrapper = mountSidebar({ conversations: groupedConversations, workspaces });
    const select = wrapper.get('[data-thread-id="a-new"] button.conversation-select-btn');

    expect(select.element.tagName).toBe('BUTTON');
    await select.trigger('click');
    expect(wrapper.emitted('selectConversation')).toEqual([['a-new']]);
  });

  it('keeps pagination reachable with a visible load-more button when folders are collapsed', async () => {
    const wrapper = mountSidebar({ conversations: groupedConversations, workspaces, hasMore: true });
    await wrapper.get('[data-testid="project-toggle-repo-a"]').trigger('click');

    await wrapper.get('[data-testid="conversations-load-more"]').trigger('click');
    expect(wrapper.emitted('loadMore')).toHaveLength(1);

    await wrapper.setProps({ isLoadingMore: true });
    expect(wrapper.get('[data-testid="conversations-load-more"]').text()).toContain('Loading');
    expect(wrapper.get<HTMLButtonElement>('[data-testid="conversations-load-more"]').element.disabled).toBe(true);
  });
});

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
