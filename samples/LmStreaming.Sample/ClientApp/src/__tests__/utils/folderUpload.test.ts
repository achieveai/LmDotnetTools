import { describe, it, expect } from 'vitest';
import {
  filesFromDirectoryInput,
  resolveDrop,
  isDirectoryPickerSupported,
  MAX_FOLDER_UPLOAD_FILES,
  type FileSystemEntryLike,
  type DataTransferLike,
} from '@/utils/folderUpload';

/** Builds a File-like object carrying a `webkitRelativePath` (happy-dom's File lacks a settable one). */
function fileWithRelPath(relativePath: string, name = relativePath.split('/').pop() ?? relativePath): File {
  const file = new File(['x'], name);
  Object.defineProperty(file, 'webkitRelativePath', { value: relativePath, configurable: true });
  return file;
}

/** A leaf file entry that resolves its File via the async `file(cb)` callback. */
function fileEntry(name: string): FileSystemEntryLike {
  return {
    isFile: true,
    isDirectory: false,
    name,
    file: (success) => success(new File(['data'], name)),
  };
}

/**
 * A directory entry whose reader PAGES its children: it returns `batches` in order, then an empty
 * batch (mirroring how real browsers page `readEntries`). This lets a test prove the traversal keeps
 * calling `readEntries` until it drains.
 */
function dirEntry(name: string, batches: FileSystemEntryLike[][]): FileSystemEntryLike {
  return {
    isFile: false,
    isDirectory: true,
    name,
    createReader: () => {
      let index = 0;
      return {
        readEntries: (success) => {
          const batch = index < batches.length ? batches[index] : [];
          index += 1;
          success(batch);
        },
      };
    },
  };
}

/** Builds `n` distinct leaf file entries (for exercising the file-cap boundary). */
function manyFileEntries(n: number): FileSystemEntryLike[] {
  return Array.from({ length: n }, (_, i) => fileEntry(`f${i}.txt`));
}

/** Builds a DataTransfer-like whose items expose `webkitGetAsEntry`. */
function dropWithEntries(entries: FileSystemEntryLike[]): DataTransferLike {
  return {
    items: entries.map((entry) => ({ kind: 'file', webkitGetAsEntry: () => entry })),
    files: entries.filter((e) => e.isFile).map((e) => new File(['x'], e.name)),
  };
}

describe('filesFromDirectoryInput', () => {
  it('builds an UploadItem per file using its webkitRelativePath (INCLUDING the leaf name)', () => {
    const files = [fileWithRelPath('proj/a.txt'), fileWithRelPath('proj/sub/b.txt')];

    const result = filesFromDirectoryInput(files);

    expect(result.kind).toBe('resolved');
    if (result.kind !== 'resolved') return;
    expect(result.items.map((i) => i.relativePath)).toEqual(['proj/a.txt', 'proj/sub/b.txt']);
    expect(result.items[0].file).toBe(files[0]);
  });

  it('falls back to the plain file name when webkitRelativePath is empty', () => {
    const bare = new File(['x'], 'loose.txt'); // no webkitRelativePath

    const result = filesFromDirectoryInput([bare]);

    expect(result).toEqual({ kind: 'resolved', items: [{ file: bare, relativePath: 'loose.txt' }] });
  });

  // F4: the webkitdirectory picker must apply the SAME shared file cap as drag/drop, rejecting the
  // whole selection (no partial upload) when it is exceeded.
  it('signals over-limit for a picker selection exceeding the file cap', () => {
    const files = Array.from({ length: MAX_FOLDER_UPLOAD_FILES + 1 }, (_, i) =>
      fileWithRelPath(`proj/f${i}.txt`)
    );

    expect(filesFromDirectoryInput(files)).toEqual({ kind: 'over-limit', limit: MAX_FOLDER_UPLOAD_FILES });
  });

  it('resolves a picker selection exactly at the file cap (boundary, not over-limit)', () => {
    const files = Array.from({ length: MAX_FOLDER_UPLOAD_FILES }, (_, i) => fileWithRelPath(`proj/f${i}.txt`));

    const result = filesFromDirectoryInput(files);

    expect(result.kind).toBe('resolved');
    if (result.kind === 'resolved') {
      expect(result.items).toHaveLength(MAX_FOLDER_UPLOAD_FILES);
    }
  });
});

describe('resolveDrop', () => {
  it('falls back to a flat-only resolution when webkitGetAsEntry is unavailable', async () => {
    const files = [new File(['a'], 'a.txt'), new File(['b'], 'b.txt')];
    const dt: DataTransferLike = { items: [{ kind: 'file' }], files };

    const result = await resolveDrop(dt);

    expect(result.kind).toBe('resolved');
    if (result.kind !== 'resolved') return;
    expect(result.files).toEqual(files);
    expect(result.items).toEqual([]);
  });

  it('resolves a drop of only loose files into the FLAT group (empty tree — keeps the overwrite preflight)', async () => {
    const result = await resolveDrop(dropWithEntries([fileEntry('a.txt'), fileEntry('b.txt')]));

    expect(result.kind).toBe('resolved');
    if (result.kind !== 'resolved') return;
    expect(result.files.map((f) => f.name)).toEqual(['a.txt', 'b.txt']);
    expect(result.items).toEqual([]);
  });

  // F2: a MIXED drop (loose top-level files ALONGSIDE a folder) must split — the loose files go to the
  // FLAT group (overwrite preflight) and the directory becomes the TREE group (no preflight).
  it('splits a MIXED drop into a flat file group and a folder tree group', async () => {
    const result = await resolveDrop(
      dropWithEntries([dirEntry('proj', [[fileEntry('a.txt')]]), fileEntry('readme.md')])
    );

    expect(result.kind).toBe('resolved');
    if (result.kind !== 'resolved') return;
    // Loose top-level file → flat group (basename overwrite preflight).
    expect(result.files.map((f) => f.name)).toEqual(['readme.md']);
    // Directory → tree group (relative paths, no preflight).
    expect(result.items.map((i) => i.relativePath)).toEqual(['proj/a.txt']);
  });

  it('traverses a directory across MULTIPLE readEntries batches into the tree group', async () => {
    // The directory pages its children in two non-empty batches, then an empty one.
    const tree = dirEntry('proj', [
      [fileEntry('a.txt'), dirEntry('sub', [[fileEntry('c.txt')]])],
      [fileEntry('b.txt')],
    ]);

    const result = await resolveDrop(dropWithEntries([tree]));

    expect(result.kind).toBe('resolved');
    const paths = result.kind === 'resolved' ? result.items.map((i) => i.relativePath).sort() : [];
    expect(paths).toEqual(['proj/a.txt', 'proj/b.txt', 'proj/sub/c.txt']);
    expect(result.kind === 'resolved' && result.files).toEqual([]);
  });

  it('keeps duplicate basenames from different directories DISTINCT by relativePath', async () => {
    const tree = dirEntry('proj', [
      [dirEntry('a', [[fileEntry('readme.md')]]), dirEntry('b', [[fileEntry('readme.md')]])],
    ]);

    const result = await resolveDrop(dropWithEntries([tree]));

    const paths = result.kind === 'resolved' ? result.items.map((i) => i.relativePath).sort() : [];
    expect(paths).toEqual(['proj/a/readme.md', 'proj/b/readme.md']);
  });

  // F3: reaching MAX_FOLDER_UPLOAD_FILES must be signalled EXPLICITLY (over-limit) so the caller can
  // reject the whole selection, instead of silently truncating to the collected subset.
  it('signals over-limit (rejecting the whole selection) when a tree exceeds the file cap', async () => {
    const overCap = dirEntry('big', [manyFileEntries(MAX_FOLDER_UPLOAD_FILES + 1)]);

    const result = await resolveDrop(dropWithEntries([overCap]));

    expect(result).toEqual({ kind: 'over-limit', limit: MAX_FOLDER_UPLOAD_FILES });
  });

  it('resolves a tree exactly at the file cap (boundary, not over-limit)', async () => {
    const atCap = dirEntry('big', [manyFileEntries(MAX_FOLDER_UPLOAD_FILES)]);

    const result = await resolveDrop(dropWithEntries([atCap]));

    expect(result.kind).toBe('resolved');
    if (result.kind === 'resolved') {
      expect(result.items).toHaveLength(MAX_FOLDER_UPLOAD_FILES);
    }
  });
});

describe('isDirectoryPickerSupported / MAX_FOLDER_UPLOAD_FILES', () => {
  it('returns a boolean for the current environment', () => {
    expect(typeof isDirectoryPickerSupported()).toBe('boolean');
  });

  it('exposes a positive bound for traversal', () => {
    expect(MAX_FOLDER_UPLOAD_FILES).toBeGreaterThan(0);
  });
});
