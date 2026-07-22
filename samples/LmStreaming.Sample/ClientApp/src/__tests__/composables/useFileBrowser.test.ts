import { describe, it, expect, vi, afterEach } from 'vitest';
import { flushPromises } from '@vue/test-utils';
import { useFileBrowser } from '@/composables/useFileBrowser';
import type { DirectoryListing } from '@/types/fileBrowser';
import { sampleListing, noSessionState, jsonResponse, noContentResponse } from '../fixtures/fileBrowser';

afterEach(() => vi.restoreAllMocks());

describe('useFileBrowser.load', () => {
  it('populates entries, path, and breadcrumbs from a listing', async () => {
    const nested: DirectoryListing = { ...sampleListing, path: 'src/sub', moreCount: 3 };
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse(nested));

    const fb = useFileBrowser(() => 'thread-1');
    await fb.load('src/sub');

    expect(fb.entries.value).toHaveLength(sampleListing.entries.length);
    expect(fb.currentPath.value).toBe('src/sub');
    expect(fb.moreCount.value).toBe(3);
    expect(fb.breadcrumbs.value.map((c) => c.name)).toEqual(['root', 'src', 'sub']);
    expect(fb.breadcrumbs.value.map((c) => c.path)).toEqual(['', 'src', 'src/sub']);
    expect(fb.isLoading.value).toBe(false);
    expect(fb.noSession.value).toBe(false);
  });

  it('sets noSession (and does not populate entries) on a no-session response', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse(noSessionState));

    const fb = useFileBrowser(() => 'thread-1');
    await fb.load('');

    expect(fb.noSession.value).toBe(true);
    expect(fb.entries.value).toEqual([]);
  });

  it('does not fetch when there is no active thread', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse(sampleListing));

    const fb = useFileBrowser(() => null);
    await fb.load('');

    expect(fetchSpy).not.toHaveBeenCalled();
    expect(fb.entries.value).toEqual([]);
  });

  it('aborts a prior in-flight load and resets state when the thread becomes null', async () => {
    const signals: AbortSignal[] = [];
    const pending: { resolve: (r: Response) => void; reject: (e: unknown) => void }[] = [];
    vi.spyOn(globalThis, 'fetch').mockImplementation((_url, init) => {
      signals.push((init as RequestInit).signal as AbortSignal);
      return new Promise((resolve, reject) => pending.push({ resolve, reject }));
    });

    let threadId: string | null = 'thread-1';
    const fb = useFileBrowser(() => threadId);
    // Seed state as if a prior thread had been listed, so we can assert it gets cleared.
    fb.entries.value = [{ name: 'x', type: 'file', size: 1, nameLossy: false }];
    fb.moreCount.value = 4;
    fb.workspaceId.value = 'ws-old';
    fb.previewTarget.value = { name: 'x', type: 'file', size: 1, nameLossy: false };
    fb.noSession.value = true;
    fb.error.value = 'stale error';

    const first = fb.load('a'); // in-flight fetch while a thread is present
    expect(signals[0].aborted).toBe(false);
    expect(fb.isLoading.value).toBe(true);

    // The conversation goes away: a load with a null thread must abort the in-flight request and reset.
    threadId = null;
    await fb.load('');

    expect(signals[0].aborted).toBe(true);
    expect(fb.entries.value).toEqual([]);
    expect(fb.moreCount.value).toBe(0);
    expect(fb.workspaceId.value).toBeNull();
    expect(fb.previewTarget.value).toBeNull();
    expect(fb.previewResult.value).toBeNull();
    expect(fb.noSession.value).toBe(false);
    expect(fb.error.value).toBeNull();
    expect(fb.isLoading.value).toBe(false);

    // If the aborted listing rejects late (as a real aborted fetch does), it must not repopulate.
    pending[0].reject(new DOMException('Aborted', 'AbortError'));
    await Promise.allSettled([first]);
    await flushPromises();

    expect(fb.entries.value).toEqual([]);
  });

  it('aborts a superseded in-flight load (identity-gated result)', async () => {
    const signals: AbortSignal[] = [];
    const pending: { resolve: (r: Response) => void; reject: (e: unknown) => void }[] = [];
    vi.spyOn(globalThis, 'fetch').mockImplementation((_url, init) => {
      signals.push((init as RequestInit).signal as AbortSignal);
      return new Promise((resolve, reject) => pending.push({ resolve, reject }));
    });

    const fb = useFileBrowser(() => 'thread-1');
    const first = fb.load('a');
    const second = fb.load('b'); // must abort the first before issuing its own fetch

    expect(signals[0].aborted).toBe(true);
    expect(signals[1].aborted).toBe(false);

    // The winning (second) request resolves; the aborted first rejects like a real aborted fetch.
    pending[1].resolve(jsonResponse({ ...sampleListing, path: 'b' }));
    pending[0].reject(new DOMException('Aborted', 'AbortError'));
    await Promise.allSettled([first, second]);
    await flushPromises();

    expect(fb.currentPath.value).toBe('b');
    expect(fb.error.value).toBeNull(); // the aborted request must not surface as an error
    expect(fb.isLoading.value).toBe(false);
  });
});

describe('useFileBrowser.upload', () => {
  it('issues one request per file and preserves mixed success/failure, then reloads', async () => {
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(jsonResponse({ name: 'ok.txt', size: 3 })) // file 1 upload
      .mockResolvedValueOnce(jsonResponse({ code: 'file_too_large' }, 413)) // file 2 upload
      .mockResolvedValueOnce(jsonResponse(sampleListing)); // reload listing

    const fb = useFileBrowser(() => 'thread-1');
    const outcomes = await fb.upload([
      new File(['abc'], 'ok.txt'),
      new File(['x'.repeat(10)], 'big.bin'),
    ]);

    expect(outcomes).toEqual([
      { name: 'ok.txt', success: true },
      { name: 'big.bin', success: false, error: 'file_too_large' },
    ]);
    // 2 uploads + 1 reload.
    expect(fetchSpy).toHaveBeenCalledTimes(3);
    expect(fb.entries.value).toHaveLength(sampleListing.entries.length);
  });

  it('sends every file to the directory the batch STARTED in, even if the user navigates mid-batch', async () => {
    const fb = useFileBrowser(() => 'thread-1');
    fb.currentPath.value = 'start';
    const urls: string[] = [];
    vi.spyOn(globalThis, 'fetch').mockImplementation((url, init) => {
      urls.push(url as string);
      if ((init as RequestInit)?.method === 'POST') {
        // Simulate the user navigating away while the batch is uploading.
        fb.currentPath.value = 'elsewhere';
        return Promise.resolve(jsonResponse({ name: 'f', size: 1 }));
      }
      return Promise.resolve(jsonResponse(sampleListing));
    });

    await fb.upload([new File(['a'], 'a.txt'), new File(['b'], 'b.txt')]);

    const uploadUrls = urls.filter((u) => u.includes('?path='));
    expect(uploadUrls).toHaveLength(2);
    // Both files targeted the STARTING directory; navigation mid-batch never re-targeted them.
    expect(uploadUrls.every((u) => u.includes('path=start'))).toBe(true);
    expect(urls.some((u) => u.includes('elsewhere'))).toBe(false);
  });

  it('cleanup aborts an in-flight upload and swallows the cancellation (no error committed)', async () => {
    let capturedSignal: AbortSignal | undefined;
    const pending: { resolve: (r: Response) => void; reject: (e: unknown) => void }[] = [];
    vi.spyOn(globalThis, 'fetch').mockImplementation((_url, init) => {
      capturedSignal = (init as RequestInit)?.signal ?? undefined;
      return new Promise((resolve, reject) => pending.push({ resolve, reject }));
    });

    const fb = useFileBrowser(() => 'thread-1');
    const p = fb.upload([new File(['a'], 'a.txt')]);
    expect(capturedSignal?.aborted).toBe(false);

    fb.cleanup(); // unmount aborts the mutation's lifecycle signal
    expect(capturedSignal?.aborted).toBe(true);

    // A real aborted fetch rejects; the composable must swallow it (cancellation, not a failure).
    pending[0].reject(new DOMException('Aborted', 'AbortError'));
    await p;
    await flushPromises();
    expect(fb.error.value).toBeNull();
  });
});

describe('useFileBrowser upload single-flight guard (F5)', () => {
  it('serializes concurrent batches (one POST in flight at a time) without clobbering progress', async () => {
    const pending: Array<{ resolve: (r: Response) => void }> = [];
    vi.spyOn(globalThis, 'fetch').mockImplementation((_url, init) => {
      if ((init as RequestInit)?.method === 'POST') {
        return new Promise((resolve) => pending.push({ resolve }));
      }
      return Promise.resolve(jsonResponse(sampleListing)); // reload
    });

    const fb = useFileBrowser(() => 'thread-1');
    const p1 = fb.upload([new File(['a'], 'a.txt')]);
    const p2 = fb.upload([new File(['b'], 'b.txt')]); // second batch started while the first is active

    // The guard is active for the whole duration and only ONE upload is in flight (second is queued).
    expect(fb.isUploading.value).toBe(true);
    expect(pending).toHaveLength(1);
    expect(fb.uploadProgress.value?.activeName).toBe('a.txt');

    // Complete batch 1 (upload + its reload); only then does batch 2 issue its POST.
    pending[0].resolve(jsonResponse({ name: 'a.txt', size: 1 }));
    await flushPromises();
    expect(pending).toHaveLength(2);
    expect(fb.uploadProgress.value?.activeName).toBe('b.txt');

    pending[1].resolve(jsonResponse({ name: 'b.txt', size: 1 }));
    await flushPromises();

    const [o1, o2] = await Promise.all([p1, p2]);
    expect(o1).toEqual([{ name: 'a.txt', success: true }]);
    expect(o2).toEqual([{ name: 'b.txt', success: true }]);
    // The guard clears and progress is null once BOTH batches settle.
    expect(fb.isUploading.value).toBe(false);
    expect(fb.uploadProgress.value).toBeNull();
  });
});

describe('useFileBrowser upload outcomes on mid-batch throw (F7)', () => {
  it('preserves already-completed outcomes; marks only the active + remaining files failed', async () => {
    let postCount = 0;
    vi.spyOn(globalThis, 'fetch').mockImplementation((_url, init) => {
      if ((init as RequestInit)?.method === 'POST') {
        postCount += 1;
        // The SECOND file hits a session-level 409 → uploadFile THROWS, aborting the batch mid-way.
        if (postCount === 2) {
          return Promise.resolve(jsonResponse({ code: 'no_session_yet' }, 409));
        }
        return Promise.resolve(jsonResponse({ name: `ok${postCount}`, size: 1 }));
      }
      // The post-batch reload also reports the session is gone, so `noSession` stays set.
      return Promise.resolve(jsonResponse(noSessionState)); // reload
    });

    const fb = useFileBrowser(() => 'thread-1');
    const outcomes = await fb.upload([
      new File(['a'], 'a.txt'),
      new File(['b'], 'b.txt'),
      new File(['c'], 'c.txt'),
    ]);

    expect(outcomes).toHaveLength(3);
    // File #1 already uploaded before the throw → its success is PRESERVED (not reported as failed).
    expect(outcomes[0].success).toBe(true);
    // File #2 (the one that threw) and file #3 (never attempted) are both failed — not all three.
    expect(outcomes[1]).toEqual({ name: 'b.txt', success: false, error: expect.any(String) });
    expect(outcomes[2]).toEqual({ name: 'c.txt', success: false, error: expect.any(String) });
    expect(fb.noSession.value).toBe(true);
  });
});

describe('useFileBrowser.uploadFolder', () => {
  it('sends each file sequentially with its relativePath and aggregates mixed outcomes without aborting', async () => {
    const bodies: FormData[] = [];
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockImplementation((_url, init) => {
      const method = (init as RequestInit)?.method;
      if (method === 'POST') {
        const body = (init as RequestInit).body as FormData;
        bodies.push(body);
        // Fail the second file, succeed the rest — a single failure must NOT abort the batch.
        if (body.get('relativePath') === 'proj/b/readme.md') {
          return Promise.resolve(jsonResponse({ code: 'mkdir_failed' }, 422));
        }
        return Promise.resolve(jsonResponse({ name: body.get('relativePath'), size: 1 }));
      }
      return Promise.resolve(jsonResponse(sampleListing)); // reload
    });

    const fb = useFileBrowser(() => 'thread-1');
    const outcomes = await fb.uploadFolder([
      { file: new File(['1'], 'readme.md'), relativePath: 'proj/a/readme.md' },
      { file: new File(['2'], 'readme.md'), relativePath: 'proj/b/readme.md' },
      { file: new File(['3'], 'note.txt'), relativePath: 'proj/note.txt' },
    ]);

    // Duplicate basenames in different directories BOTH uploaded under distinct relativePaths.
    expect(bodies.map((b) => b.get('relativePath'))).toEqual([
      'proj/a/readme.md',
      'proj/b/readme.md',
      'proj/note.txt',
    ]);
    expect(outcomes).toEqual([
      { name: 'proj/a/readme.md', success: true },
      { name: 'proj/b/readme.md', success: false, error: 'mkdir_failed' },
      { name: 'proj/note.txt', success: true },
    ]);
    // 3 uploads + 1 reload.
    expect(fetchSpy).toHaveBeenCalledTimes(4);
    // Progress is cleared once the batch settles.
    expect(fb.uploadProgress.value).toBeNull();
  });
});

describe('useFileBrowser.createDirectory', () => {
  it('POSTs the folder name to the /directory endpoint under the current path, then reloads', async () => {
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(jsonResponse({ path: 'src/docs' })) // create
      .mockResolvedValueOnce(jsonResponse(sampleListing)); // reload

    const fb = useFileBrowser(() => 'thread-1');
    fb.currentPath.value = 'src';
    const ok = await fb.createDirectory('docs');

    expect(ok).toBe(true);
    const [url, init] = fetchSpy.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/conversations/thread-1/files/directory?path=src');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual({ name: 'docs' });
    // A GET reload followed so the new folder shows.
    const [reloadUrl, reloadInit] = fetchSpy.mock.calls[1] as [string, RequestInit];
    expect(reloadInit.method).toBeUndefined();
    expect(reloadUrl).toContain('/api/conversations/thread-1/files');
    expect(fb.error.value).toBeNull();
  });

  it('maps a 400 invalid_folder_name to a user-facing error and returns false', async () => {
    vi.spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(jsonResponse({ code: 'invalid_folder_name' }, 400)) // create
      .mockResolvedValueOnce(jsonResponse(sampleListing)); // reload

    const fb = useFileBrowser(() => 'thread-1');
    const ok = await fb.createDirectory('..');

    expect(ok).toBe(false);
    expect(fb.error.value).toBeTruthy();
    expect(fb.noSession.value).toBe(false);
  });

  it('sets noSession on 409 no_session_yet', async () => {
    vi.spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(jsonResponse({ code: 'no_session_yet' }, 409)) // create
      .mockResolvedValueOnce(jsonResponse(sampleListing)); // reload

    const fb = useFileBrowser(() => 'thread-1');
    const ok = await fb.createDirectory('docs');

    expect(ok).toBe(false);
    expect(fb.noSession.value).toBe(true);
  });
});

describe('useFileBrowser.remove', () => {
  it('deletes the entry then reloads the listing', async () => {
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(noContentResponse()) // DELETE
      .mockResolvedValueOnce(jsonResponse(sampleListing)); // reload

    const fb = useFileBrowser(() => 'thread-1');
    fb.currentPath.value = 'src';
    await fb.remove({ name: 'old.txt', type: 'file', size: 1, nameLossy: false });

    const [delUrl, delInit] = fetchSpy.mock.calls[0] as [string, RequestInit];
    expect(delInit.method).toBe('DELETE');
    expect(delUrl).toBe('/api/conversations/thread-1/files?path=src%2Fold.txt');
    // A GET reload followed.
    const [reloadUrl, reloadInit] = fetchSpy.mock.calls[1] as [string, RequestInit];
    expect(reloadInit.method).toBeUndefined();
    expect(reloadUrl).toContain('/api/conversations/thread-1/files');
  });

  it('does NOT reload after a mutation if the user navigated to a different directory meanwhile', async () => {
    const calls: Array<{ url: string; method?: string }> = [];
    const fb = useFileBrowser(() => 'thread-1');
    fb.currentPath.value = 'src';
    vi.spyOn(globalThis, 'fetch').mockImplementation((url, init) => {
      const method = (init as RequestInit)?.method;
      calls.push({ url: url as string, method });
      if (method === 'DELETE') {
        fb.currentPath.value = 'docs'; // user navigated away while the delete was in flight
        return Promise.resolve(noContentResponse());
      }
      return Promise.resolve(jsonResponse(sampleListing));
    });

    await fb.remove({ name: 'old.txt', type: 'file', size: 1, nameLossy: false });

    // The DELETE targeted the START path; NO reload GET was issued (it would have aborted the newer nav).
    expect(calls.filter((c) => c.method === 'DELETE')).toHaveLength(1);
    expect(calls.filter((c) => c.method === undefined)).toHaveLength(0);
    expect(calls[0].url).toBe('/api/conversations/thread-1/files?path=src%2Fold.txt');
  });
});

describe('useFileBrowser.preview', () => {
  it('a stale earlier preview that resolves AFTER a newer one does not overwrite the newer selection', async () => {
    const pending: { resolve: (r: Response) => void }[] = [];
    vi.spyOn(globalThis, 'fetch').mockImplementation(
      () => new Promise((resolve) => pending.push({ resolve }))
    );

    const fb = useFileBrowser(() => 'thread-1');
    const entryA = { name: 'a.txt', type: 'file' as const, size: 1, nameLossy: false };
    const entryB = { name: 'b.txt', type: 'file' as const, size: 1, nameLossy: false };

    const pA = fb.preview(entryA); // older request
    const pB = fb.preview(entryB); // newer request — supersedes A

    // Resolve the NEWER request first, then let the OLDER one resolve late.
    pending[1].resolve(jsonResponse({ previewable: true, text: 'B content' }));
    pending[0].resolve(jsonResponse({ previewable: true, text: 'A content' }));
    await Promise.allSettled([pA, pB]);
    await flushPromises();

    // The newer selection (B) wins; the late A is dropped by the supersession guard.
    expect(fb.previewTarget.value?.name).toBe('b.txt');
    expect(fb.previewResult.value?.text).toBe('B content');
  });

  it('a preview that resolves after clearPreview() does not repopulate the panel', async () => {
    const pending: { resolve: (r: Response) => void }[] = [];
    vi.spyOn(globalThis, 'fetch').mockImplementation(
      () => new Promise((resolve) => pending.push({ resolve }))
    );

    const fb = useFileBrowser(() => 'thread-1');
    const p = fb.preview({ name: 'a.txt', type: 'file', size: 1, nameLossy: false });
    fb.clearPreview(); // user closed/navigated before the preview returned

    pending[0].resolve(jsonResponse({ previewable: true, text: 'A content' }));
    await Promise.allSettled([p]);
    await flushPromises();

    expect(fb.previewTarget.value).toBeNull();
    expect(fb.previewResult.value).toBeNull();
  });

  it('aborts the superseded preview request so obsolete server work is cancelled', async () => {
    const signals: AbortSignal[] = [];
    const pending: { resolve: (r: Response) => void; reject: (e: unknown) => void }[] = [];
    vi.spyOn(globalThis, 'fetch').mockImplementation((_url, init) => {
      signals.push((init as RequestInit).signal as AbortSignal);
      return new Promise((resolve, reject) => pending.push({ resolve, reject }));
    });

    const fb = useFileBrowser(() => 'thread-1');
    const pA = fb.preview({ name: 'a.txt', type: 'file', size: 1, nameLossy: false });
    const pB = fb.preview({ name: 'b.txt', type: 'file', size: 1, nameLossy: false });

    // Starting B aborts A's in-flight request (not just ignores its result on arrival).
    expect(signals[0].aborted).toBe(true);
    expect(signals[1].aborted).toBe(false);

    pending[1].resolve(jsonResponse({ previewable: true, text: 'B content' }));
    pending[0].reject(new DOMException('Aborted', 'AbortError'));
    await Promise.allSettled([pA, pB]);
    await flushPromises();
    expect(fb.previewTarget.value?.name).toBe('b.txt');
  });

  it('a stale ERROR from a superseded preview does not surface beside the newer preview', async () => {
    const pending: { resolve: (r: Response) => void; reject: (e: unknown) => void }[] = [];
    vi.spyOn(globalThis, 'fetch').mockImplementation(
      () => new Promise((resolve, reject) => pending.push({ resolve, reject }))
    );

    const fb = useFileBrowser(() => 'thread-1');
    const pA = fb.preview({ name: 'a.txt', type: 'file', size: 1, nameLossy: false });
    const pB = fb.preview({ name: 'b.txt', type: 'file', size: 1, nameLossy: false });

    // B succeeds; then the superseded A rejects with a NORMAL (non-abort) error. The seq guard must
    // swallow it so an obsolete error never appears next to the newer preview. (Would fail pre-guard.)
    pending[1].resolve(jsonResponse({ previewable: true, text: 'B content' }));
    pending[0].reject(new Error('gateway exploded'));
    await Promise.allSettled([pA, pB]);
    await flushPromises();

    expect(fb.error.value).toBeNull();
    expect(fb.previewTarget.value?.name).toBe('b.txt');
  });
});
