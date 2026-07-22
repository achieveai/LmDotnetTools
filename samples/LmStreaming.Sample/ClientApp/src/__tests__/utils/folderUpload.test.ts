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

/** Builds a DataTransfer-like whose items expose `webkitGetAsEntry`. */
function dropWithEntries(entries: FileSystemEntryLike[]): DataTransferLike {
  return {
    items: entries.map((entry) => ({ kind: 'file', webkitGetAsEntry: () => entry })),
    files: entries
      .filter((e) => e.isFile)
      .map((e) => new File(['x'], e.name)),
  };
}

describe('filesFromDirectoryInput', () => {
  it('builds an UploadItem per file using its webkitRelativePath (INCLUDING the leaf name)', () => {
    const files = [fileWithRelPath('proj/a.txt'), fileWithRelPath('proj/sub/b.txt')];

    const items = filesFromDirectoryInput(files);

    expect(items.map((i) => i.relativePath)).toEqual(['proj/a.txt', 'proj/sub/b.txt']);
    expect(items[0].file).toBe(files[0]);
  });

  it('falls back to the plain file name when webkitRelativePath is empty', () => {
    const bare = new File(['x'], 'loose.txt'); // no webkitRelativePath

    const items = filesFromDirectoryInput([bare]);

    expect(items).toEqual([{ file: bare, relativePath: 'loose.txt' }]);
  });
});

describe('resolveDrop', () => {
  it('falls back to a FLAT drop when webkitGetAsEntry is unavailable', async () => {
    const files = [new File(['a'], 'a.txt'), new File(['b'], 'b.txt')];
    const dt: DataTransferLike = { items: [{ kind: 'file' }], files };

    const result = await resolveDrop(dt);

    expect(result.kind).toBe('flat');
    expect(result.kind === 'flat' && result.files).toEqual(files);
  });

  it('treats a drop of only loose files as FLAT (keeps today behavior incl. the overwrite preflight)', async () => {
    const result = await resolveDrop(dropWithEntries([fileEntry('a.txt'), fileEntry('b.txt')]));

    expect(result.kind).toBe('flat');
  });

  it('traverses a directory across MULTIPLE readEntries batches into a TREE', async () => {
    // The directory pages its children in two non-empty batches, then an empty one.
    const tree = dirEntry('proj', [
      [fileEntry('a.txt'), dirEntry('sub', [[fileEntry('c.txt')]])],
      [fileEntry('b.txt')],
    ]);

    const result = await resolveDrop(dropWithEntries([tree]));

    expect(result.kind).toBe('tree');
    const paths = result.kind === 'tree' ? result.items.map((i) => i.relativePath).sort() : [];
    expect(paths).toEqual(['proj/a.txt', 'proj/b.txt', 'proj/sub/c.txt']);
  });

  it('keeps duplicate basenames from different directories DISTINCT by relativePath', async () => {
    const tree = dirEntry('proj', [
      [
        dirEntry('a', [[fileEntry('readme.md')]]),
        dirEntry('b', [[fileEntry('readme.md')]]),
      ],
    ]);

    const result = await resolveDrop(dropWithEntries([tree]));

    const paths = result.kind === 'tree' ? result.items.map((i) => i.relativePath).sort() : [];
    expect(paths).toEqual(['proj/a/readme.md', 'proj/b/readme.md']);
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
