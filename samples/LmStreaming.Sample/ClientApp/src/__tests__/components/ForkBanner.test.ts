import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import ForkBanner from '@/components/ForkBanner.vue';
import type { ConversationSummary } from '@/types/conversations';

const source: ConversationSummary = { threadId: 'src', title: 'Plan the migration', lastUpdated: 0 };
const fork: ConversationSummary = {
  threadId: 'fork-1',
  title: 'Plan the migration (fork)',
  lastUpdated: 0,
  forkedFrom: { threadId: 'src', messageId: 'm4', seq: 4 },
  rootThreadId: 'src',
};

describe('ForkBanner', () => {
  it('names the source and fork point, warns about shared files, and opens the source', async () => {
    const wrapper = mount(ForkBanner, { props: { conversation: fork, conversations: [source, fork] } });

    const banner = wrapper.get('[data-testid="fork-banner"]');
    expect(banner.text()).toContain('Forked from “Plan the migration” at msg 4');
    expect(banner.text()).toContain('Workspace files are shared, not rewound.');

    await wrapper.get('[data-testid="fork-banner-open"]').trigger('click');
    expect(wrapper.emitted('open')).toEqual([['src']]);
  });

  it('hides the link when the source was deleted', () => {
    const wrapper = mount(ForkBanner, {
      props: { conversation: fork, conversations: [{ ...source, deleted: true }, fork] },
    });

    expect(wrapper.find('[data-testid="fork-banner"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="fork-banner-open"]').exists()).toBe(false);
  });

  it('renders nothing for a conversation that is not a fork', () => {
    const wrapper = mount(ForkBanner, { props: { conversation: source, conversations: [source] } });

    expect(wrapper.find('[data-testid="fork-banner"]').exists()).toBe(false);
  });
});
