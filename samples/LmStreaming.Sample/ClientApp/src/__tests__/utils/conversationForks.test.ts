import { describe, it, expect } from 'vitest';
import {
  forkOriginLabel,
  isCliBackedProvider,
  nestForks,
  resolveBranchAnchors,
} from '@/utils/conversationForks';
import type { ConversationSummary, DisplayItem } from '@/types';
import { MessageType } from '@/types';
import type { BranchPoint } from '@/api/conversationsApi';

const row = (threadId: string, overrides: Partial<ConversationSummary> = {}): ConversationSummary => ({
  threadId,
  title: threadId,
  lastUpdated: 0,
  ...overrides,
});

const forkOf = (threadId: string, parent: string, root: string, seq: number): ConversationSummary =>
  row(threadId, { forkedFrom: { threadId: parent, messageId: `m${seq}`, seq }, rootThreadId: root });

describe('nestForks (sidebar grouping)', () => {
  it('nests a fork AND a fork of that fork one level under the original', () => {
    const entries = nestForks([
      row('src'),
      forkOf('f1', 'src', 'src', 4),
      forkOf('f1a', 'f1', 'src', 7),
      row('other'),
    ]);

    expect(entries.map((e) => [e.conversation.threadId, e.forks.map((f) => f.threadId)])).toEqual([
      ['src', ['f1', 'f1a']],
      ['other', []],
    ]);
  });

  it('keeps a deleted original as the header of its forks, and drops it when none are loaded', () => {
    const withFork = nestForks([row('src', { deleted: true }), forkOf('f1', 'src', 'src', 2)]);
    expect(withFork).toHaveLength(1);
    expect(withFork[0].conversation.deleted).toBe(true);
    expect(withFork[0].forks.map((f) => f.threadId)).toEqual(['f1']);

    expect(nestForks([row('src', { deleted: true })])).toEqual([]);
  });

  it('shows a fork whose original is not in the loaded page as a top-level row', () => {
    const entries = nestForks([row('a'), forkOf('f1', 'src', 'src', 3)]);

    expect(entries.map((e) => e.conversation.threadId)).toEqual(['a', 'f1']);
    expect(entries[1].forks).toEqual([]);
  });
});

describe('forkOriginLabel', () => {
  it('says "from msg N" for a direct fork and names the parent fork for a fork of a fork', () => {
    const rows = [row('src'), forkOf('f1', 'src', 'src', 4), forkOf('f1a', 'f1', 'src', 7)];
    rows[1].title = 'Try option B';

    expect(forkOriginLabel(rows[1], rows)).toBe('from msg 4');
    expect(forkOriginLabel(rows[2], rows)).toBe('from "Try option B" · msg 7');
  });
});

describe('isCliBackedProvider', () => {
  it('is true for the CLI providers and their mocks, false for API and Copilot-API models', () => {
    for (const id of ['claude', 'codex', 'copilot', 'claude-mock', 'codex-mock', 'copilot-mock']) {
      expect(isCliBackedProvider(id)).toBe(true);
    }
    for (const id of ['openai', 'anthropic', 'test', 'claude-opus-4.8', 'gpt-5.5', null, undefined]) {
      expect(isCliBackedProvider(id)).toBe(false);
    }
  });
});

describe('resolveBranchAnchors', () => {
  const text = (value: string) => ({ $type: MessageType.Text, role: 'assistant' as const, text: value });
  const items: DisplayItem[] = [
    { type: 'user-message', id: 'u1', content: text('q'), status: 'completed', timestamp: 1, persistedId: 'm1', seq: 1 },
    { type: 'assistant-message', id: 'a1', content: text('a'), runId: 'r1', persistedId: 'm2', seq: 2 },
    { type: 'user-message', id: 'u2', content: text('q2'), status: 'completed', timestamp: 2, persistedId: 'm5', seq: 5 },
  ];
  const point = (afterMessageId: string, afterSeq: number): BranchPoint => ({
    afterMessageId,
    afterSeq,
    options: [],
  });

  it('anchors on the exact stored id, else on the last item at or before the seq', () => {
    const anchors = resolveBranchAnchors(items, [point('m2', 2), point('m4-usage-row', 4)]);

    // m4 is not drawn (e.g. a usage record ending run r1): its point lands after the r1 bubble.
    expect([...anchors.entries()].map(([id, pts]) => [id, pts.map((p) => p.afterSeq)])).toEqual([
      ['a1', [2, 4]],
    ]);
  });
});
