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
});
