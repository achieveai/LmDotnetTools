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
    expect(button.attributes('aria-label')).toBe('Copy message');
    await button.trigger('click');
    await flushPromises();

    expect(writeText).toHaveBeenCalledWith(MARKDOWN);
    expect(button.text()).toContain('Copied');

    vi.advanceTimersByTime(2000);
    await flushPromises();
    expect(button.text()).toContain('Copy');
    expect(button.text()).not.toContain('Copied');
  });

  it('says so when copying fails', async () => {
    setSecureContext(false);
    Object.defineProperty(document, 'execCommand', { value: () => false, configurable: true });
    const wrapper = mount(CopyMessageButton, { props: { text: 'x' } });

    await wrapper.get('[data-testid="copy-message-button"]').trigger('click');
    await flushPromises();

    expect(wrapper.get('[data-testid="copy-message-button"]').text()).toContain('Copy failed');
  });
});
