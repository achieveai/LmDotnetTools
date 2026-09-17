import { afterEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import HeaderActionsMenu from '@/components/HeaderActionsMenu.vue';

function mountMenu(props: Record<string, boolean> = {}) {
  return mount(HeaderActionsMenu, { attachTo: document.body, props });
}

describe('HeaderActionsMenu', () => {
  afterEach(() => document.body.replaceChildren());

  it('publishes the menu-button ARIA contract and emits every action after closing', async () => {
    const wrapper = mountMenu();
    const trigger = wrapper.get('[data-testid="header-actions-menu-button"]');
    expect(trigger.attributes('aria-haspopup')).toBe('menu');
    expect(trigger.attributes('aria-expanded')).toBe('false');

    const actions = [
      ['marketplace-button', 'openMarketplaces'],
      ['egress-auth-button', 'openEgress'],
      ['file-browser-button', 'openFiles'],
      ['share-button', 'openShare'],
      ['clear-button', 'clear'],
    ] as const;
    for (const [testId, event] of actions) {
      await trigger.trigger('click');
      expect(wrapper.get('[role="menu"]').attributes('aria-labelledby')).toBe('header-actions-trigger');
      await wrapper.get(`[data-testid="${testId}"]`).trigger('click');
      expect(wrapper.emitted(event)).toHaveLength(1);
      expect(wrapper.find('[role="menu"]').exists()).toBe(false);
    }
  });

  it('opens from the keyboard at either end and roves over enabled items', async () => {
    const wrapper = mountMenu({ filesDisabled: true });
    const trigger = wrapper.get('[data-testid="header-actions-menu-button"]');
    await trigger.trigger('keydown', { key: 'ArrowDown' });
    await wrapper.vm.$nextTick();
    expect(document.activeElement).toBe(wrapper.get('[data-testid="marketplace-button"]').element);
    expect(wrapper.get('[data-testid="marketplace-button"]').attributes('tabindex')).toBe('-1');

    await wrapper.get('[data-testid="marketplace-button"]').trigger('keydown', { key: 'ArrowDown' });
    expect(document.activeElement).toBe(wrapper.get('[data-testid="egress-auth-button"]').element);
    await wrapper.get('[data-testid="egress-auth-button"]').trigger('keydown', { key: 'ArrowDown' });
    expect(document.activeElement).toBe(wrapper.get('[data-testid="share-button"]').element);
    await wrapper.get('[data-testid="share-button"]').trigger('keydown', { key: 'End' });
    expect(document.activeElement).toBe(wrapper.get('[data-testid="clear-button"]').element);
    await wrapper.get('[data-testid="clear-button"]').trigger('keydown', { key: 'Home' });
    expect(document.activeElement).toBe(wrapper.get('[data-testid="marketplace-button"]').element);

    await wrapper.get('[data-testid="marketplace-button"]').trigger('keydown', { key: 'Escape' });
    expect(wrapper.find('[role="menu"]').exists()).toBe(false);
    expect(document.activeElement).toBe(trigger.element);

    await trigger.trigger('keydown', { key: 'ArrowUp' });
    await wrapper.vm.$nextTick();
    expect(document.activeElement).toBe(wrapper.get('[data-testid="clear-button"]').element);
  });

  it('closes on forward or reverse Tab from a stable trigger focus', async () => {
    const wrapper = mountMenu();
    const trigger = wrapper.get('[data-testid="header-actions-menu-button"]');
    await trigger.trigger('keydown', { key: 'ArrowDown' });
    const first = wrapper.get('[data-testid="marketplace-button"]');
    expect(document.activeElement).toBe(first.element);

    await first.trigger('keydown', { key: 'Tab' });
    expect(wrapper.find('[role="menu"]').exists()).toBe(false);
    expect(document.activeElement).toBe(trigger.element);

    await trigger.trigger('keydown', { key: 'ArrowUp' });
    const last = wrapper.get('[data-testid="clear-button"]');
    expect(document.activeElement).toBe(last.element);
    await last.trigger('keydown', { key: 'Tab', shiftKey: true });
    expect(wrapper.find('[role="menu"]').exists()).toBe(false);
    expect(document.activeElement).toBe(trigger.element);
  });

  it('supports Enter and Space, closes on outside click, and keeps disabled actions unavailable', async () => {
    const wrapper = mountMenu({ filesDisabled: true, shareDisabled: true, clearDisabled: true });
    const trigger = wrapper.get('[data-testid="header-actions-menu-button"]');
    await trigger.trigger('keydown', { key: 'Enter' });
    expect(wrapper.get('[data-testid="file-browser-button"]').attributes('disabled')).toBeDefined();
    expect(wrapper.get('[data-testid="share-button"]').attributes('disabled')).toBeDefined();
    expect(wrapper.get('[data-testid="clear-button"]').attributes('disabled')).toBeDefined();
    await wrapper.get('[data-testid="file-browser-button"]').trigger('click');
    expect(wrapper.emitted('openFiles')).toBeUndefined();

    document.body.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await wrapper.vm.$nextTick();
    expect(wrapper.find('[role="menu"]').exists()).toBe(false);

    await trigger.trigger('keydown', { key: ' ' });
    expect(wrapper.find('[role="menu"]').exists()).toBe(true);
  });

  it('does not return focus to More when an action opens another surface', async () => {
    const wrapper = mountMenu();
    const trigger = wrapper.get('[data-testid="header-actions-menu-button"]');
    await trigger.trigger('click');
    await wrapper.get('[data-testid="marketplace-button"]').trigger('click');
    await wrapper.vm.$nextTick();
    expect(document.activeElement).not.toBe(trigger.element);

    await trigger.trigger('click');
    await wrapper.get('[data-testid="clear-button"]').trigger('click');
    await wrapper.vm.$nextTick();
    expect(document.activeElement).toBe(trigger.element);
  });
});
