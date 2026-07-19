import { describe, it, expect, vi, afterEach } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import FileBrowser from '@/components/FileBrowser.vue';
import FileBrowserModal from '@/components/FileBrowserModal.vue';
import {
  sampleListing,
  noSessionState,
  binaryPreview,
  textPreview,
  jsonResponse,
} from '../fixtures/fileBrowser';

afterEach(() => vi.restoreAllMocks());

/** Mounts the browser with an initial listing already loaded. Returns the wrapper + fetch spy. */
async function mountBrowser(initial = sampleListing) {
  const fetchSpy = vi.spyOn(globalThis, 'fetch');
  fetchSpy.mockResolvedValueOnce(jsonResponse(initial));
  const wrapper = mount(FileBrowser, {
    props: { threadId: 'thread-1' },
    attachTo: document.body,
  });
  await flushPromises();
  return { wrapper, fetchSpy };
}

/** Mounts the real Files modal (FileBrowser inside BaseModal) with an initial listing loaded. */
async function mountModal(initial = sampleListing) {
  const fetchSpy = vi.spyOn(globalThis, 'fetch');
  fetchSpy.mockResolvedValueOnce(jsonResponse(initial));
  const wrapper = mount(FileBrowserModal, {
    props: { threadId: 'thread-1' },
    attachTo: document.body,
  });
  await flushPromises();
  return { wrapper, fetchSpy };
}

describe('FileBrowser rendering', () => {
  it('renders one row per entry under its name testid', async () => {
    const { wrapper } = await mountBrowser();

    for (const entry of sampleListing.entries) {
      expect(wrapper.find(`[data-testid="file-entry-${entry.name}"]`).exists()).toBe(true);
    }
  });

  it('shows the "N more items" notice when moreCount > 0', async () => {
    const { wrapper } = await mountBrowser({ ...sampleListing, moreCount: 5 });

    const more = wrapper.find('[data-testid="file-browser-more"]');
    expect(more.exists()).toBe(true);
    expect(more.text()).toContain('5 more items');
  });

  it('renders the no-session empty state', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    fetchSpy.mockResolvedValueOnce(jsonResponse(noSessionState));
    const wrapper = mount(FileBrowser, { props: { threadId: 'thread-1' }, attachTo: document.body });
    await flushPromises();

    expect(wrapper.find('[data-testid="file-browser-no-session"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="file-browser-list"]').exists()).toBe(false);
  });
});

describe('FileBrowser navigation', () => {
  it('navigates into a directory row', async () => {
    const { wrapper, fetchSpy } = await mountBrowser();
    fetchSpy.mockResolvedValueOnce(jsonResponse({ ...sampleListing, path: 'src', entries: [] }));

    await wrapper.find('[data-testid="file-entry-name-src"]').trigger('click');
    await flushPromises();

    const lastUrl = fetchSpy.mock.calls.at(-1)?.[0];
    expect(lastUrl).toBe('/api/conversations/thread-1/files?path=src');
  });

  it('marks a symlink row non-navigable and offers no actions', async () => {
    const { wrapper } = await mountBrowser();

    const nameBtn = wrapper.find('[data-testid="file-entry-name-link"]');
    expect(nameBtn.attributes('disabled')).toBeDefined();
    // No file actions on a symlink.
    expect(wrapper.find('[data-testid="file-entry-preview-link"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="file-entry-download-link"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="file-entry-delete-link"]').exists()).toBe(false);
  });

  it('disables actions on a nameLossy entry and badges it', async () => {
    const { wrapper } = await mountBrowser();

    expect(wrapper.find('[data-testid="file-entry-lossy-lossy.dat"]').exists()).toBe(true);
    // A lossy file cannot be previewed/downloaded/deleted (client cannot address it safely).
    expect(wrapper.find('[data-testid="file-entry-preview-lossy.dat"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="file-entry-download-lossy.dat"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="file-entry-delete-lossy.dat"]').exists()).toBe(false);
  });
});

describe('FileBrowser preview', () => {
  it('shows metadata-only (no text panel) for a non-previewable binary file', async () => {
    const { wrapper, fetchSpy } = await mountBrowser();
    fetchSpy.mockResolvedValueOnce(jsonResponse(binaryPreview));

    await wrapper.find('[data-testid="file-entry-preview-readme.md"]').trigger('click');
    await flushPromises();

    expect(wrapper.find('[data-testid="file-preview-unavailable"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="file-preview-text"]').exists()).toBe(false);
  });

  it('renders the returned text in a <pre> for a previewable file', async () => {
    const { wrapper, fetchSpy } = await mountBrowser();
    fetchSpy.mockResolvedValueOnce(jsonResponse(textPreview));

    await wrapper.find('[data-testid="file-entry-preview-readme.md"]').trigger('click');
    await flushPromises();

    const pre = wrapper.find('[data-testid="file-preview-text"]');
    expect(pre.exists()).toBe(true);
    expect(pre.text()).toContain('line one');
  });
});

describe('FileBrowser delete confirmation', () => {
  it('opens a confirm dialog focusing Cancel; Cancel performs no delete', async () => {
    const { wrapper, fetchSpy } = await mountBrowser();
    const callsBefore = fetchSpy.mock.calls.length;

    await wrapper.find('[data-testid="file-entry-delete-readme.md"]').trigger('click');
    await flushPromises();

    const dialog = wrapper.find('[data-testid="file-browser-delete-confirm"]');
    expect(dialog.exists()).toBe(true);
    expect(dialog.find('[role="dialog"]').exists()).toBe(true);
    // File wording (not folder).
    expect(dialog.text()).toContain('Delete file readme.md?');

    const cancel = wrapper.find('[data-testid="file-browser-delete-cancel"]');
    expect(document.activeElement).toBe(cancel.element);

    await cancel.trigger('click');
    await flushPromises();

    // Dialog closed and no DELETE request was issued.
    expect(wrapper.find('[data-testid="file-browser-delete-confirm"]').exists()).toBe(false);
    expect(fetchSpy.mock.calls.length).toBe(callsBefore);
  });

  it('uses folder wording when deleting a directory', async () => {
    const { wrapper } = await mountBrowser();

    await wrapper.find('[data-testid="file-entry-delete-src"]').trigger('click');
    await flushPromises();

    const dialog = wrapper.find('[data-testid="file-browser-delete-confirm"]');
    expect(dialog.text()).toContain('Delete folder src and all its contents?');
  });

  it('Escape inside the delete-confirm cancels it without closing the Files modal', async () => {
    const { wrapper, fetchSpy } = await mountModal();
    const callsBefore = fetchSpy.mock.calls.length;

    await wrapper.find('[data-testid="file-entry-delete-readme.md"]').trigger('click');
    await flushPromises();

    const confirmEl = wrapper.find('[data-testid="file-browser-delete-confirm"]').element;
    // A bubbling Escape from inside the confirm: without scoping it would reach BaseModal's
    // document-level handler and close the WHOLE modal.
    confirmEl.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })
    );
    await flushPromises();

    // The confirm is cancelled, no DELETE was issued, and the Files modal did NOT close.
    expect(wrapper.find('[data-testid="file-browser-delete-confirm"]').exists()).toBe(false);
    expect(fetchSpy.mock.calls.length).toBe(callsBefore);
    expect(wrapper.emitted('close')).toBeUndefined();
  });
});

describe('FileBrowser upload failures', () => {
  it('surfaces per-file failures for a mixed batch (1 ok + 1 rejected)', async () => {
    const { wrapper, fetchSpy } = await mountBrowser();
    fetchSpy
      .mockResolvedValueOnce(jsonResponse({ name: 'ok.txt', size: 2 })) // upload ok.txt: 200
      .mockResolvedValueOnce(jsonResponse({ code: 'file_too_large' }, 413)) // upload big.bin: rejected
      .mockResolvedValueOnce(jsonResponse(sampleListing)); // reload listing after upload

    const inputWrapper = wrapper.find('[data-testid="file-browser-file-input"]');
    const inputEl = inputWrapper.element as HTMLInputElement;
    Object.defineProperty(inputEl, 'files', {
      configurable: true,
      value: [new File(['ok'], 'ok.txt'), new File(['x'.repeat(10)], 'big.bin')],
    });
    await inputWrapper.trigger('change');
    await flushPromises();

    const notice = wrapper.find('[data-testid="file-browser-upload-errors"]');
    expect(notice.exists()).toBe(true);
    expect(notice.text()).toContain('1 file(s) failed');
    expect(notice.text()).toContain('big.bin (file_too_large)');
    // The successful file's reload still ran (2 uploads + 1 reload after the initial listing).
    expect(fetchSpy).toHaveBeenCalledTimes(4);
  });
});
