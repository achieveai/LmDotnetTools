import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import ConversationSidebar from '@/components/ConversationSidebar.vue';
import type { ConversationSummary } from '@/types/conversations';

const row = (threadId: string, overrides: Partial<ConversationSummary> = {}): ConversationSummary => ({
  threadId,
  title: `Title ${threadId}`,
  lastUpdated: 0,
  ...overrides,
});
const forkOf = (threadId: string, parent: string, seq: number): ConversationSummary =>
  row(threadId, { forkedFrom: { threadId: parent, messageId: `m${seq}`, seq }, rootThreadId: 'src' });

function mountSidebar(conversations: ConversationSummary[]) {
  return mount(ConversationSidebar, {
    props: {
      conversations,
      currentThreadId: null,
      isLoading: false,
      isLoadingMore: false,
      sortMode: 'lastUsed',
      isCollapsed: false,
    },
  });
}

/** Every rendered row, in order, as [testid, thread id]. */
function rows(wrapper: ReturnType<typeof mountSidebar>): string[][] {
  return wrapper
    .findAll('li[data-thread-id]')
    .map((li) => [li.attributes('data-testid')!, li.attributes('data-thread-id')!]);
}

describe('ConversationSidebar fork nesting', () => {
  it('lists forks and a fork of a fork directly under the original, with their origin', () => {
    const wrapper = mountSidebar([
      forkOf('f1a', 'f1', 7),
      row('src'),
      forkOf('f1', 'src', 4),
      row('other'),
    ]);

    expect(rows(wrapper)).toEqual([
      ['conversation-item', 'src'],
      ['sidebar-fork-row', 'f1a'],
      ['sidebar-fork-row', 'f1'],
      ['conversation-item', 'other'],
    ]);
    const labels = wrapper.findAll('[data-testid="sidebar-fork-row"] .fork-origin').map((n) => n.text());
    expect(labels).toEqual(['from "Title f1" · msg 7', 'from msg 4']);
  });

  it('shows a deleted original as a non-clickable header above its forks', async () => {
    const wrapper = mountSidebar([row('src', { deleted: true }), forkOf('f1', 'src', 2)]);

    const header = wrapper.get('[data-testid="sidebar-deleted-original"]');
    expect(header.text()).toContain('deleted original');
    expect(header.find('button').exists()).toBe(false);
    await header.trigger('click');
    expect(wrapper.emitted('selectConversation')).toBeUndefined();

    await wrapper.get('[data-testid="sidebar-fork-row"] .conversation-select-btn').trigger('click');
    expect(wrapper.emitted('selectConversation')).toEqual([['f1']]);
  });

  it('shows a fork whose original is not loaded as a top-level row with a fork badge', () => {
    const wrapper = mountSidebar([row('a'), forkOf('f1', 'src', 3)]);

    expect(rows(wrapper)).toEqual([
      ['conversation-item', 'a'],
      ['sidebar-fork-row', 'f1'],
    ]);
    const orphan = wrapper.get('[data-testid="sidebar-fork-row"]');
    expect(orphan.attributes('data-root-thread-id')).toBeUndefined();
    expect(orphan.find('.fork-badge').exists()).toBe(true);
  });
});
