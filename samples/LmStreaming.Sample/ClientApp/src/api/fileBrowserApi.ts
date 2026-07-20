import type {
  DirectoryListing,
  NoSessionState,
  PreviewResult,
  UploadOutcome,
} from '@/types/fileBrowser';

/**
 * Raised when the conversation has no sandbox session yet and an ACTION (preview/download/upload/
 * delete) was attempted. The listing endpoint does NOT throw this — it returns a
 * {@link NoSessionState} so the browser can render an empty "no session" state.
 */
export class NoSessionError extends Error {
  constructor(message = 'The conversation has no sandbox session yet.') {
    super(message);
    this.name = 'NoSessionError';
  }
}

/** Raised on HTTP 409 `caller_credential_conflict` (another caller owns the session credentials). */
export class CredentialConflictError extends Error {
  constructor(message = 'The workspace is in use by another caller.') {
    super(message);
    this.name = 'CredentialConflictError';
  }
}

/** Generic file-browser failure carrying the server's structured `code` and the HTTP `status`. */
export class FileBrowserError extends Error {
  readonly code: string | null;
  readonly status: number;
  constructor(message: string, status: number, code: string | null = null) {
    super(message);
    this.name = 'FileBrowserError';
    this.code = code;
    this.status = status;
  }
}

/** Best-effort parse of a JSON error body; returns null when unreadable. Cancellation is re-thrown. */
async function readBody(response: Response): Promise<Record<string, unknown> | null> {
  try {
    return (await response.json()) as Record<string, unknown>;
  } catch (e) {
    // A read aborted mid-body is CANCELLATION, not a malformed body — propagate it so the caller can
    // treat it as cancellation rather than masking it as a generic failure.
    if (e instanceof DOMException && e.name === 'AbortError') {
      throw e;
    }
    return null;
  }
}

function codeOf(body: Record<string, unknown> | null): string | null {
  const code = body?.code;
  return typeof code === 'string' ? code : null;
}

/**
 * Maps a structured session-level failure to its typed error, consistently for EVERY operation:
 * 409 `no_session_yet` → {@link NoSessionError}, 409 `caller_credential_conflict` →
 * {@link CredentialConflictError}. Returns null when the response is not a session-level failure.
 */
function sessionError(status: number, code: string | null): Error | null {
  if (status === 409 && code === 'no_session_yet') {
    return new NoSessionError();
  }
  if (status === 409 && code === 'caller_credential_conflict') {
    return new CredentialConflictError();
  }
  return null;
}

/** Reads the error body and returns the typed session error, or a generic {@link FileBrowserError}. */
async function classifyFailure(response: Response, operation: string): Promise<Error> {
  const body = await readBody(response);
  const code = codeOf(body);
  return (
    sessionError(response.status, code) ??
    new FileBrowserError(`Failed to ${operation} (${response.status})`, response.status, code)
  );
}

/** Builds the `?path=` query; an empty path (root) is sent as no query for a clean URL. */
function filesUrl(threadId: string, path: string, suffix = ''): string {
  const base = `/api/conversations/${encodeURIComponent(threadId)}/files${suffix}`;
  return path ? `${base}?path=${encodeURIComponent(path)}` : base;
}

/**
 * Lists a directory. Returns either a {@link DirectoryListing} or, when the conversation has no
 * sandbox session yet, a {@link NoSessionState} (distinguished by its `state` field) — the caller
 * renders that as an empty state rather than an error.
 *
 * @throws {NoSessionError} on 409 no_session_yet.
 * @throws {CredentialConflictError} on 409 caller_credential_conflict.
 * @throws {FileBrowserError} on 404 unknown_thread / path / gateway errors.
 */
export async function listFiles(
  threadId: string,
  path: string,
  signal?: AbortSignal
): Promise<DirectoryListing | NoSessionState> {
  const response = await fetch(filesUrl(threadId, path), { signal });
  if (response.ok) {
    return (await response.json()) as DirectoryListing | NoSessionState;
  }
  throw await classifyFailure(response, 'list files');
}

/**
 * Fetches a text preview for a file.
 * @throws {NoSessionError} on 409 no_session_yet.
 * @throws {CredentialConflictError} on 409 caller_credential_conflict.
 * @throws {FileBrowserError} on other non-ok statuses.
 */
export async function previewFile(
  threadId: string,
  path: string,
  signal?: AbortSignal
): Promise<PreviewResult> {
  const response = await fetch(filesUrl(threadId, path, '/preview'), { signal });
  if (response.ok) {
    return (await response.json()) as PreviewResult;
  }
  throw await classifyFailure(response, 'preview file');
}

/**
 * Downloads a file, triggering a browser "save" via a temporary object-URL anchor.
 * @throws {NoSessionError} on 409 no_session_yet.
 * @throws {CredentialConflictError} on 409 caller_credential_conflict.
 * @throws {FileBrowserError} on other non-ok statuses.
 */
export async function downloadFile(threadId: string, path: string, signal?: AbortSignal): Promise<void> {
  const response = await fetch(filesUrl(threadId, path, '/download'), { signal });
  if (!response.ok) {
    throw await classifyFailure(response, 'download file');
  }
  const blob = await response.blob();
  triggerBrowserDownload(blob, fileNameFromPath(path));
}

/** The last path segment, used as the suggested download filename. */
function fileNameFromPath(path: string): string {
  const segments = path.split('/').filter(Boolean);
  return segments.length > 0 ? segments[segments.length - 1] : 'download';
}

/** Creates a transient object URL + `<a download>` click to save a blob, then revokes the URL. */
function triggerBrowserDownload(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  document.body.appendChild(anchor);
  anchor.click();
  // Defer anchor removal + URL revoke to a later tick: revoking synchronously right after click()
  // can cancel the in-progress download of larger blobs in some browsers.
  setTimeout(() => {
    anchor.remove();
    URL.revokeObjectURL(url);
  }, 0);
}

/**
 * Uploads ONE file into a directory. Per-file failures (413 too large, 400 invalid name, 409
 * target_busy) resolve to a failed {@link UploadOutcome} rather than throwing, so a batch upload can
 * continue past a single bad file. Session-level 409s throw so the whole batch aborts.
 *
 * @throws {NoSessionError} on 409 no_session_yet.
 * @throws {CredentialConflictError} on 409 caller_credential_conflict.
 */
export async function uploadFile(
  threadId: string,
  path: string,
  file: File,
  signal?: AbortSignal
): Promise<UploadOutcome> {
  const form = new FormData();
  form.append('file', file);
  const response = await fetch(filesUrl(threadId, path), {
    method: 'POST',
    body: form,
    signal,
  });
  if (response.ok) {
    const result = (await response.json()) as { name: string; size: number };
    return { name: result.name, success: true };
  }
  const body = await readBody(response);
  const code = codeOf(body);
  // Session-level failures abort the whole batch (thrown, mapped consistently with the other ops).
  const session = sessionError(response.status, code);
  if (session) {
    throw session;
  }
  // Per-file failure: surface as a failed outcome so a batch can continue.
  const error =
    response.status === 413
      ? 'file_too_large'
      : response.status === 400
        ? code ?? 'invalid_file_name'
        : code ?? `upload_failed_${response.status}`;
  return { name: file.name, success: false, error };
}

/**
 * Deletes an entry (file or directory — the server derives which; no flags are sent).
 * @throws {NoSessionError} on 409 no_session_yet.
 * @throws {CredentialConflictError} on 409 caller_credential_conflict.
 * @throws {FileBrowserError} on other non-204 statuses (422 delete_failed, 400 cannot_delete_root, 404).
 */
export async function deleteEntry(
  threadId: string,
  path: string,
  signal?: AbortSignal
): Promise<void> {
  const response = await fetch(filesUrl(threadId, path), { method: 'DELETE', signal });
  if (response.status === 204) {
    return;
  }
  throw await classifyFailure(response, 'delete entry');
}
