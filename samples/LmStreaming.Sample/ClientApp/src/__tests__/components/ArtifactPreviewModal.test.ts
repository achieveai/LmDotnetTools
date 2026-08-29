import { describe, it, expect, vi, afterEach } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import ArtifactPreviewModal from '@/components/ArtifactPreviewModal.vue';
import { jsonResponse, textPreview, binaryPreview } from '../fixtures/fileBrowser';
import { ComponentLogger } from '@/utils/logger';

/**
 * The artifact preview popup (#583, PR 5). It rides the EXISTING file-browser preview endpoint, so
 * these tests mock `fetch` the same way `FileBrowser.test.ts` does and assert the modal's four
 * states: rendered markdown, plain text, not-previewable, and the error/no-session message.
 */

afterEach(() => vi.restoreAllMocks());

async function mountModal(response: Response | Error, path = 'docs/spec.md') {
  const fetchSpy = vi.spyOn(globalThis, 'fetch');
  if (response instanceof Error) {
    fetchSpy.mockRejectedValueOnce(response);
  } else {
    fetchSpy.mockResolvedValueOnce(response);
  }
  const wrapper = mount(ArtifactPreviewModal, {
    props: { threadId: 'thread-1', path },
    attachTo: document.body,
  });
  await flushPromises();
  return { wrapper, fetchSpy };
}

describe('ArtifactPreviewModal — fetching', () => {
  it('previews through the file-browser endpoint with the FULL workspace-relative path', async () => {
    const { fetchSpy } = await mountModal(jsonResponse(textPreview), 'docs/todo-board/spec.md');

    expect(fetchSpy.mock.calls[0][0]).toBe(
      '/api/conversations/thread-1/files/preview?path=docs%2Ftodo-board%2Fspec.md'
    );
  });
});

describe('ArtifactPreviewModal — rendering states', () => {
  it('renders a .md artifact through the markdown pipeline, not as raw text', async () => {
    const { wrapper } = await mountModal(
      jsonResponse({ previewable: true, text: '# Heading\n\nBody line.', lineCount: 3 }),
      'docs/spec.md'
    );

    const markdown = wrapper.get('[data-testid="artifact-preview-markdown"]');
    expect(markdown.find('h1').text()).toBe('Heading');
    expect(wrapper.find('[data-testid="artifact-preview-text"]').exists()).toBe(false);
    // Styled by the app's global markdown stylesheet, same as chat messages.
    expect(markdown.classes()).toContain('markdown-content');
  });

  it('renders a non-markdown previewable artifact as plain preformatted text', async () => {
    const { wrapper } = await mountModal(
      jsonResponse({ previewable: true, text: '# not markdown here', lineCount: 1 }),
      'src/notes.txt'
    );

    expect(wrapper.get('[data-testid="artifact-preview-text"]').text()).toBe('# not markdown here');
    expect(wrapper.find('[data-testid="artifact-preview-markdown"]').exists()).toBe(false);
  });

  it('shows the server reason for a non-previewable artifact instead of a blank box', async () => {
    const { wrapper } = await mountModal(jsonResponse(binaryPreview), 'out/report.md');

    const unavailable = wrapper.get('[data-testid="artifact-preview-unavailable"]');
    expect(unavailable.text()).toContain('Preview unavailable');
    expect(unavailable.text()).toContain('binary');
  });

  it('explains a missing workspace session in plain words (409 no_session_yet)', async () => {
    const { wrapper } = await mountModal(
      jsonResponse({ code: 'no_session_yet' }, 409),
      'docs/spec.md'
    );

    expect(wrapper.get('[data-testid="artifact-preview-error"]').text()).toContain(
      'no workspace session'
    );
  });

  it('degrades any other failure to a message inside the modal, never a thrown error', async () => {
    const { wrapper } = await mountModal(new TypeError('network down'), 'docs/spec.md');

    expect(wrapper.get('[data-testid="artifact-preview-error"]').text()).toContain(
      'Could not load the preview'
    );
  });
});

describe('ArtifactPreviewModal — in-flight fetch cancellation (596/F-005)', () => {
  it('hands the preview fetch an AbortSignal and aborts it on unmount', async () => {
    // A preview in flight when the conversation switches used to run to completion — up to a
    // 256 KiB read — only to be discarded because the component was already unmounted.
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockImplementationOnce(() => new Promise<Response>(() => {})); // never resolves

    const wrapper = mount(ArtifactPreviewModal, {
      props: { threadId: 'thread-1', path: 'docs/spec.md' },
      attachTo: document.body,
    });
    await flushPromises();

    const init = fetchSpy.mock.calls[0][1] as RequestInit | undefined;
    const signal = init?.signal;
    // The signal must actually reach fetch — an AbortController nothing listens to cancels nothing.
    expect(signal).toBeInstanceOf(AbortSignal);
    expect(signal!.aborted).toBe(false);

    wrapper.unmount();
    expect(signal!.aborted).toBe(true);
  });

  it('does NOT log a preview failure when the rejection is our own unmount abort', async () => {
    // The abort guard's observable effect is the log line it SKIPS: without
    // `if (abort.signal.aborted) return;` every conversation switch with a preview in flight
    // writes a spurious "Artifact preview failed" at debug (the refs the guard also skips are
    // dead after unmount and prove nothing). Deleting the guard turns this red.
    const debugSpy = vi.spyOn(ComponentLogger.prototype, 'debug').mockImplementation(() => {});
    // Faithful fake: real fetch rejects an aborted call with DOMException 'AbortError'.
    vi.spyOn(globalThis, 'fetch').mockImplementation(
      (_input, init) =>
        new Promise<Response>((_resolve, reject) => {
          init?.signal?.addEventListener('abort', () =>
            reject(new DOMException('The operation was aborted.', 'AbortError'))
          );
        })
    );

    const wrapper = mount(ArtifactPreviewModal, {
      props: { threadId: 't1', path: 'docs/spec.md' },
      attachTo: document.body,
    });
    await flushPromises();
    expect(debugSpy).not.toHaveBeenCalled();

    wrapper.unmount();
    await flushPromises();
    expect(debugSpy.mock.calls.map((c) => c[0])).not.toContain('Artifact preview failed');
  });
});

describe('ArtifactPreviewModal — backdrop beside the sidebar (#594 D6 / #603 F-001)', () => {
  // jsdom applies no CSS, so the geometry itself (`left: 280px` off the backdrop) is pinned by the
  // source guards in ChatLayout.test.ts; THIS pins the plumbing they assume — the prop must land
  // the class on BaseModal's actual backdrop element, through two component roots.
  it('marks the real .modal-backdrop element when the layout reports a sidebar column', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(jsonResponse(textPreview));
    const wrapper = mount(ArtifactPreviewModal, {
      props: { threadId: 'thread-1', path: 'docs/spec.md', besideSidebar: true },
      attachTo: document.body,
    });
    await flushPromises();

    expect(wrapper.get('.modal-backdrop').classes()).toContain('artifact-preview-beside-sidebar');
  });

  it('leaves the backdrop untouched when no sidebar column is reserved (collapsed, focus mode)', async () => {
    const { wrapper } = await mountModal(jsonResponse(textPreview));

    expect(wrapper.get('.modal-backdrop').classes()).not.toContain(
      'artifact-preview-beside-sidebar'
    );
  });
});

describe('ArtifactPreviewModal — chrome', () => {
  it('titles the modal with the full path and closes via BaseModal', async () => {
    const { wrapper } = await mountModal(jsonResponse(textPreview), 'docs/todo-board/spec.md');

    expect(wrapper.get('[data-testid="artifact-preview-modal"]').text()).toContain(
      'docs/todo-board/spec.md'
    );

    await wrapper.get('[data-testid="artifact-preview-modal-close"]').trigger('click');
    expect(wrapper.emitted('close')).toHaveLength(1);
  });
});
