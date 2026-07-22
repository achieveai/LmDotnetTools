import { describe, it, expect, vi, afterEach } from 'vitest';
import {
  listFiles,
  previewFile,
  uploadFile,
  deleteEntry,
  downloadFile,
  createDirectory,
  NoSessionError,
  CredentialConflictError,
  FileBrowserError,
} from '@/api/fileBrowserApi';
import { isNoSession } from '@/types/fileBrowser';
import {
  sampleListing,
  noSessionState,
  textPreview,
  jsonResponse,
  noContentResponse,
} from '../fixtures/fileBrowser';

function spyFetch(response: Response) {
  return vi.spyOn(globalThis, 'fetch').mockResolvedValue(response);
}

describe('fileBrowserApi.listFiles', () => {
  afterEach(() => vi.restoreAllMocks());

  it('requests the files endpoint with no query at the root', async () => {
    const fetchSpy = spyFetch(jsonResponse(sampleListing));

    const result = await listFiles('thread-1', '');

    expect(fetchSpy).toHaveBeenCalledWith('/api/conversations/thread-1/files', {
      signal: undefined,
    });
    expect(isNoSession(result)).toBe(false);
  });

  it('url-encodes the path query for a nested directory', async () => {
    const fetchSpy = spyFetch(jsonResponse(sampleListing));

    await listFiles('thread-1', 'src/sub dir');

    expect(fetchSpy).toHaveBeenCalledWith(
      '/api/conversations/thread-1/files?path=src%2Fsub%20dir',
      { signal: undefined }
    );
  });

  it('returns a NoSessionState when the body carries a `state` discriminator', async () => {
    spyFetch(jsonResponse(noSessionState));

    const result = await listFiles('thread-1', '');

    expect(isNoSession(result)).toBe(true);
    expect((result as typeof noSessionState).state).toBe('no_session_yet');
  });

  it('throws CredentialConflictError on 409 caller_credential_conflict', async () => {
    spyFetch(jsonResponse({ code: 'caller_credential_conflict' }, 409));

    await expect(listFiles('thread-1', '')).rejects.toBeInstanceOf(CredentialConflictError);
  });

  it('throws FileBrowserError carrying code/status on 404 unknown_thread', async () => {
    spyFetch(jsonResponse({ error: 'nope', code: 'unknown_thread' }, 404));

    await expect(listFiles('thread-1', '')).rejects.toMatchObject({
      name: 'FileBrowserError',
      status: 404,
      code: 'unknown_thread',
    });
  });
});

describe('fileBrowserApi.previewFile', () => {
  afterEach(() => vi.restoreAllMocks());

  it('requests the preview endpoint and returns the result', async () => {
    const fetchSpy = spyFetch(jsonResponse(textPreview));

    const result = await previewFile('thread-1', 'readme.md');

    expect(fetchSpy).toHaveBeenCalledWith(
      '/api/conversations/thread-1/files/preview?path=readme.md',
      { signal: undefined }
    );
    expect(result).toEqual(textPreview);
  });

  it('throws NoSessionError on 409 no_session_yet', async () => {
    spyFetch(jsonResponse({ code: 'no_session_yet' }, 409));

    await expect(previewFile('thread-1', 'readme.md')).rejects.toBeInstanceOf(NoSessionError);
  });
});

describe('fileBrowserApi.uploadFile', () => {
  afterEach(() => vi.restoreAllMocks());

  it('POSTs a multipart form with the file in field "file"', async () => {
    const fetchSpy = spyFetch(jsonResponse({ name: 'a.txt', size: 3 }));
    const file = new File(['abc'], 'a.txt', { type: 'text/plain' });

    const outcome = await uploadFile('thread-1', 'sub', file);

    expect(outcome).toEqual({ name: 'a.txt', success: true });
    const [url, init] = fetchSpy.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/conversations/thread-1/files?path=sub');
    expect(init.method).toBe('POST');
    const body = init.body as FormData;
    expect(body.get('file')).toBe(file);
  });

  it('resolves to a failed outcome on 413 file_too_large (does not throw)', async () => {
    spyFetch(jsonResponse({ code: 'file_too_large' }, 413));
    const file = new File(['abc'], 'big.bin');

    const outcome = await uploadFile('thread-1', '', file);

    expect(outcome).toEqual({ name: 'big.bin', success: false, error: 'file_too_large' });
  });

  it('resolves to a failed outcome on 400 invalid_file_name (does not throw)', async () => {
    spyFetch(jsonResponse({ code: 'invalid_file_name' }, 400));
    const file = new File(['x'], '..');

    const outcome = await uploadFile('thread-1', '', file);

    expect(outcome).toEqual({ name: '..', success: false, error: 'invalid_file_name' });
  });

  it('throws NoSessionError on 409 no_session_yet (session-level, aborts the batch)', async () => {
    spyFetch(jsonResponse({ code: 'no_session_yet' }, 409));
    const file = new File(['x'], 'a.txt');

    await expect(uploadFile('thread-1', '', file)).rejects.toBeInstanceOf(NoSessionError);
  });

  it('appends the optional relativePath field and echoes the server name for a folder upload', async () => {
    const fetchSpy = spyFetch(jsonResponse({ name: 'proj/sub/a.txt', size: 3 }));
    const file = new File(['abc'], 'a.txt');

    const outcome = await uploadFile('thread-1', '', file, undefined, 'proj/sub/a.txt');

    expect(outcome).toEqual({ name: 'proj/sub/a.txt', success: true });
    const [, init] = fetchSpy.mock.calls[0] as [string, RequestInit];
    const body = init.body as FormData;
    expect(body.get('file')).toBe(file);
    expect(body.get('relativePath')).toBe('proj/sub/a.txt');
  });

  it('does NOT append relativePath for a flat upload (today behavior)', async () => {
    const fetchSpy = spyFetch(jsonResponse({ name: 'a.txt', size: 3 }));

    await uploadFile('thread-1', '', new File(['abc'], 'a.txt'));

    const [, init] = fetchSpy.mock.calls[0] as [string, RequestInit];
    expect((init.body as FormData).get('relativePath')).toBeNull();
  });

  it('labels a failed folder-upload outcome with the relativePath (distinguishes duplicate basenames)', async () => {
    spyFetch(jsonResponse({ code: 'mkdir_failed' }, 422));
    const file = new File(['x'], 'readme.md');

    const outcome = await uploadFile('thread-1', '', file, undefined, 'b/readme.md');

    expect(outcome).toEqual({ name: 'b/readme.md', success: false, error: 'mkdir_failed' });
  });
});

describe('fileBrowserApi.createDirectory', () => {
  afterEach(() => vi.restoreAllMocks());

  it('POSTs a JSON body { name } to the /directory endpoint under the parent path', async () => {
    const fetchSpy = spyFetch(jsonResponse({ path: 'docs/notes' }));

    const result = await createDirectory('thread-1', 'docs', 'notes');

    expect(result).toEqual({ path: 'docs/notes' });
    const [url, init] = fetchSpy.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/conversations/thread-1/files/directory?path=docs');
    expect(init.method).toBe('POST');
    expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/json');
    expect(JSON.parse(init.body as string)).toEqual({ name: 'notes' });
  });

  it('omits the query at the root and returns the resolved path', async () => {
    const fetchSpy = spyFetch(jsonResponse({ path: 'notes' }));

    const result = await createDirectory('thread-1', '', 'notes');

    expect(result).toEqual({ path: 'notes' });
    expect((fetchSpy.mock.calls[0] as [string, RequestInit])[0]).toBe(
      '/api/conversations/thread-1/files/directory'
    );
  });

  it('throws NoSessionError on 409 no_session_yet', async () => {
    spyFetch(jsonResponse({ code: 'no_session_yet' }, 409));

    await expect(createDirectory('thread-1', '', 'x')).rejects.toBeInstanceOf(NoSessionError);
  });

  it('throws FileBrowserError carrying code/status on 400 invalid_folder_name', async () => {
    spyFetch(jsonResponse({ error: 'bad', code: 'invalid_folder_name' }, 400));

    await expect(createDirectory('thread-1', '', '..')).rejects.toMatchObject({
      name: 'FileBrowserError',
      status: 400,
      code: 'invalid_folder_name',
    });
  });

  it('throws FileBrowserError on 422 create_directory_failed', async () => {
    spyFetch(jsonResponse({ code: 'create_directory_failed', exitCode: 1 }, 422));

    await expect(createDirectory('thread-1', '', 'x')).rejects.toMatchObject({
      name: 'FileBrowserError',
      status: 422,
      code: 'create_directory_failed',
    });
  });
});

describe('fileBrowserApi.deleteEntry', () => {
  afterEach(() => vi.restoreAllMocks());

  it('DELETEs the entry and resolves on 204', async () => {
    const fetchSpy = spyFetch(noContentResponse());

    await expect(deleteEntry('thread-1', 'src/old.txt')).resolves.toBeUndefined();

    expect(fetchSpy).toHaveBeenCalledWith('/api/conversations/thread-1/files?path=src%2Fold.txt', {
      method: 'DELETE',
      signal: undefined,
    });
  });

  it('throws NoSessionError on 409 no_session_yet', async () => {
    spyFetch(jsonResponse({ code: 'no_session_yet' }, 409));

    await expect(deleteEntry('thread-1', 'x')).rejects.toBeInstanceOf(NoSessionError);
  });

  it('throws FileBrowserError on 422 delete_failed', async () => {
    spyFetch(jsonResponse({ code: 'delete_failed' }, 422));

    await expect(deleteEntry('thread-1', 'x')).rejects.toMatchObject({
      name: 'FileBrowserError',
      status: 422,
      code: 'delete_failed',
    });
  });
});

describe('fileBrowserApi.downloadFile', () => {
  afterEach(() => vi.restoreAllMocks());

  it('fetches the download endpoint and triggers an object-URL anchor', async () => {
    const fetchSpy = spyFetch(new Response(new Blob(['data']), { status: 200 }));
    const createSpy = vi.fn(() => 'blob:mock');
    const revokeSpy = vi.fn();
    const origCreate = URL.createObjectURL;
    const origRevoke = URL.revokeObjectURL;
    URL.createObjectURL = createSpy as unknown as typeof URL.createObjectURL;
    URL.revokeObjectURL = revokeSpy as unknown as typeof URL.revokeObjectURL;
    try {
      await downloadFile('thread-1', 'dir/report.csv');

      expect(fetchSpy).toHaveBeenCalledWith(
        '/api/conversations/thread-1/files/download?path=dir%2Freport.csv',
        { signal: undefined }
      );
      expect(createSpy).toHaveBeenCalledTimes(1);
      // The revoke is deferred (so a larger blob's download isn't cancelled); it hasn't run yet.
      expect(revokeSpy).not.toHaveBeenCalled();
      // Flush the macrotask queue: the deferred cleanup then revokes the object URL.
      await new Promise((resolve) => setTimeout(resolve, 0));
      expect(revokeSpy).toHaveBeenCalledWith('blob:mock');
    } finally {
      URL.createObjectURL = origCreate;
      URL.revokeObjectURL = origRevoke;
    }
  });

  it('throws NoSessionError on 409 no_session_yet', async () => {
    spyFetch(jsonResponse({ code: 'no_session_yet' }, 409));

    await expect(downloadFile('thread-1', 'x')).rejects.toBeInstanceOf(NoSessionError);
  });
});

// Ensures the exported error class is a real Error subclass (instanceof works across the module).
describe('fileBrowserApi error classes', () => {
  it('FileBrowserError is an Error with code/status', () => {
    const e = new FileBrowserError('boom', 500, 'x');
    expect(e).toBeInstanceOf(Error);
    expect(e.status).toBe(500);
    expect(e.code).toBe('x');
  });
});

// Regression guard for the centralized session-error classification: EVERY action operation must map the
// two structured session codes to the same typed errors (previously preview/download/delete dropped
// caller_credential_conflict to a generic FileBrowserError).
describe('fileBrowserApi consistent session-error classification', () => {
  afterEach(() => vi.restoreAllMocks());

  const actions: Array<{ name: string; run: () => Promise<unknown> }> = [
    { name: 'previewFile', run: () => previewFile('t', 'x') },
    { name: 'downloadFile', run: () => downloadFile('t', 'x') },
    { name: 'deleteEntry', run: () => deleteEntry('t', 'x') },
  ];

  it.each(actions)('$name maps 409 no_session_yet → NoSessionError', async ({ run }) => {
    spyFetch(jsonResponse({ code: 'no_session_yet' }, 409));
    await expect(run()).rejects.toBeInstanceOf(NoSessionError);
  });

  it.each(actions)(
    '$name maps 409 caller_credential_conflict → CredentialConflictError',
    async ({ run }) => {
      spyFetch(jsonResponse({ code: 'caller_credential_conflict' }, 409));
      await expect(run()).rejects.toBeInstanceOf(CredentialConflictError);
    }
  );

  it('uploadFile maps both session codes consistently (session-level → throws)', async () => {
    spyFetch(jsonResponse({ code: 'no_session_yet' }, 409));
    await expect(uploadFile('t', '', new File(['x'], 'a.txt'))).rejects.toBeInstanceOf(NoSessionError);
    vi.restoreAllMocks();
    spyFetch(jsonResponse({ code: 'caller_credential_conflict' }, 409));
    await expect(uploadFile('t', '', new File(['x'], 'a.txt'))).rejects.toBeInstanceOf(
      CredentialConflictError
    );
  });

  it('re-throws an AbortError raised while reading the error body (not masked as a generic failure)', async () => {
    // A non-OK response whose body read is aborted mid-stream: classification must propagate the
    // cancellation, not turn it into a FileBrowserError.
    const aborted = {
      ok: false,
      status: 409,
      json: () => Promise.reject(new DOMException('Aborted', 'AbortError')),
    } as unknown as Response;
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(aborted);

    await expect(previewFile('t', 'x')).rejects.toHaveProperty('name', 'AbortError');
  });
});
