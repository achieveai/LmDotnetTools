import type {
  DirectoryListing,
  NoSessionState,
  PreviewResult,
  ResolvedWorkspaceLink,
  UploadOutcome,
} from '@/types/fileBrowser';
import { apiFetch } from '@/api/http';

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
  const response = await apiFetch(filesUrl(threadId, path), { signal });
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
  const response = await apiFetch(filesUrl(threadId, path, '/preview'), { signal });
  if (response.ok) {
    return (await response.json()) as PreviewResult;
  }
  throw await classifyFailure(response, 'preview file');
}

/**
 * Resolves a raw file link from a chat message (an absolute host path, a `file://` URI or a relative path)
 * to a workspace-relative path. Only the server knows the workspace's host path, so it does the mapping
 * and confirms the entry exists.
 * @throws {NoSessionError} on 409 no_session_yet.
 * @throws {CredentialConflictError} on 409 caller_credential_conflict.
 * @throws {FileBrowserError} on 400 `outside_workspace` / `invalid_path`, 404 `not_found`, and others.
 */
export async function resolveWorkspaceLink(
  threadId: string,
  target: string,
  signal?: AbortSignal
): Promise<ResolvedWorkspaceLink> {
  const url = `/api/conversations/${encodeURIComponent(threadId)}/files/resolve?target=${encodeURIComponent(target)}`;
  const response = await apiFetch(url, { signal });
  if (response.ok) {
    return (await response.json()) as ResolvedWorkspaceLink;
  }
  throw await classifyFailure(response, 'resolve link');
}

/**
 * Fetches a file's bytes (the download endpoint, 64 MiB cap) without saving them — for in-page viewers.
 * @throws {NoSessionError} on 409 no_session_yet.
 * @throws {CredentialConflictError} on 409 caller_credential_conflict.
 * @throws {FileBrowserError} on other non-ok statuses.
 */
export async function fetchFileBlob(threadId: string, path: string, signal?: AbortSignal): Promise<Blob> {
  const response = await apiFetch(filesUrl(threadId, path, '/download'), { signal });
  if (!response.ok) {
    throw await classifyFailure(response, 'download file');
  }
  return response.blob();
}

/**
 * Downloads a file, triggering a browser "save" via a temporary object-URL anchor.
 * @throws {NoSessionError} on 409 no_session_yet.
 * @throws {CredentialConflictError} on 409 caller_credential_conflict.
 * @throws {FileBrowserError} on other non-ok statuses.
 */
export async function downloadFile(threadId: string, path: string, signal?: AbortSignal): Promise<void> {
  const blob = await fetchFileBlob(threadId, path, signal);
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
  signal?: AbortSignal,
  relativePath?: string
): Promise<UploadOutcome> {
  const form = new FormData();
  form.append('file', file);
  // Folder / relative-path uploads carry the file's path (INCLUDING the leaf name); the server writes
  // it relative to `path` and echoes it back as `name`. Flat uploads omit it (today's behavior).
  if (relativePath) {
    form.append('relativePath', relativePath);
  }
  // The outcome label: for a folder upload use the relativePath so duplicate basenames in different
  // directories (e.g. `a/readme.md` vs `b/readme.md`) stay distinguishable in per-file reporting.
  const label = relativePath ?? file.name;
  const response = await apiFetch(filesUrl(threadId, path), {
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
  return { name: label, success: false, error };
}

/**
 * Creates a directory named `name` inside `parentPath`.
 * @returns The resolved workspace-relative path of the new directory (`<resolvedParent>/<name>`).
 * @throws {NoSessionError} on 409 no_session_yet.
 * @throws {CredentialConflictError} on 409 caller_credential_conflict.
 * @throws {FileBrowserError} on other non-ok statuses (400 invalid_folder_name/not_a_directory/
 *   ambiguous_path/invalid_path, 404 not_found, 409 target_busy, 422 create_directory_failed,
 *   502 gateway_error) — each carrying the server's structured `code`.
 */
export async function createDirectory(
  threadId: string,
  parentPath: string,
  name: string,
  signal?: AbortSignal
): Promise<{ path: string }> {
  const response = await apiFetch(filesUrl(threadId, parentPath, '/directory'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name }),
    signal,
  });
  if (response.ok) {
    return (await response.json()) as { path: string };
  }
  throw await classifyFailure(response, 'create directory');
}

/**
 * Deletes an entry (file or directory — the server derives which; no flags are sent).
 * @throws {NoSessionError} on 409 no_session_yet.
 * @throws {CredentialConflictError} on 409 caller_credential_conflict.
 * @throws {FileBrowserError} on other non-204 statuses (409 entry_changed when the entry was concurrently
 *   replaced by one of a different kind, 422 delete_failed, 400 cannot_delete_root, 404).
 */
export async function deleteEntry(
  threadId: string,
  path: string,
  signal?: AbortSignal
): Promise<void> {
  const response = await apiFetch(filesUrl(threadId, path), { method: 'DELETE', signal });
  if (response.status === 204) {
    return;
  }
  throw await classifyFailure(response, 'delete entry');
}

// ---------------------------------------------------------------------------------------------
// Path-addressed raw workspace access (Bug#15)
//
// `preview`/`download` above are QUERY-addressed (`?path=`) and go through `apiFetch`, which is
// the only place the bearer token is attached. Neither property survives contact with a rendered
// HTML document: an `<iframe src>` / `<img src>` / relative `<link href>` cannot send a header,
// and a relative reference inside a document served at `…/files/download?path=a/b.html` resolves
// against `…/files/` — dropping the query and naming a route that does not exist.
//
// So a second shape exists beside them (the originals are untouched, and still used as the
// fallback whenever a grant cannot be minted): a PATH-addressed URL carrying a short-lived,
// signed, read-only grant in a path segment, where relative resolution keeps it.
// ---------------------------------------------------------------------------------------------

/** The server's `POST …/files/grant` body. Local to this module: nothing else has a use for it. */
interface WorkspaceGrantResponse {
  grant: string;
  /** ISO-8601 instant at which the grant stops validating. */
  expiresAt: string;
}

/** A grant held for one thread, with the instant this client stops presenting it. */
interface CachedGrant {
  token: string;
  /** Local-clock ms after which a fresh grant is minted — already inside the server's expiry. */
  refreshAfter: number;
}

/**
 * How far ahead of the server's expiry a cached grant is replaced. Mirrors
 * `FileBrowserLimits.WorkspaceGrantRefreshMargin`. Sized for the PAGE, not the round trip: an
 * iframe that is already open keeps fetching subresources with the grant it was handed, so the
 * margin has to cover someone reading a rendered report for a few minutes.
 */
const GRANT_REFRESH_MARGIN_MS = 5 * 60 * 1000;

const grantCache = new Map<string, CachedGrant>();
/**
 * Mint requests currently in flight, keyed by thread. A preview typically asks for the URL of the
 * document and several subresources in the same tick; without this each one would mint its own
 * grant, so opening one page would issue a handful of tokens that all outlive it.
 */
const grantInFlight = new Map<string, Promise<string>>();

/**
 * Drops any cached grant for a thread. Called on a 401 from a raw URL — the one answer that covers
 * every way a grant can stop working (expired, key rotated, signed in as someone else) — so the
 * next request mints a fresh one instead of replaying a dead token.
 */
export function clearWorkspaceGrant(threadId: string): void {
  grantCache.delete(threadId);
  grantInFlight.delete(threadId);
}

/** Test seam: forget every cached grant. */
export function clearAllWorkspaceGrants(): void {
  grantCache.clear();
  grantInFlight.clear();
}

/**
 * Obtains a read grant for a thread's workspace, reusing the cached one until it is within
 * {@link GRANT_REFRESH_MARGIN_MS} of expiring.
 *
 * The mint itself goes through {@link apiFetch}, so it is bearer-authenticated and authorized
 * exactly like every other file-browser call — the grant is only a way to carry that already-made
 * decision onto requests that cannot send a header.
 *
 * @throws {NoSessionError} on 409 no_session_yet.
 * @throws {CredentialConflictError} on 409 caller_credential_conflict.
 * @throws {FileBrowserError} on other non-ok statuses.
 */
export function requestWorkspaceGrant(threadId: string, signal?: AbortSignal): Promise<string> {
  const cached = grantCache.get(threadId);
  if (cached && Date.now() < cached.refreshAfter) {
    return Promise.resolve(cached.token);
  }

  const pending = grantInFlight.get(threadId);
  if (pending) {
    return pending;
  }

  const attempt = mintWorkspaceGrant(threadId, signal)
    .then((minted) => {
      grantCache.set(threadId, minted);
      return minted.token;
    })
    .finally(() => {
      grantInFlight.delete(threadId);
    });

  grantInFlight.set(threadId, attempt);
  return attempt;
}

/** One trip to `POST …/files/grant`, translated into a {@link CachedGrant}. */
async function mintWorkspaceGrant(threadId: string, signal?: AbortSignal): Promise<CachedGrant> {
  const url = `/api/conversations/${encodeURIComponent(threadId)}/files/grant`;
  const response = await apiFetch(url, { method: 'POST', signal });
  if (!response.ok) {
    throw await classifyFailure(response, 'obtain a workspace grant');
  }
  const body = (await response.json()) as WorkspaceGrantResponse;
  const expiresAtMs = Date.parse(body.expiresAt);
  // An unparseable or already-past expiry must not produce a grant this client caches forever, nor
  // one it refuses to use at all. Falling back to "now + the margin" means it is used once and
  // re-minted next time, which is the safe reading of a server answer we do not understand.
  const refreshAfter = Number.isFinite(expiresAtMs)
    ? expiresAtMs - GRANT_REFRESH_MARGIN_MS
    : Date.now() + GRANT_REFRESH_MARGIN_MS;
  return { token: body.grant, refreshAfter };
}

/**
 * The raw, path-addressed URL for one workspace file:
 * `/api/conversations/{threadId}/workspace/{grant}/{path}`.
 *
 * Every path segment is encoded INDIVIDUALLY so that `/` keeps its meaning as a separator while a
 * `#`, `?` or space inside a file name cannot truncate or re-parse the URL. That shape is the whole
 * point: a document served from it has base URL `…/workspace/{grant}/<its dir>/`, so its own
 * `img/x.png`, `./style.css` and `../shared/app.js` resolve to the sibling workspace files.
 *
 * `download: true` adds `?download=1`, which flips the server's `Content-Disposition` to
 * `attachment`. It is a QUERY on purpose: a relative link inside a served document drops the query,
 * so no subresource a page loads can be turned into a download.
 */
export function workspaceFileUrl(
  threadId: string,
  grant: string,
  path: string,
  options?: { download?: boolean }
): string {
  const segments = path
    .split('/')
    .filter(Boolean)
    .map((segment) => encodeURIComponent(segment))
    .join('/');
  const base = `/api/conversations/${encodeURIComponent(threadId)}/workspace/${encodeURIComponent(grant)}/${segments}`;
  return options?.download ? `${base}?download=1` : base;
}
