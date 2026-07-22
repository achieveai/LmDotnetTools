/**
 * Client-side folder / relative-path upload helpers for the workspace File Browser.
 *
 * There is NO folder-upload endpoint: a folder upload is flattened here into N independent single-file
 * uploads, each carrying its `relativePath` (see {@link UploadItem}). Two entry points feed this module:
 *   - the `webkitdirectory` file picker → {@link filesFromDirectoryInput} (uses `File.webkitRelativePath`);
 *   - a directory drag-drop → {@link resolveDrop} (traverses the File System entries).
 *
 * The File System entry types are duck-typed ({@link FileSystemEntryLike} etc.) so the traversal is unit
 * testable without the real, browser-only File System Access API.
 */
import type { UploadItem } from '@/types/fileBrowser';

/** Minimal shape of a `FileSystemEntry` (file or directory) used by the traversal. */
export interface FileSystemEntryLike {
  isFile: boolean;
  isDirectory: boolean;
  name: string;
  /** Present on file entries: resolves the underlying `File` via a callback. */
  file?(success: (file: File) => void, error?: (err: unknown) => void): void;
  /** Present on directory entries: creates a paging reader over the directory's children. */
  createReader?(): DirectoryReaderLike;
}

/** Minimal shape of a `FileSystemDirectoryReader`; `readEntries` PAGES results until an empty batch. */
export interface DirectoryReaderLike {
  readEntries(success: (entries: FileSystemEntryLike[]) => void, error?: (err: unknown) => void): void;
}

/** Minimal shape of a `DataTransferItem` exposing the non-standard `webkitGetAsEntry`. */
export interface DataTransferItemLike {
  kind: string;
  webkitGetAsEntry?(): FileSystemEntryLike | null;
}

/** Minimal shape of a `DataTransfer` for {@link resolveDrop}. */
export interface DataTransferLike {
  items?: ArrayLike<DataTransferItemLike> | null;
  files?: ArrayLike<File> | null;
}

/** Upper bound on files enumerated by a single folder upload, so a huge/looping tree stays bounded. */
export const MAX_FOLDER_UPLOAD_FILES = 2000;

/**
 * Signals a folder selection that exceeds {@link MAX_FOLDER_UPLOAD_FILES}. Both entry points (drop and
 * `webkitdirectory` picker) share this so the whole selection is REJECTED (no partial upload) rather
 * than silently truncated to the collected subset.
 */
export interface OverLimitResult {
  kind: 'over-limit';
  /** The exceeded file cap ({@link MAX_FOLDER_UPLOAD_FILES}). */
  limit: number;
}

/**
 * How a drop resolves. `files` is the FLAT group — loose top-level file entries that keep today's
 * behavior (WITH the basename overwrite preflight). `items` is the TREE group — directory contents
 * uploaded as a folder (relative paths, no preflight). A mixed drop populates BOTH; an over-limit tree
 * rejects the whole drop.
 */
export type DropResult = OverLimitResult | { kind: 'resolved'; files: File[]; items: UploadItem[] };

/** How a `webkitdirectory` picker selection resolves: the folder upload items, or an over-limit signal. */
export type FolderPickResult = OverLimitResult | { kind: 'resolved'; items: UploadItem[] };

/** True when a file `<input>` supports directory selection (the non-standard `webkitdirectory`). */
export function isDirectoryPickerSupported(): boolean {
  return typeof document !== 'undefined' && 'webkitdirectory' in document.createElement('input');
}

/**
 * Builds upload items from a `<input webkitdirectory>` selection. Each file's `webkitRelativePath`
 * (e.g. `proj/sub/note.txt`, INCLUDING the leaf name) becomes its relative path; a browser that leaves
 * it blank falls back to the plain file name. Applies the SAME {@link MAX_FOLDER_UPLOAD_FILES} cap as a
 * directory drop, rejecting the whole selection (no partial upload) when exceeded.
 */
export function filesFromDirectoryInput(files: File[]): FolderPickResult {
  if (files.length > MAX_FOLDER_UPLOAD_FILES) {
    return { kind: 'over-limit', limit: MAX_FOLDER_UPLOAD_FILES };
  }
  return { kind: 'resolved', items: files.map((file) => ({ file, relativePath: relativePathOf(file) })) };
}

function relativePathOf(file: File): string {
  const webkitRelativePath = (file as { webkitRelativePath?: string }).webkitRelativePath;
  return webkitRelativePath && webkitRelativePath.length > 0 ? webkitRelativePath : file.name;
}

/**
 * Resolves a drop into a FLAT group (loose top-level files) plus a TREE group (directory contents).
 *
 * When the platform exposes `webkitGetAsEntry`, the drop is SPLIT: loose top-level file entries become
 * the flat group (kept on today's path, including the basename overwrite preflight) and directory
 * entries become the folder/tree group (relative paths, no preflight). This stops a loose file dropped
 * alongside a folder from silently skipping the overwrite preflight. Without entry support, everything
 * stays flat (today's behavior). Entries are captured SYNCHRONOUSLY (the DataTransfer and its entries
 * are only valid during the drop handler) and directories are traversed depth-first, reading each in
 * repeated `readEntries` batches until an empty batch signals the end. A tree that exceeds
 * {@link MAX_FOLDER_UPLOAD_FILES} rejects the whole drop as {@link OverLimitResult}.
 */
export async function resolveDrop(dataTransfer: DataTransferLike): Promise<DropResult> {
  const items = dataTransfer.items ? Array.from(dataTransfer.items) : [];
  const fileItems = items.filter((item) => item.kind === 'file');
  const supportsEntries = fileItems.length > 0 && typeof fileItems[0].webkitGetAsEntry === 'function';
  if (!supportsEntries) {
    // No entry traversal available: keep every dropped file in the flat group (overwrite preflight).
    return { kind: 'resolved', files: dataTransfer.files ? Array.from(dataTransfer.files) : [], items: [] };
  }

  // Capture entries synchronously, before any await, while the DataTransfer is still live.
  const entries = fileItems
    .map((item) => item.webkitGetAsEntry?.() ?? null)
    .filter((entry): entry is FileSystemEntryLike => entry != null);

  // Loose top-level files → FLAT group (read their File so the basename overwrite preflight can run).
  const files: File[] = [];
  for (const entry of entries.filter((entry) => entry.isFile)) {
    files.push(await readFile(entry));
  }

  // Directory entries → TREE group (folder upload), bounded by the shared file cap.
  const collected: UploadItem[] = [];
  for (const entry of entries.filter((entry) => entry.isDirectory)) {
    if (!(await traverseEntry(entry, entry.name, collected))) {
      return { kind: 'over-limit', limit: MAX_FOLDER_UPLOAD_FILES };
    }
  }
  return { kind: 'resolved', files, items: collected };
}

/**
 * Depth-first traversal accumulating `{ file, relativePath }` items. Returns `false` (WITHOUT mutating
 * further) as soon as adding a file would exceed {@link MAX_FOLDER_UPLOAD_FILES}, so the caller can
 * reject the whole selection instead of silently truncating; returns `true` when the subtree drains.
 */
async function traverseEntry(
  entry: FileSystemEntryLike,
  path: string,
  out: UploadItem[]
): Promise<boolean> {
  if (entry.isFile && entry.file) {
    if (out.length >= MAX_FOLDER_UPLOAD_FILES) {
      return false; // adding this file would exceed the cap
    }
    out.push({ file: await readFile(entry), relativePath: path });
    return true;
  }
  if (entry.isDirectory && entry.createReader) {
    const reader = entry.createReader();
    // Browsers page a directory's children: keep reading until a batch comes back empty.
    for (let batch = await readBatch(reader); batch.length > 0; batch = await readBatch(reader)) {
      for (const child of batch) {
        if (!(await traverseEntry(child, `${path}/${child.name}`, out))) {
          return false;
        }
      }
    }
  }
  return true;
}

function readFile(entry: FileSystemEntryLike): Promise<File> {
  return new Promise((resolve, reject) => entry.file!(resolve, reject));
}

function readBatch(reader: DirectoryReaderLike): Promise<FileSystemEntryLike[]> {
  return new Promise((resolve, reject) => reader.readEntries(resolve, reject));
}
