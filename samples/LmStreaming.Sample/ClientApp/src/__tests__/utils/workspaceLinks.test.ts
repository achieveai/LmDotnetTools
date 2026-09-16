import { describe, it, expect } from 'vitest';
import {
  buildWorkspaceLinkHref,
  isWorkspaceLinkCandidate,
  parseWorkspaceLinkHref,
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
