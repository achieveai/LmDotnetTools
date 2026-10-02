import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import BranchSwitcher from '@/components/BranchSwitcher.vue';
import type { BranchPoint } from '@/api/conversationsApi';

const point = (currentIndex: number): BranchPoint => ({
  afterMessageId: 'm4',
  afterSeq: 4,
  options: ['src', 'fork-1', 'fork-2'].map((threadId, i) => ({
    threadId,
    title: threadId,
    current: i === currentIndex,
  })),
});

describe('BranchSwitcher', () => {
  it('labels the current option as i / N', () => {
    const wrapper = mount(BranchSwitcher, { props: { point: point(1) } });

    expect(wrapper.get('[data-testid="branch-switcher-label"]').text()).toBe('2 / 3');
  });

  it('opens the neighbouring options with the arrows', async () => {
    const wrapper = mount(BranchSwitcher, { props: { point: point(1) } });

    await wrapper.get('[data-testid="branch-switcher-prev"]').trigger('click');
    await wrapper.get('[data-testid="branch-switcher-next"]').trigger('click');

    expect(wrapper.emitted('open')).toEqual([['src'], ['fork-2']]);
  });

  it('disables the arrow past either end instead of wrapping', async () => {
    const first = mount(BranchSwitcher, { props: { point: point(0) } });
    const last = mount(BranchSwitcher, { props: { point: point(2) } });

    expect(first.get('[data-testid="branch-switcher-prev"]').attributes('disabled')).toBeDefined();
    expect(last.get('[data-testid="branch-switcher-next"]').attributes('disabled')).toBeDefined();
    await first.get('[data-testid="branch-switcher-prev"]').trigger('click');
    expect(first.emitted('open')).toBeUndefined();
  });
});
