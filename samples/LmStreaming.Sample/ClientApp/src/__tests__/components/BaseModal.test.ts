import { describe, it, expect, afterEach } from 'vitest';
import { mount } from '@vue/test-utils';
import BaseModal from '@/components/BaseModal.vue';

const slots = {
  default: '<button data-testid="inner-a">A</button><button data-testid="inner-b">B</button>',
};

function mountModal() {
  return mount(BaseModal, {
    props: { title: 'Files', dataTestId: 'demo-modal' },
    slots,
    attachTo: document.body,
  });
}

describe('BaseModal accessibility', () => {
  let wrapper: ReturnType<typeof mountModal> | undefined;
  afterEach(() => {
    wrapper?.unmount();
    wrapper = undefined;
  });

  it('renders a labelled dialog whose aria-labelledby resolves to the title text', () => {
    wrapper = mountModal();

    const dialog = wrapper.find('[role="dialog"]');
    expect(dialog.exists()).toBe(true);
    expect(dialog.attributes('aria-modal')).toBe('true');

    const labelId = dialog.attributes('aria-labelledby');
    expect(labelId).toBeTruthy();
    const title = wrapper.find(`#${labelId}`);
    expect(title.exists()).toBe(true);
    expect(title.text()).toBe('Files');
  });

  it('exposes the backdrop and close button under the provided testids', () => {
    wrapper = mountModal();
    expect(wrapper.find('[data-testid="demo-modal"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="demo-modal-close"]').exists()).toBe(true);
  });

  it('emits close when the close button is clicked', async () => {
    wrapper = mountModal();
    await wrapper.find('[data-testid="demo-modal-close"]').trigger('click');
    expect(wrapper.emitted('close')).toHaveLength(1);
  });

  it('emits close on Escape', async () => {
    wrapper = mountModal();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(wrapper.emitted('close')).toHaveLength(1);
  });

  it('closes on a backdrop click but not on an inner click', async () => {
    wrapper = mountModal();

    await wrapper.find('[data-testid="inner-a"]').trigger('click');
    expect(wrapper.emitted('close')).toBeUndefined();

    await wrapper.find('[data-testid="demo-modal"]').trigger('click');
    expect(wrapper.emitted('close')).toHaveLength(1);
  });
});

describe('BaseModal focus management', () => {
  let wrapper: ReturnType<typeof mountModal> | undefined;
  afterEach(() => {
    wrapper?.unmount();
    wrapper = undefined;
  });

  it('focuses the dialog container on mount', () => {
    wrapper = mountModal();
    const container = wrapper.find('[role="dialog"]').element as HTMLElement;
    expect(document.activeElement).toBe(container);
  });

  it('traps Tab from the last focusable back to the first', () => {
    wrapper = mountModal();
    // DOM order: close button (first), then the slotted inner-a/inner-b (last).
    const first = wrapper.find('[data-testid="demo-modal-close"]').element as HTMLElement;
    const last = wrapper.find('[data-testid="inner-b"]').element as HTMLElement;

    last.focus();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }));

    expect(document.activeElement).toBe(first);
  });

  it('traps Shift+Tab from the first focusable to the last', () => {
    wrapper = mountModal();
    const first = wrapper.find('[data-testid="demo-modal-close"]').element as HTMLElement;
    const last = wrapper.find('[data-testid="inner-b"]').element as HTMLElement;

    first.focus();
    document.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true })
    );

    expect(document.activeElement).toBe(last);
  });

  it('restores focus to the previously-focused element on unmount', () => {
    const opener = document.createElement('button');
    document.body.appendChild(opener);
    opener.focus();
    expect(document.activeElement).toBe(opener);

    wrapper = mountModal();
    expect(document.activeElement).not.toBe(opener);

    wrapper.unmount();
    wrapper = undefined;
    expect(document.activeElement).toBe(opener);

    opener.remove();
  });
});
