import { describe, it, expect, vi, afterEach } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import { ref } from 'vue';
import ArtifactPreviewModal from '@/components/ArtifactPreviewModal.vue';
import { jsonResponse, textPreview, binaryPreview } from '../fixtures/fileBrowser';
import { ComponentLogger } from '@/utils/logger';
import type { DirectoryListing, FileEntry } from '@/types/fileBrowser';
import { WORKSPACE_FILE_LINKS } from '@/utils/workspaceLinks';

vi.mock('@/components/DiagramViewer.vue', () => ({
  default: {
    props: ['source', 'language'],
    template: '<figure class="diagram-proof" :data-language="language">{{ source }}</figure>',
  },
}));

/**
 * The artifact preview popup (#583, PR 5). It rides the EXISTING file-browser preview endpoint, so
 * these tests mock `fetch` the same way `FileBrowser.test.ts` does and assert the modal's four
 * states: rendered markdown, plain text, not-previewable, and the error/no-session message.
 */

afterEach(() => vi.restoreAllMocks());

const imageEntry = (name: string, size: number | null): FileEntry => ({ name, type: 'file', size, nameLossy: false });

const listing = (path: string, entries: FileEntry[], moreCount = 0): DirectoryListing => ({
  workspaceId: 'ws-1',
  path,
  entries,
  moreCount,
});

async function mountModal(
  response: Response | Error,
  path = 'docs/spec.md',
  props: { embedded?: boolean; expanded?: boolean } = {}
) {
  const fetchSpy = vi.spyOn(globalThis, 'fetch');
  if (response instanceof Error) {
    fetchSpy.mockRejectedValueOnce(response);
  } else {
    fetchSpy.mockResolvedValueOnce(response);
  }
  const wrapper = mount(ArtifactPreviewModal, {
    props: { threadId: 'thread-1', path, ...props },
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
      props: { threadId: 'thread-1', path: 'docs/spec.md', embedded: true },
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
      props: { threadId: 't1', path: 'docs/spec.md', embedded: true },
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

  it('renders an embedded labelled region without modal semantics or a focus-trapping backdrop', async () => {
    const { wrapper } = await mountModal(
      jsonResponse(textPreview),
      'docs/todo-board/spec.md',
      { embedded: true }
    );

    const surface = wrapper.get('[data-testid="artifact-preview-surface"]');
    expect(surface.element.tagName).toBe('SECTION');
    expect(surface.attributes('role')).toBe('region');
    expect(surface.attributes('aria-label')).toBe('File preview: spec.md');
    expect(wrapper.find('.modal-backdrop').exists()).toBe(false);
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false);
  });

  it('shows filename and path, then emits compact expand and close actions', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(jsonResponse(textPreview));
    const wrapper = mount(ArtifactPreviewModal, {
      props: {
        threadId: 'thread-1',
        path: 'docs/todo-board/spec.md',
        embedded: true,
        expanded: false,
      },
      attachTo: document.body,
    });
    await flushPromises();

    expect(wrapper.get('[data-testid="artifact-preview-filename"]').text()).toBe('spec.md');
    expect(wrapper.get('[data-testid="artifact-preview-path"]').text()).toBe('docs/todo-board/spec.md');
    const expand = wrapper.get('[data-testid="artifact-preview-expand"]');
    expect(expand.attributes('aria-label')).toBe('Expand file preview');
    await expand.trigger('click');
    expect(wrapper.emitted('toggleExpand')).toHaveLength(1);

    await wrapper.setProps({ expanded: true });
    expect(wrapper.get('[data-testid="artifact-preview-expand"]').attributes('aria-label')).toBe(
      'Restore file preview'
    );
    await wrapper.get('[data-testid="artifact-preview-close"]').trigger('click');
    expect(wrapper.emitted('close')).toHaveLength(1);
    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });
});

describe('ArtifactPreviewModal — embedded Markdown', () => {
  it('uses completed TextMessage rendering for diagrams while preserving ordinary code', async () => {
    const body = [
      '```mermaid\ngraph LR; A-->B\n```',
      '```plantuml\n@startuml\nAlice -> Bob\n@enduml\n```',
      '```typescript\nconst answer = 42;\n```',
    ].join('\n\n');
    const { wrapper } = await mountModal(
      jsonResponse({ previewable: true, text: body, lineCount: 11 }),
      'docs/diagrams.md',
      { embedded: true }
    );
    await flushPromises();

    expect(wrapper.findAll('.diagram-proof')).toHaveLength(2);
    expect(wrapper.get('[data-language="mermaid"]').text()).toContain('A-->B');
    expect(wrapper.get('[data-language="plantuml"]').text()).toContain('Alice -> Bob');
    expect(wrapper.get('code.language-typescript').text()).toContain('const answer = 42;');
  });

  it('keeps workspace links disabled and removes unsafe raw SVG', async () => {
    const open = vi.fn();
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      jsonResponse({
        previewable: true,
        text: '[Other file](docs/other.md)\n\n<svg onload="alert(1)"><script>alert(2)</script></svg>',
        lineCount: 3,
      })
    );
    const wrapper = mount(ArtifactPreviewModal, {
      props: { threadId: 'thread-1', path: 'docs/spec.md', embedded: true },
      global: {
        provide: {
          [WORKSPACE_FILE_LINKS]: { threadId: ref('thread-1'), open },
        },
      },
      attachTo: document.body,
    });
    await flushPromises();

    expect(wrapper.get('a').classes()).not.toContain('workspace-link');
    await wrapper.get('a').trigger('click');
    expect(open).not.toHaveBeenCalled();
    const markdown = wrapper.get('[data-testid="artifact-preview-markdown"]');
    expect(markdown.find('svg').exists()).toBe(false);
    expect(markdown.find('script').exists()).toBe(false);
  });
});


describe('ArtifactPreviewModal — chat file links (target resolved on the server)', () => {
  function mountTarget(responses: Response[], target = 'B:\\ws\\docs\\report.md') {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    for (const r of responses) fetchSpy.mockResolvedValueOnce(r);
    const wrapper = mount(ArtifactPreviewModal, {
      props: { threadId: 'thread-1', target },
      attachTo: document.body,
    });
    return { wrapper, fetchSpy };
  }

  it('resolves the raw target for THIS conversation, then previews the resolved path', async () => {
    const { wrapper, fetchSpy } = mountTarget([
      jsonResponse({ path: 'docs/report.md', type: 'file', size: 20 }),
      jsonResponse({ previewable: true, text: '# Report', lineCount: 1 }),
    ]);
    await flushPromises();

    expect(fetchSpy.mock.calls[0][0]).toBe(
      '/api/conversations/thread-1/files/resolve?target=B%3A%5Cws%5Cdocs%5Creport.md'
    );
    expect(fetchSpy.mock.calls[1][0]).toBe(
      '/api/conversations/thread-1/files/preview?path=docs%2Freport.md'
    );
    expect(wrapper.get('[data-testid="artifact-preview-markdown"]').find('h1').text()).toBe('Report');
    expect(wrapper.get('[data-testid="artifact-preview-modal"]').text()).toContain('docs/report.md');
  });

  it.each([
    [400, 'outside_workspace', 'outside the workspace'],
    [400, 'invalid_path', 'not a valid workspace path'],
    [404, 'not_found', 'not found in the workspace'],
  ])('explains a %i %s resolve failure', async (status, code, message) => {
    const { wrapper, fetchSpy } = mountTarget([jsonResponse({ code }, status)]);
    await flushPromises();

    expect(wrapper.get('[data-testid="artifact-preview-error"]').text()).toContain(message);
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(wrapper.find('[data-testid="artifact-preview-download"]').exists()).toBe(false);
  });

  it('says a folder link is a folder and fetches nothing else', async () => {
    const { wrapper, fetchSpy } = mountTarget(
      [jsonResponse({ path: 'docs', type: 'directory', size: null })],
      'docs/'
    );
    await flushPromises();

    expect(wrapper.get('[data-testid="artifact-preview-unavailable"]').text()).toContain('folder');
    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });
});

describe('ArtifactPreviewModal — viewers', () => {
  it('renders a CSV as a table with a header row', async () => {
    const { wrapper } = await mountModal(
      jsonResponse({ previewable: true, text: 'name,qty\n"Widget, large",3\nBolt,10', lineCount: 3 }),
      'data/items.csv'
    );

    const table = wrapper.get('[data-testid="artifact-preview-table"]');
    expect(table.findAll('th').map((th) => th.text())).toEqual(['name', 'qty']);
    expect(table.findAll('tbody tr').map((tr) => tr.findAll('td').map((td) => td.text()))).toEqual([
      ['Widget, large', '3'],
      ['Bolt', '10'],
    ]);
  });

  it('renders a TSV as a table', async () => {
    const { wrapper } = await mountModal(
      jsonResponse({ previewable: true, text: 'a\tb\n1\t2', lineCount: 2 }),
      'out.tsv'
    );
    expect(wrapper.get('[data-testid="artifact-preview-table"]').findAll('th')).toHaveLength(2);
  });

  it('shows an image from the download bytes as a typed object URL, and revokes it on close', async () => {
    const createObjectURL = vi.fn((_: Blob) => 'blob:preview-1');
    const revokeObjectURL = vi.fn();
    Object.assign(URL, { createObjectURL, revokeObjectURL });
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(jsonResponse(listing('img', [imageEntry('chart.png', 4)])))
      .mockResolvedValueOnce(new Response(new Uint8Array([137, 80, 78, 71]), { status: 200 }));

    const wrapper = mount(ArtifactPreviewModal, {
      props: { threadId: 'thread-1', path: 'img/chart.png', embedded: true },
      attachTo: document.body,
    });
    await flushPromises();

    // The chip's bare path carries no size, so the parent listing is read before any bytes.
    expect(fetchSpy.mock.calls[0][0]).toBe('/api/conversations/thread-1/files?path=img');
    expect(fetchSpy.mock.calls[1][0]).toBe(
      '/api/conversations/thread-1/files/download?path=img%2Fchart.png'
    );
    // The server answers application/octet-stream + nosniff, so the blob is re-typed from the extension.
    expect(createObjectURL.mock.calls[0][0].type).toBe('image/png');
    expect(wrapper.get('[data-testid="artifact-preview-image"]').attributes('src')).toBe('blob:preview-1');

    wrapper.unmount();
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:preview-1');
  });

  it('does not fetch an image above the in-page size cap; offers the download instead', async () => {
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(jsonResponse({ path: 'big.jpg', type: 'file', size: 64 * 1024 * 1024 }));
    const wrapper = mount(ArtifactPreviewModal, {
      props: { threadId: 'thread-1', target: 'big.jpg' },
      attachTo: document.body,
    });
    await flushPromises();

    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(wrapper.get('[data-testid="artifact-preview-unavailable"]').text()).toContain('too large');
    expect(wrapper.find('[data-testid="artifact-preview-download"]').exists()).toBe(true);
  });

  function mountImagePath(responses: Response[], path: string) {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    for (const r of responses) fetchSpy.mockResolvedValueOnce(r);
    const wrapper = mount(ArtifactPreviewModal, {
      props: { threadId: 'thread-1', path },
      attachTo: document.body,
    });
    return { wrapper, fetchSpy };
  }

  it('applies the same cap to an artifact chip path, reading the size from the parent listing', async () => {
    const { wrapper, fetchSpy } = mountImagePath(
      [jsonResponse(listing('', [imageEntry('big.png', 64 * 1024 * 1024)]))],
      'big.png'
    );
    await flushPromises();

    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(fetchSpy.mock.calls[0][0]).toBe('/api/conversations/thread-1/files');
    expect(wrapper.get('[data-testid="artifact-preview-unavailable"]').text()).toContain('too large');
    expect(wrapper.find('[data-testid="artifact-preview-download"]').exists()).toBe(true);
  });

  it.each([
    ['the listing is past its row cap', listing('img', [], 5)],
    ['the entry has no size', listing('img', [imageEntry('chart.png', null)])],
  ])('does not fetch an image whose size is unknown because %s', async (_, body) => {
    const { wrapper, fetchSpy } = mountImagePath([jsonResponse(body)], 'img/chart.png');
    await flushPromises();

    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(wrapper.get('[data-testid="artifact-preview-unavailable"]').text()).toContain('could not be checked');
    expect(wrapper.find('[data-testid="artifact-preview-download"]').exists()).toBe(true);
  });

  it('does not fetch a resolved link image whose size the server could not report', async () => {
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(jsonResponse({ path: 'a.png', type: 'symlink', size: null }));
    const wrapper = mount(ArtifactPreviewModal, {
      props: { threadId: 'thread-1', target: 'a.png' },
      attachTo: document.body,
    });
    await flushPromises();

    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(wrapper.get('[data-testid="artifact-preview-unavailable"]').text()).toContain('could not be checked');
  });

  it.each([
    ['missing from a complete listing', listing('img', [imageEntry('other.png', 4)]), 'not found in the workspace'],
    ['in a conversation with no session', { state: 'no_session_yet', workspaceId: null }, 'no workspace session'],
  ])('explains an artifact chip image %s, fetching no bytes', async (_, body, message) => {
    const { wrapper, fetchSpy } = mountImagePath([jsonResponse(body)], 'img/chart.png');
    await flushPromises();

    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(wrapper.get('[data-testid="artifact-preview-error"]').text()).toContain(message);
  });

  it('offers a download for a non-previewable file', async () => {
    const { wrapper } = await mountModal(jsonResponse(binaryPreview), 'bin/tool.zip');
    expect(wrapper.find('[data-testid="artifact-preview-download"]').exists()).toBe(true);
  });

  it('the download button saves the file through the download endpoint', async () => {
    const { wrapper, fetchSpy } = await mountModal(jsonResponse(textPreview), 'docs/spec.md');
    Object.assign(URL, { createObjectURL: vi.fn(() => 'blob:dl'), revokeObjectURL: vi.fn() });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    fetchSpy.mockResolvedValueOnce(new Response('bytes', { status: 200 }));

    await wrapper.get('[data-testid="artifact-preview-download"]').trigger('click');
    await flushPromises();

    expect(fetchSpy.mock.calls[1][0]).toBe(
      '/api/conversations/thread-1/files/download?path=docs%2Fspec.md'
    );
    expect(click).toHaveBeenCalledTimes(1);
  });

  it.each([
    ['a file over the download cap', 413, 'file_too_large', 'This file is too large to download.'],
    ['any other download failure', 502, 'gateway_error', 'Could not download the file.'],
  ])('explains %s inside the modal', async (_, status, code, message) => {
    const { wrapper, fetchSpy } = await mountModal(jsonResponse(binaryPreview), 'bin/tool.zip');
    fetchSpy.mockResolvedValueOnce(jsonResponse({ error: code, code }, status));

    await wrapper.get('[data-testid="artifact-preview-download"]').trigger('click');
    await flushPromises();

    expect(wrapper.get('[data-testid="artifact-preview-error"]').text()).toBe(message);
  });
});
