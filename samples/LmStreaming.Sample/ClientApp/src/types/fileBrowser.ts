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

/** One worksheet of a {@link TablePreview}: already capped by the producer. */
export interface SheetPreview {
  name: string;
  /** Row 0 is the header row. Rows are ragged — a short row simply has fewer cells. */
  rows: string[][];
  /** True when rows or columns were dropped to fit the caps. */
  truncated: boolean;
}

/**
 * A tabular preview. A spreadsheet has one entry per worksheet, in workbook order; a delimited text
 * file is parsed on the client into a single-sheet table of the same shape, so one viewer serves both.
 */
export interface TablePreview {
  sheets: SheetPreview[];
  /** True when whole sheets past the server's sheet cap were dropped. */
  truncated: boolean;
}

/** Result of the preview endpoint. `text` is present only when `previewable` is true. */
export interface PreviewResult {
  previewable: boolean;
  /**
   * Why a file is not previewable, e.g. `binary` | `too_large` | `not_utf8` | `not_a_file` |
   * `excluded` | `corrupt_spreadsheet`.
   */
  reason?: string;
  text?: string;
  lineCount?: number;
  /**
   * Present INSTEAD of `text` for a spreadsheet preview, which the server parses (the client never
   * sees the workbook bytes). Absent — the key is omitted, not null — for every text preview.
   */
  table?: TablePreview;
}

/** Result of `GET files/resolve?target=`: a chat file link mapped onto the workspace. */
export interface ResolvedWorkspaceLink {
  /** Workspace-relative path, `/`-separated; `''` is the workspace root. */
  path: string;
  type: 'file' | 'directory' | 'symlink';
  /** Byte size for files; `null` when unknown or not a file. */
  size: number | null;
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
