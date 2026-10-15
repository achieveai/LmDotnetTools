import { afterEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import SandboxAppPreview from '@/components/SandboxAppPreview.vue';

describe('SandboxAppPreview', () => {
  afterEach(() => { vi.restoreAllMocks(); document.body.replaceChildren(); });

  it('posts the one-use ticket into its own iframe, never putting it in a URL', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({
      url: 'https://instance.apps.example/_launch', ticket: 'one-use-secret',
    }), { status: 200 }));
    const submitted = vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(function (this: HTMLFormElement) {
      expect(this.action).toBe('https://instance.apps.example/_launch');
      expect(this.method).toBe('post');
      expect(this.querySelector<HTMLInputElement>('input[name="ticket"]')?.value).toBe('one-use-secret');
      expect(this.target).toBe(document.querySelector('iframe')?.name);
    });

    mount(SandboxAppPreview, { attachTo: document.body, props: {
      threadId: 'thread-1', workspaceId: 'workspace-1', appId: 'budget', name: 'Budget explorer',
    } });
    await flushPromises();

    expect(submitted).toHaveBeenCalledTimes(1);
    expect(fetchSpy).toHaveBeenCalledWith('/api/conversations/thread-1/apps/budget/launch?workspace=workspace-1', expect.objectContaining({ method: 'POST' }));
    const iframe = document.querySelector('iframe');
    expect(iframe?.src).not.toContain('one-use-secret');
    expect(iframe?.getAttribute('sandbox')).toBe('allow-scripts allow-forms allow-same-origin');
    expect(iframe?.getAttribute('referrerpolicy')).toBe('no-referrer');
  });

  it('shows a retry control when launch fails', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(new Response('{}', { status: 503 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({
        url: 'https://second.apps.example/_launch', ticket: 'next-ticket',
      }), { status: 200 }));
    const submit = vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(() => {});
    const wrapper = mount(SandboxAppPreview, { attachTo: document.body, props: {
      threadId: 'thread-1', workspaceId: 'workspace-1', appId: 'budget', name: 'Budget explorer',
    } });
    await flushPromises();
    expect(wrapper.text()).toContain('Unavailable');
    await wrapper.get('[data-testid="sandbox-app-retry"]').trigger('click');
    await flushPromises();
    expect(submit).toHaveBeenCalledTimes(1);
  });

  it('marks the app unavailable when its own iframe reports a failed exchange', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({
      url: 'https://instance.apps.example/_launch', ticket: 'one-use-secret',
    }), { status: 200 }));
    vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(() => {});
    const wrapper = mount(SandboxAppPreview, { attachTo: document.body, props: {
      threadId: 'thread-1', workspaceId: 'workspace-1', appId: 'budget', name: 'Budget explorer',
    } });
    await flushPromises();
    const iframe = wrapper.get('iframe');
    window.dispatchEvent(new MessageEvent('message', {
      origin: 'https://evil.example', source: iframe.element.contentWindow,
      data: { type: 'sandbox-app-failed' },
    }));
    expect(wrapper.text()).not.toContain('Unavailable');
    window.dispatchEvent(new MessageEvent('message', {
      origin: 'https://instance.apps.example', source: iframe.element.contentWindow,
      data: { type: 'sandbox-app-failed' },
    }));
    await iframe.trigger('load');
    expect(wrapper.text()).toContain('Unavailable');
    expect(wrapper.find('[data-testid="sandbox-app-retry"]').exists()).toBe(true);
    wrapper.unmount();
  });

  it('keeps a failed frame unavailable if a load event follows the error', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({
      url: 'https://instance.apps.example/_launch', ticket: 'one-use-secret',
    }), { status: 200 }));
    vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(() => {});
    const wrapper = mount(SandboxAppPreview, { attachTo: document.body, props: {
      threadId: 'thread-1', workspaceId: 'workspace-1', appId: 'budget', name: 'Budget explorer',
    } });
    await flushPromises();
    const iframe = wrapper.get('iframe');
    await iframe.trigger('error');
    await iframe.trigger('load');
    expect(wrapper.text()).toContain('Unavailable');
    wrapper.unmount();
  });
});
