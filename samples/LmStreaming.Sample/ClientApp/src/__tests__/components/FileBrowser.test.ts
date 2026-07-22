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
import { MAX_FOLDER_UPLOAD_FILES } from '@/utils/folderUpload';

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

  it('marks the background content inert while the confirm is open, with the dialog OUTSIDE it', async () => {
    const { wrapper } = await mountBrowser();

    // No confirmation yet → the background is interactive (no inert attribute).
    expect(wrapper.find('.fb-main').attributes('inert')).toBeUndefined();

    await wrapper.find('[data-testid="file-entry-delete-readme.md"]').trigger('click');
    await flushPromises();

    const main = wrapper.find('.fb-main');
    // Background is inert so BaseModal's trap (which skips [inert]) and pointer input stay off it...
    expect(main.attributes('inert')).toBeDefined();
    // ...while the confirmation overlay is a SIBLING of the inert region, not a descendant, so it stays live.
    expect(main.find('[data-testid="file-browser-delete-confirm"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="file-browser-delete-confirm"]').exists()).toBe(true);
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

/** A File-like carrying a webkitRelativePath (happy-dom's File lacks a settable one). */
function relFile(relativePath: string): File {
  const name = relativePath.split('/').pop() ?? relativePath;
  const file = new File(['x'], name);
  Object.defineProperty(file, 'webkitRelativePath', { value: relativePath, configurable: true });
  return file;
}

/** A leaf file entry for a fake drag-drop tree. */
function fileEntry(name: string) {
  return { isFile: true, isDirectory: false, name, file: (cb: (f: File) => void) => cb(new File(['d'], name)) };
}

/** A directory entry whose reader returns one non-empty batch then an empty one. */
function dirEntry(name: string, children: Array<ReturnType<typeof fileEntry> | Record<string, unknown>>) {
  return {
    isFile: false,
    isDirectory: true,
    name,
    createReader: () => {
      let done = false;
      return {
        readEntries: (cb: (entries: unknown[]) => void) => {
          cb(done ? [] : children);
          done = true;
        },
      };
    },
  };
}

/** Dispatches a native drop carrying a webkitGetAsEntry-capable dataTransfer onto the dropzone. */
function dispatchDrop(wrapper: ReturnType<typeof mount>, entries: unknown[]): void {
  const dt = {
    items: entries.map((entry) => ({ kind: 'file', webkitGetAsEntry: () => entry })),
    files: [] as File[],
  };
  const event = new Event('drop', { bubbles: true, cancelable: true });
  Object.defineProperty(event, 'dataTransfer', { value: dt, configurable: true });
  wrapper.find('[data-testid="file-browser-dropzone"]').element.dispatchEvent(event);
}

describe('FileBrowser new folder', () => {
  it('opens a name-entry dialog (focusing its input), marking the background inert', async () => {
    const { wrapper } = await mountBrowser();

    await wrapper.find('[data-testid="file-browser-new-folder"]').trigger('click');
    await flushPromises();

    const dialog = wrapper.find('[data-testid="file-browser-new-folder-dialog"]');
    expect(dialog.exists()).toBe(true);
    const input = wrapper.find('[data-testid="file-browser-new-folder-input"]');
    expect(document.activeElement).toBe(input.element);
    // Background is inert (focus + pointer confined to the dialog, which is OUTSIDE the inert subtree).
    expect(wrapper.find('.fb-main').attributes('inert')).toBeDefined();
  });

  it('creates the folder then refreshes the listing on confirm', async () => {
    const { wrapper, fetchSpy } = await mountBrowser();

    await wrapper.find('[data-testid="file-browser-new-folder"]').trigger('click');
    await flushPromises();
    await wrapper.find('[data-testid="file-browser-new-folder-input"]').setValue('docs');

    fetchSpy
      .mockResolvedValueOnce(jsonResponse({ path: 'docs' })) // create directory
      .mockResolvedValueOnce(jsonResponse(sampleListing)); // reload listing
    await wrapper.find('[data-testid="file-browser-new-folder-confirm"]').trigger('click');
    await flushPromises();

    const posts = fetchSpy.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === 'POST');
    expect(posts.length).toBe(1);
    expect(posts[0][0]).toBe('/api/conversations/thread-1/files/directory');
    expect(JSON.parse((posts[0][1] as RequestInit).body as string)).toEqual({ name: 'docs' });
    // Dialog closed and the listing was reloaded (create POST + reload GET after the initial listing).
    expect(wrapper.find('[data-testid="file-browser-new-folder-dialog"]').exists()).toBe(false);
    expect(fetchSpy).toHaveBeenCalledTimes(3);
  });

  it('disables Create while the name is blank', async () => {
    const { wrapper } = await mountBrowser();

    await wrapper.find('[data-testid="file-browser-new-folder"]').trigger('click');
    await flushPromises();

    const confirm = wrapper.find('[data-testid="file-browser-new-folder-confirm"]');
    expect(confirm.attributes('disabled')).toBeDefined();
    await wrapper.find('[data-testid="file-browser-new-folder-input"]').setValue('docs');
    expect(confirm.attributes('disabled')).toBeUndefined();
  });
});

describe('FileBrowser folder upload (picker)', () => {
  it('uploads each picked file sequentially, sending its relativePath', async () => {
    const { wrapper, fetchSpy } = await mountBrowser();
    const bodies: FormData[] = [];
    fetchSpy.mockImplementation((_url, init) => {
      const method = (init as RequestInit)?.method;
      if (method === 'POST') {
        bodies.push((init as RequestInit).body as FormData);
        return Promise.resolve(jsonResponse({ name: 'x', size: 1 }));
      }
      return Promise.resolve(jsonResponse(sampleListing));
    });

    const input = wrapper.find('[data-testid="file-browser-folder-input"]');
    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [relFile('proj/a.txt'), relFile('proj/sub/b.txt')],
    });
    await input.trigger('change');
    await flushPromises();

    expect(bodies.map((b) => b.get('relativePath'))).toEqual(['proj/a.txt', 'proj/sub/b.txt']);
  });

  it('shows an unsupported hint when the platform lacks webkitdirectory (flat upload still works)', async () => {
    // happy-dom does not implement webkitdirectory, so the affordance renders in its unsupported state.
    const { wrapper } = await mountBrowser();
    expect(wrapper.find('[data-testid="file-browser-folder-unsupported"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="file-browser-folder-upload"]').attributes('disabled')).toBeDefined();
    // The flat picker is untouched.
    expect(wrapper.find('[data-testid="file-browser-file-input"]').exists()).toBe(true);
  });
});

describe('FileBrowser folder upload (drag & drop)', () => {
  it('traverses a dropped folder and uploads files with distinct relativePaths, WITHOUT an overwrite preflight', async () => {
    const { wrapper, fetchSpy } = await mountBrowser();
    const bodies: FormData[] = [];
    fetchSpy.mockImplementation((_url, init) => {
      const method = (init as RequestInit)?.method;
      if (method === 'POST') {
        bodies.push((init as RequestInit).body as FormData);
        return Promise.resolve(jsonResponse({ name: 'x', size: 1 }));
      }
      return Promise.resolve(jsonResponse(sampleListing));
    });

    // The dropped tree contains readme.md (which collides with the EXISTING root readme.md) plus a
    // duplicate basename across two subfolders — folder uploads must skip the basename preflight.
    const tree = dirEntry('proj', [
      fileEntry('readme.md'),
      dirEntry('a', [fileEntry('dup.txt')]),
      dirEntry('b', [fileEntry('dup.txt')]),
    ]);
    dispatchDrop(wrapper, [tree]);
    await flushPromises();

    // No advisory overwrite confirmation was shown for the folder drop.
    expect(wrapper.find('[data-testid="file-browser-overwrite-confirm"]').exists()).toBe(false);
    const paths = bodies.map((b) => b.get('relativePath')).sort();
    expect(paths).toEqual(['proj/a/dup.txt', 'proj/b/dup.txt', 'proj/readme.md']);
  });
});

describe('FileBrowser fixed-height list', () => {
  it('constrains the list to a stable-height, internally-scrolling panel', async () => {
    const { wrapper } = await mountBrowser();

    const list = wrapper.find('[data-testid="file-browser-list"]');
    expect(list.exists()).toBe(true);
    const style = list.attributes('style') ?? '';
    expect(style).toContain('overflow-y: auto');
    expect(style).toContain('min-height: 0');
    expect(style).toMatch(/height:/);
  });

  it('renders the empty state INSIDE the fixed-height list container (constant space when empty)', async () => {
    const { wrapper } = await mountBrowser({ ...sampleListing, entries: [] });

    const list = wrapper.find('[data-testid="file-browser-list"]');
    expect(list.find('[data-testid="file-browser-empty"]').exists()).toBe(true);
  });

  // F8: the fixed-height container must stay mounted while loading (loading indicator INSIDE it) so the
  // panel does not collapse to a one-line div and re-expand on every load/navigation/refresh.
  it('keeps the fixed-height container mounted (loading indicator inside) while loading', async () => {
    // A listing fetch that never resolves keeps the browser in its loading state.
    vi.spyOn(globalThis, 'fetch').mockReturnValue(new Promise<Response>(() => {}));
    const wrapper = mount(FileBrowser, { props: { threadId: 'thread-1' }, attachTo: document.body });
    await flushPromises();

    const list = wrapper.find('[data-testid="file-browser-list"]');
    expect(list.exists()).toBe(true);
    const style = list.attributes('style') ?? '';
    expect(style).toContain('overflow-y: auto');
    expect(style).toContain('min-height: 0');
    expect(style).toMatch(/height:/);
    // The loading indicator lives INSIDE the stable-height panel, not replacing it.
    const loading = wrapper.find('[data-testid="file-browser-loading"]');
    expect(loading.exists()).toBe(true);
    expect(list.find('[data-testid="file-browser-loading"]').exists()).toBe(true);
  });
});

describe('FileBrowser mixed drop (F2)', () => {
  it('uploads the folder WITHOUT preflight while a colliding loose file triggers the overwrite confirm', async () => {
    const { wrapper, fetchSpy } = await mountBrowser();
    const bodies: FormData[] = [];
    fetchSpy.mockImplementation((_url, init) => {
      if ((init as RequestInit)?.method === 'POST') {
        bodies.push((init as RequestInit).body as FormData);
        return Promise.resolve(jsonResponse({ name: 'x', size: 1 }));
      }
      return Promise.resolve(jsonResponse(sampleListing));
    });

    // A loose readme.md (collides with the EXISTING root readme.md) dropped ALONGSIDE a folder.
    dispatchDrop(wrapper, [dirEntry('proj', [fileEntry('a.txt')]), fileEntry('readme.md')]);
    await flushPromises();

    // The folder's file uploaded (proj/a.txt), WITHOUT any preflight for it...
    const folderPaths = bodies.map((b) => b.get('relativePath'));
    expect(folderPaths).toContain('proj/a.txt');
    // ...and the loose readme.md is held at the overwrite confirmation (NOT uploaded).
    expect(wrapper.find('[data-testid="file-browser-overwrite-confirm"]').exists()).toBe(true);
    expect(folderPaths).not.toContain('readme.md');
    // No flat (relativePath-less) upload was issued for readme.md while the confirm is pending.
    expect(bodies.some((b) => !b.get('relativePath'))).toBe(false);
  });
});

describe('FileBrowser folder over-limit rejection (F3/F4)', () => {
  /** N leaf file entries for a fake drag-drop tree. */
  function manyFileEntries(n: number): ReturnType<typeof fileEntry>[] {
    return Array.from({ length: n }, (_, i) => fileEntry(`f${i}.txt`));
  }

  it('rejects an over-limit DROP with a visible error and uploads nothing (F3)', async () => {
    const { wrapper, fetchSpy } = await mountBrowser();
    const postsBefore = fetchSpy.mock.calls.length;

    dispatchDrop(wrapper, [dirEntry('big', manyFileEntries(MAX_FOLDER_UPLOAD_FILES + 1))]);
    await flushPromises();

    const notice = wrapper.find('[data-testid="file-browser-upload-errors"]');
    expect(notice.exists()).toBe(true);
    expect(notice.text()).toContain(String(MAX_FOLDER_UPLOAD_FILES));
    // No upload POST was issued — the whole selection is rejected, not partially uploaded.
    const posts = fetchSpy.mock.calls
      .slice(postsBefore)
      .filter(([, init]) => (init as RequestInit | undefined)?.method === 'POST');
    expect(posts).toHaveLength(0);
  });

  it('rejects an over-limit folder PICK with the same visible error and uploads nothing (F4)', async () => {
    const { wrapper, fetchSpy } = await mountBrowser();
    const postsBefore = fetchSpy.mock.calls.length;

    const input = wrapper.find('[data-testid="file-browser-folder-input"]');
    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: Array.from({ length: MAX_FOLDER_UPLOAD_FILES + 1 }, (_, i) => relFile(`proj/f${i}.txt`)),
    });
    await input.trigger('change');
    await flushPromises();

    const notice = wrapper.find('[data-testid="file-browser-upload-errors"]');
    expect(notice.exists()).toBe(true);
    expect(notice.text()).toContain(String(MAX_FOLDER_UPLOAD_FILES));
    const posts = fetchSpy.mock.calls
      .slice(postsBefore)
      .filter(([, init]) => (init as RequestInit | undefined)?.method === 'POST');
    expect(posts).toHaveLength(0);
  });
});

describe('FileBrowser upload single-flight (F5)', () => {
  it('disables the upload buttons and ignores new drops while a batch is uploading', async () => {
    const { wrapper, fetchSpy } = await mountBrowser();
    const pending: Array<{ resolve: (r: Response) => void }> = [];
    fetchSpy.mockImplementation((_url, init) => {
      if ((init as RequestInit)?.method === 'POST') {
        return new Promise((resolve) => pending.push({ resolve }));
      }
      return Promise.resolve(jsonResponse(sampleListing));
    });

    // Start a flat upload that stays in flight.
    const input = wrapper.find('[data-testid="file-browser-file-input"]');
    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [new File(['a'], 'a.txt')],
    });
    await input.trigger('change');
    await flushPromises();
    expect(pending).toHaveLength(1);

    // The flat "Choose files" button is disabled while a batch runs (the folder button is already
    // disabled by happy-dom's lack of webkitdirectory, so it isn't a useful discriminator here).
    expect(wrapper.find('[data-testid="file-browser-upload"]').attributes('disabled')).toBeDefined();

    // A new drop while uploading is IGNORED (no additional POST issued).
    dispatchDrop(wrapper, [fileEntry('b.txt')]);
    await flushPromises();
    expect(pending).toHaveLength(1);

    // Finish the in-flight upload; the buttons re-enable.
    pending[0].resolve(jsonResponse({ name: 'a.txt', size: 1 }));
    await flushPromises();
    expect(wrapper.find('[data-testid="file-browser-upload"]').attributes('disabled')).toBeUndefined();
  });
});

describe('FileBrowser workspace display', () => {
  it('shows the active workspace id', async () => {
    const { wrapper } = await mountBrowser();

    const ws = wrapper.find('[data-testid="file-browser-workspace"]');
    expect(ws.exists()).toBe(true);
    expect(ws.text()).toContain('ws-1');
  });
});

describe('FileBrowser overwrite confirmation', () => {
  function setFiles(wrapper: ReturnType<typeof mount>, names: string[]): HTMLInputElement {
    const inputEl = wrapper.find('[data-testid="file-browser-file-input"]').element as HTMLInputElement;
    Object.defineProperty(inputEl, 'files', {
      configurable: true,
      value: names.map((name) => new File(['x'], name)),
    });
    return inputEl;
  }

  it('shows an advisory confirm BEFORE upload when a name collides; Overwrite uploads the batch', async () => {
    const { wrapper, fetchSpy } = await mountBrowser();
    setFiles(wrapper, ['readme.md']); // collides with the existing readme.md
    await wrapper.find('[data-testid="file-browser-file-input"]').trigger('change');
    await flushPromises();

    const confirm = wrapper.find('[data-testid="file-browser-overwrite-confirm"]');
    expect(confirm.exists()).toBe(true);
    expect(confirm.text()).toContain('readme.md');
    // No upload was issued yet — only the initial listing fetch.
    expect(fetchSpy.mock.calls.length).toBe(1);

    fetchSpy
      .mockResolvedValueOnce(jsonResponse(sampleListing)) // re-check reload (applied against a fresh listing)
      .mockResolvedValueOnce(jsonResponse({ name: 'readme.md', size: 1 })) // upload
      .mockResolvedValueOnce(jsonResponse(sampleListing)); // post-batch reload
    await wrapper.find('[data-testid="file-browser-overwrite-confirm-btn"]').trigger('click');
    await flushPromises();

    expect(wrapper.find('[data-testid="file-browser-overwrite-confirm"]').exists()).toBe(false);
    const uploads = fetchSpy.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === 'POST');
    expect(uploads.length).toBe(1);
  });

  it('Skip existing uploads only the non-colliding files', async () => {
    const { wrapper, fetchSpy } = await mountBrowser();
    setFiles(wrapper, ['readme.md', 'new.txt']); // one collides, one is new
    await wrapper.find('[data-testid="file-browser-file-input"]').trigger('change');
    await flushPromises();

    expect(wrapper.find('[data-testid="file-browser-overwrite-confirm"]').exists()).toBe(true);

    fetchSpy
      .mockResolvedValueOnce(jsonResponse({ name: 'new.txt', size: 1 })) // upload new.txt only
      .mockResolvedValueOnce(jsonResponse(sampleListing)); // reload
    await wrapper.find('[data-testid="file-browser-overwrite-cancel"]').trigger('click');
    await flushPromises();

    // Only the non-colliding file uploaded (readme.md was skipped).
    const uploads = fetchSpy.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === 'POST');
    expect(uploads.length).toBe(1);
  });
});

describe('FileBrowser overwrite-pending admission barrier (F5 round-2)', () => {
  /** Opens a flat overwrite confirmation (readme.md collides with the existing root readme.md). */
  async function openOverwriteConfirm(wrapper: ReturnType<typeof mount>): Promise<void> {
    const input = wrapper.find('[data-testid="file-browser-file-input"]');
    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [new File(['x'], 'readme.md')],
    });
    await input.trigger('change');
    await flushPromises();
  }

  it('ignores a folder pick AND a folder drop while an overwrite confirm is pending, then still confirms', async () => {
    const { wrapper, fetchSpy } = await mountBrowser();
    await openOverwriteConfirm(wrapper);
    expect(wrapper.find('[data-testid="file-browser-overwrite-confirm"]').exists()).toBe(true);

    // (c) Both upload entry buttons are disabled while the confirmation is pending.
    expect(wrapper.find('[data-testid="file-browser-upload"]').attributes('disabled')).toBeDefined();
    expect(wrapper.find('[data-testid="file-browser-folder-upload"]').attributes('disabled')).toBeDefined();

    const bodies: FormData[] = [];
    fetchSpy.mockImplementation((_url, init) => {
      if ((init as RequestInit)?.method === 'POST') {
        bodies.push((init as RequestInit).body as FormData);
        return Promise.resolve(jsonResponse({ name: 'readme.md', size: 1 }));
      }
      return Promise.resolve(jsonResponse(sampleListing));
    });

    // (a) A folder PICK while the overwrite confirm is pending is IGNORED (no folder batch starts).
    const folderInput = wrapper.find('[data-testid="file-browser-folder-input"]');
    Object.defineProperty(folderInput.element, 'files', {
      configurable: true,
      value: [relFile('proj/a.txt')],
    });
    await folderInput.trigger('change');
    await flushPromises();

    // (a) A folder DROP while pending is likewise IGNORED.
    dispatchDrop(wrapper, [dirEntry('proj', [fileEntry('b.txt')])]);
    await flushPromises();

    // No folder (relativePath) upload happened while the confirmation was pending...
    expect(bodies.some((b) => b.get('relativePath'))).toBe(false);
    // ...and the confirmation is still open (the ignored folder actions did not disturb it).
    expect(wrapper.find('[data-testid="file-browser-overwrite-confirm"]').exists()).toBe(true);

    // (b) Confirming the flat overwrite still uploads readme.md correctly afterward.
    await wrapper.find('[data-testid="file-browser-overwrite-confirm-btn"]').trigger('click');
    await flushPromises();
    expect(wrapper.find('[data-testid="file-browser-overwrite-confirm"]').exists()).toBe(false);
    const flatUploads = bodies.filter((b) => b.get('file') && !b.get('relativePath'));
    expect(flatUploads).toHaveLength(1);
  });

  it('re-checks (reloads) the listing before applying a confirmed overwrite', async () => {
    const { wrapper, fetchSpy } = await mountBrowser();
    await openOverwriteConfirm(wrapper);

    const methods: string[] = [];
    fetchSpy.mockImplementation((_url, init) => {
      const isPost = (init as RequestInit)?.method === 'POST';
      methods.push(isPost ? 'POST' : 'GET');
      return Promise.resolve(isPost ? jsonResponse({ name: 'readme.md', size: 1 }) : jsonResponse(sampleListing));
    });

    await wrapper.find('[data-testid="file-browser-overwrite-confirm-btn"]').trigger('click');
    await flushPromises();

    // A GET (the re-check reload) is issued BEFORE the upload POST, so the decision isn't applied stale.
    const firstPost = methods.indexOf('POST');
    expect(firstPost).toBeGreaterThan(0);
    expect(methods.slice(0, firstPost)).toContain('GET');
  });
});
