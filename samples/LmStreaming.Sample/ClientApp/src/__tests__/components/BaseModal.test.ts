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

  it('excludes [inert] background controls from the trap so a nested confirmation keeps focus', () => {
    // Models the file browser's nested delete/overwrite confirmation: the background content is marked
    // inert, and only the confirmation (plus the modal close button) should remain in the tab cycle.
    // A trailing inert control sits AFTER the confirmation on purpose: without the `[inert]` filter it
    // would be the last focusable, so these assertions fail — i.e. the test is red before the fix.
    wrapper = mount(BaseModal, {
      props: { title: 'Files', dataTestId: 'demo-modal' },
      slots: {
        default:
          '<div inert><button data-testid="bg-before">before</button></div>' +
          '<div data-testid="confirm">' +
          '<button data-testid="cf-cancel">Cancel</button><button data-testid="cf-ok">OK</button>' +
          '</div>' +
          '<div inert><button data-testid="bg-after">after</button></div>',
      },
      attachTo: document.body,
    });

    const close = wrapper.find('[data-testid="demo-modal-close"]').element as HTMLElement;
    const cfOk = wrapper.find('[data-testid="cf-ok"]').element as HTMLElement;

    // With the filter, cf-ok is the LAST live focusable (bg-after is inert) → Tab wraps to close (first).
    // Without the filter, bg-after would be last, so cf-ok's Tab would NOT wrap and this fails.
    cfOk.focus();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }));
    expect(document.activeElement).toBe(close);

    // Shift+Tab from the first (close) wraps to the last LIVE focusable — cf-ok, never the trailing inert
    // bg-after (which would be the last focusable without the filter).
    close.focus();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true }));
    expect(document.activeElement).toBe(cfOk);
  });
});
