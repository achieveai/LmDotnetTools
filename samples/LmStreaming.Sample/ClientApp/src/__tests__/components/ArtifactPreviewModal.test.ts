import { describe, it, expect, vi, afterEach } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import ArtifactPreviewModal from '@/components/ArtifactPreviewModal.vue';
import { jsonResponse, textPreview, binaryPreview } from '../fixtures/fileBrowser';

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
