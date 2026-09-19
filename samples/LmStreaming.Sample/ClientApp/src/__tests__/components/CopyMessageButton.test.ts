import { describe, it, expect, vi, afterEach } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import CopyMessageButton from '@/components/CopyMessageButton.vue';
import { copyTextToClipboard } from '@/utils/clipboard';

const MARKDOWN = '# Title\n\n- **bold** item\n\n```ts\nconst a = 1;\n```\n\n[Report](B:\\ws\\docs\\a.md)';

function setSecureContext(value: boolean) {
  Object.defineProperty(window, 'isSecureContext', { value, configurable: true });
}

function stubClipboard(writeText: (text: string) => Promise<void>) {
  Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
}

afterEach(() => {
  vi.restoreAllMocks();
  vi.useRealTimers();
  // jsdom has no clipboard; remove whatever a test installed.
  Reflect.deleteProperty(navigator, 'clipboard');
  Reflect.deleteProperty(document, 'execCommand');
  Reflect.deleteProperty(window, 'isSecureContext');
});

describe('copyTextToClipboard', () => {
  it('uses the async Clipboard API when the page is a secure context', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    stubClipboard(writeText);
    setSecureContext(true);

    await copyTextToClipboard(MARKDOWN);

    expect(writeText).toHaveBeenCalledWith(MARKDOWN);
  });

  it('falls back to execCommand when the Clipboard API is unavailable (http on a LAN IP)', async () => {
    setSecureContext(false);
    let copied: string | null = null;
    const execCommand = vi.fn(() => {
      copied = (document.activeElement as HTMLTextAreaElement).value;
      return true;
    });
    Object.defineProperty(document, 'execCommand', { value: execCommand, configurable: true });

    await copyTextToClipboard(MARKDOWN);

    expect(execCommand).toHaveBeenCalledWith('copy');
    expect(copied).toBe(MARKDOWN);
    // The helper textarea is gone again.
    expect(document.querySelectorAll('textarea').length).toBe(0);
  });

  it('falls back when the async API rejects (e.g. permission denied)', async () => {
    stubClipboard(vi.fn().mockRejectedValue(new DOMException('denied', 'NotAllowedError')));
    setSecureContext(true);
    const execCommand = vi.fn(() => true);
    Object.defineProperty(document, 'execCommand', { value: execCommand, configurable: true });

    await copyTextToClipboard('x');

    expect(execCommand).toHaveBeenCalledWith('copy');
  });

  it('rejects when both paths fail', async () => {
    setSecureContext(false);
    Object.defineProperty(document, 'execCommand', { value: () => false, configurable: true });

    await expect(copyTextToClipboard('x')).rejects.toThrow();
  });

  it('rejects, and removes its helper textarea, when execCommand throws', async () => {
    setSecureContext(false);
    const execCommand = () => {
      throw new Error('blocked');
    };
    Object.defineProperty(document, 'execCommand', { value: execCommand, configurable: true });

    await expect(copyTextToClipboard('x')).rejects.toThrow('Copy to clipboard failed');
    expect(document.querySelectorAll('textarea').length).toBe(0);
  });
});

describe('CopyMessageButton', () => {
  it('copies the exact raw markdown and confirms, then resets', async () => {
    vi.useFakeTimers();
    const writeText = vi.fn().mockResolvedValue(undefined);
    stubClipboard(writeText);
    setSecureContext(true);
    const wrapper = mount(CopyMessageButton, { props: { text: MARKDOWN } });

    const button = wrapper.get('[data-testid="copy-message-button"]');
    // Icon-only: the accessible name is a visually hidden label, never visible text next to the icon.
    expect(button.attributes('aria-label')).toBeUndefined();
    expect(button.get('.copy-message-label').text()).toBe('Copy message');
    expect(button.attributes('data-state')).toBe('idle');
    await button.trigger('click');
    await flushPromises();

    expect(writeText).toHaveBeenCalledWith(MARKDOWN);
    expect(button.attributes('data-state')).toBe('copied');
    expect(button.get('.copy-message-label').text()).toBe('Copied');

    vi.advanceTimersByTime(2000);
    await flushPromises();
    expect(button.attributes('data-state')).toBe('idle');
    expect(button.get('.copy-message-label').text()).toBe('Copy message');
  });

  it('draws the result as an icon swap, so the button keeps one footprint in every state', async () => {
    vi.useFakeTimers();
    stubClipboard(vi.fn().mockResolvedValue(undefined));
    setSecureContext(true);
    const wrapper = mount(CopyMessageButton, { props: { text: 'x' } });
    const button = wrapper.get('[data-testid="copy-message-button"]');

    const idleIcon = button.get('svg').html();
    await button.trigger('click');
    await flushPromises();
    const copiedIcon = button.get('svg').html();
    expect(copiedIcon).not.toBe(idleIcon);
    // Exactly one icon at a time, and the icon itself is presentational: the label carries the name.
    expect(button.findAll('svg')).toHaveLength(1);
    expect(button.get('svg').attributes('aria-hidden')).toBe('true');

    vi.advanceTimersByTime(2000);
    await flushPromises();
    expect(button.get('svg').html()).toBe(idleIcon);
  });

  it('announces the result through a polite live region outside the button, and keeps attrs on the button', async () => {
    stubClipboard(vi.fn().mockResolvedValue(undefined));
    setSecureContext(true);
    const wrapper = mount(CopyMessageButton, { props: { text: 'x' }, attrs: { class: 'bubble-copy' } });

    const status = wrapper.get('[data-testid="copy-message-status"]');
    const button = wrapper.get('[data-testid="copy-message-button"]');
    expect(status.attributes('role')).toBe('status');
    expect(status.attributes('aria-live')).toBe('polite');
    // Rendered, empty, before the click: a region inserted together with its text is often not read.
    expect(status.text()).toBe('');
    expect(button.element.contains(status.element)).toBe(false);
    expect(button.classes()).toContain('bubble-copy');

    await button.trigger('click');
    await flushPromises();
    expect(status.text()).toBe('Message copied');
  });

  it('announces a failed copy', async () => {
    setSecureContext(false);
    Object.defineProperty(document, 'execCommand', { value: () => false, configurable: true });
    const wrapper = mount(CopyMessageButton, { props: { text: 'x' } });

    await wrapper.get('[data-testid="copy-message-button"]').trigger('click');
    await flushPromises();

    expect(wrapper.get('[data-testid="copy-message-status"]').text()).toBe('Copy failed');
  });

  it('says so when copying fails', async () => {
    setSecureContext(false);
    Object.defineProperty(document, 'execCommand', { value: () => false, configurable: true });
    const wrapper = mount(CopyMessageButton, { props: { text: 'x' } });

    await wrapper.get('[data-testid="copy-message-button"]').trigger('click');
    await flushPromises();

    const button = wrapper.get('[data-testid="copy-message-button"]');
    expect(button.attributes('data-state')).toBe('failed');
    expect(button.get('.copy-message-label').text()).toBe('Copy failed');
  });
});
