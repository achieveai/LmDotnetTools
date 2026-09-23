import { describe, it, expect } from 'vitest';
import {
  buildWorkspaceLinkHref,
  isWorkspaceLinkCandidate,
  parseWorkspaceLinkHref,
  resolveAgainstBaseDir,
} from '@/utils/workspaceLinks';

/**
 * The click handler opens a preview only for an href `parseWorkspaceLinkHref` accepts, so a rejected
 * href must come back null rather than a link with an empty thread or target.
 */
describe('parseWorkspaceLinkHref', () => {
  it('round-trips a thread and a target with separators, spaces and query characters', () => {
    const link = { threadId: 'thread-1', target: 'B:\\ws\\my docs\\a.md?x=1&y=2#L3' };
    expect(parseWorkspaceLinkHref(buildWorkspaceLinkHref(link))).toEqual(link);
  });

  it.each([
    ['a web link', 'https://example.com/a.md'],
    ['another in-page anchor', '#top'],
    ['no thread', '#workspace-file?target=docs%2Fa.md'],
    ['no target', '#workspace-file?thread=thread-1'],
    ['an empty target', '#workspace-file?thread=thread-1&target='],
  ])('returns null for %s', (_, href) => {
    expect(parseWorkspaceLinkHref(href)).toBeNull();
  });
});

describe('isWorkspaceLinkCandidate', () => {
  it.each([
    ['B:\\ws\\a.md', true],
    ['docs/a.md', true],
    ['#workspace-file?thread=x&target=y', true],
    ['https://example.com', false],
    ['HTTP://example.com', false],
    ['//example.com/a.md', false],
    ['  https://example.com  ', false],
    ['mailto:a@b.example', false],
    ['tel:1', false],
    ['#top', false],
    ['   ', false],
  ])('%j -> %s', (href, expected) => {
    expect(isWorkspaceLinkCandidate(href)).toBe(expected);
  });
});

/**
 * A link written INSIDE a previewed file means "relative to THAT file's directory", so the renderer joins the
 * directory on before the target leaves the client -- the server has no way to know which file a rendered link
 * came from. The join must fire for plain relative paths ONLY: every other shape already names its own location,
 * and prefixing one would resolve a different file (or nothing).
 *
 * The join is deliberately naive concatenation. Dot segments and the containment rule stay on the server
 * (`WorkspaceLinkResolver`), which is what makes a wrong answer here a failed resolve rather than an escape.
 */
describe('resolveAgainstBaseDir', () => {
  it.each([
    ['a sibling file', 'evidence/x.md', 'docs/rdb', 'docs/rdb/evidence/x.md'],
    ['a bare file name', 'x.md', 'docs/rdb', 'docs/rdb/x.md'],
    ['a dot-relative path', './evidence/x.md', 'docs/rdb', 'docs/rdb/evidence/x.md'],
    ['a parent-relative path (left for the server to normalise)', '../sib/x.md', 'docs/rdb', 'docs/rdb/../sib/x.md'],
    ['a percent-encoded name', 'my%20notes.md', 'docs/rdb', 'docs/rdb/my%20notes.md'],
    ['a fragment suffix', 'x.md#L10', 'docs/rdb', 'docs/rdb/x.md#L10'],
  ])('joins %s', (_, href, baseDir, expected) => {
    expect(resolveAgainstBaseDir(href, baseDir)).toBe(expected);
  });

  it.each([
    ['a file at the workspace root has no directory to join', 'x.md', ''],
    ['no base at all (every link in a chat message)', 'docs/x.md', undefined],
  ])('leaves the href alone when %s', (_, href, baseDir) => {
    expect(resolveAgainstBaseDir(href, baseDir)).toBe(href);
  });

  it.each([
    ['a posix absolute path', '/workspace/docs/a.md'],
    ['a windows absolute path', 'B:\\ws\\docs\\a.md'],
    ['a forward-slash drive path', 'B:/ws/docs/a.md'],
    ['a rooted path with no drive', '\\ws\\a.md'],
    ['a file URI', 'file:///B:/ws/docs/a.md'],
    ['a sandbox container URI', 'sandbox:/workspace/docs/a.md'],
    ['a model-authored in-page anchor', '#workspace-file?thread=t&target=x'],
  ])('never joins %s, which already names its own location', (_, href) => {
    expect(resolveAgainstBaseDir(href, 'docs/rdb')).toBe(href);
  });

  it('is not confused by a colon inside a posix file name, which is not a scheme', () => {
    expect(resolveAgainstBaseDir('notes/a:b.md', 'docs/rdb')).toBe('docs/rdb/notes/a:b.md');
  });
});
