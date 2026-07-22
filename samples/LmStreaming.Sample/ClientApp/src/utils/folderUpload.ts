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

/**
 * The two ways a drop resolves: `flat` keeps today's behavior (loose files, WITH the basename overwrite
 * preflight); `tree` is a folder upload (relative paths, no preflight).
 */
export type DropResult =
  | { kind: 'flat'; files: File[] }
  | { kind: 'tree'; items: UploadItem[] };

/** Upper bound on files enumerated by a single folder upload, so a huge/looping tree stays bounded. */
export const MAX_FOLDER_UPLOAD_FILES = 2000;

/** True when a file `<input>` supports directory selection (the non-standard `webkitdirectory`). */
export function isDirectoryPickerSupported(): boolean {
  return typeof document !== 'undefined' && 'webkitdirectory' in document.createElement('input');
}

/**
 * Builds upload items from a `<input webkitdirectory>` selection. Each file's `webkitRelativePath`
 * (e.g. `proj/sub/note.txt`, INCLUDING the leaf name) becomes its relative path; a browser that leaves
 * it blank falls back to the plain file name.
 */
export function filesFromDirectoryInput(files: File[]): UploadItem[] {
  return files.map((file) => ({ file, relativePath: relativePathOf(file) }));
}

function relativePathOf(file: File): string {
  const webkitRelativePath = (file as { webkitRelativePath?: string }).webkitRelativePath;
  return webkitRelativePath && webkitRelativePath.length > 0 ? webkitRelativePath : file.name;
}

/**
 * Resolves a drop into either a FLAT file batch or a directory TREE.
 *
 * The drop is treated as a folder upload (`tree`) only when the platform exposes `webkitGetAsEntry` AND
 * the drop contains at least one directory; otherwise it stays FLAT (today's behavior, including the
 * basename overwrite preflight). Directory entries are captured SYNCHRONOUSLY (the DataTransfer and its
 * entries are only valid during the drop handler) and then traversed depth-first, reading each directory
 * in repeated `readEntries` batches until an empty batch signals the end.
 */
export async function resolveDrop(dataTransfer: DataTransferLike): Promise<DropResult> {
  const flat = (): DropResult => ({
    kind: 'flat',
    files: dataTransfer.files ? Array.from(dataTransfer.files) : [],
  });

  const items = dataTransfer.items ? Array.from(dataTransfer.items) : [];
  const fileItems = items.filter((item) => item.kind === 'file');
  const supportsEntries = fileItems.length > 0 && typeof fileItems[0].webkitGetAsEntry === 'function';
  if (!supportsEntries) {
    return flat();
  }

  // Capture entries synchronously, before any await, while the DataTransfer is still live.
  const entries = fileItems
    .map((item) => item.webkitGetAsEntry?.() ?? null)
    .filter((entry): entry is FileSystemEntryLike => entry != null);
  if (!entries.some((entry) => entry.isDirectory)) {
    // Only loose files were dropped: keep the flat path so the basename overwrite preflight still runs.
    return flat();
  }

  const collected: UploadItem[] = [];
  for (const entry of entries) {
    await traverseEntry(entry, entry.name, collected);
  }
  return { kind: 'tree', items: collected };
}

/** Depth-first traversal accumulating `{ file, relativePath }` items, bounded by {@link MAX_FOLDER_UPLOAD_FILES}. */
async function traverseEntry(
  entry: FileSystemEntryLike,
  path: string,
  out: UploadItem[]
): Promise<void> {
  if (out.length >= MAX_FOLDER_UPLOAD_FILES) {
    return;
  }
  if (entry.isFile && entry.file) {
    out.push({ file: await readFile(entry), relativePath: path });
    return;
  }
  if (entry.isDirectory && entry.createReader) {
    const reader = entry.createReader();
    // Browsers page a directory's children: keep reading until a batch comes back empty.
    for (let batch = await readBatch(reader); batch.length > 0; batch = await readBatch(reader)) {
      for (const child of batch) {
        await traverseEntry(child, `${path}/${child.name}`, out);
        if (out.length >= MAX_FOLDER_UPLOAD_FILES) {
          return;
        }
      }
    }
  }
}

function readFile(entry: FileSystemEntryLike): Promise<File> {
  return new Promise((resolve, reject) => entry.file!(resolve, reject));
}

function readBatch(reader: DirectoryReaderLike): Promise<FileSystemEntryLike[]> {
  return new Promise((resolve, reject) => reader.readEntries(resolve, reject));
}
