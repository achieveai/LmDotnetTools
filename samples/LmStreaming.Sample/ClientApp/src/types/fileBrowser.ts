/**
 * Types for the workspace File Browser served under
 * `GET/POST/DELETE /api/conversations/{threadId}/files`. Mirrors the backend's camelCase JSON
 * contract. The listing endpoint returns EITHER a {@link DirectoryListing} (a resolved sandbox
 * session with a directory to show) OR a {@link NoSessionState} (the conversation has no sandbox
 * session yet) — the two are distinguished by the presence of a `state` discriminator.
 */

/** A single entry (file/directory/symlink) within a directory listing. */
export interface FileEntry {
  name: string;
  type: 'file' | 'directory' | 'symlink';
  /** Byte size for files; `null` for directories/symlinks or when unknown. */
  size: number | null;
  /**
   * True when the entry name contains bytes that don't round-trip as UTF-8 (the server sends a
   * lossy replacement). Such entries can be listed but not acted upon (preview/download/delete/upload)
   * because the client cannot address them safely.
   */
  nameLossy: boolean;
}

/** A resolved directory listing for a sandbox session. */
export interface DirectoryListing {
  workspaceId: string;
  /** The directory this listing is for; `''` is the workspace root. */
  path: string;
  entries: FileEntry[];
  /** Count of entries beyond the server's row cap that were NOT returned (0 when fully listed). */
  moreCount: number;
}

/** Returned by the listing endpoint when the conversation has no sandbox session yet. */
export interface NoSessionState {
  state: 'no_session_yet';
  workspaceId: string | null;
}

/** Result of the preview endpoint. `text` is present only when `previewable` is true. */
export interface PreviewResult {
  previewable: boolean;
  /** Why a file is not previewable, e.g. `binary` | `too_large` | `not_utf8` | `not_a_file`. */
  reason?: string;
  text?: string;
  lineCount?: number;
}

/** Per-file outcome of an upload; a batch resolves to an array of these preserving mixed results. */
export interface UploadOutcome {
  name: string;
  success: boolean;
  /** Machine-readable failure code, e.g. `file_too_large` | `invalid_file_name` | `target_busy`. */
  error?: string;
}

/**
 * A file plus the workspace-relative path it should be written to. Produced by a folder / relative-path
 * upload (webkitdirectory picker or a directory drag-drop). `relativePath` INCLUDES the leaf filename,
 * e.g. `myfolder/sub/note.txt`.
 */
export interface UploadItem {
  file: File;
  relativePath: string;
}

/**
 * Sequential-upload progress: completed vs total files and the name of the file currently uploading
 * (`null` between files). Byte progress is intentionally NOT tracked — `fetch` exposes no upload byte
 * progress, so progress is per-file.
 */
export interface UploadProgress {
  completed: number;
  total: number;
  activeName: string | null;
}

/** Narrows a listing response to the no-session shape (vs a {@link DirectoryListing}). */
export function isNoSession(x: DirectoryListing | NoSessionState): x is NoSessionState {
  return 'state' in x;
}
