import { describe, it, expect, vi, afterEach } from 'vitest';
import { listShares, addShare, removeShare } from '@/api/sharesApi';
import { ConversationApiError } from '@/api/conversationsApi';
import type { ConversationShare } from '@/types/shares';

afterEach(() => vi.restoreAllMocks());

function jsonResponse(payload: unknown, status = 200): Response {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function spyFetch(response: Response) {
  return vi.spyOn(globalThis, 'fetch').mockResolvedValue(response);
}

const grant: ConversationShare = {
  threadId: 'thread-1',
  subjectId: 'tid-1:oid-a',
  role: 'viewer',
  grantedBy: 'tid-1:oid-owner',
  grantedAtUnixMs: 1_700_000_000_000,
  expiresAtUnixMs: null,
};

describe('sharesApi.listShares', () => {
  it('requests the share collection for the thread', async () => {
    const fetchSpy = spyFetch(jsonResponse([grant]));

    const result = await listShares('thread-1');

    expect(fetchSpy).toHaveBeenCalledWith('/api/conversations/thread-1/shares');
    expect(result).toEqual([grant]);
  });

  it('url-encodes a thread id with reserved characters', async () => {
    const fetchSpy = spyFetch(jsonResponse([]));

    await listShares('a/b c');

    expect(fetchSpy).toHaveBeenCalledWith('/api/conversations/a%2Fb%20c/shares');
  });

  it('preserves the unknown_thread code so callers can tell it from a real failure', async () => {
    spyFetch(jsonResponse({ error: "Conversation 'x' not found.", code: 'unknown_thread' }, 404));

    const error = await listShares('x').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ConversationApiError);
    expect((error as ConversationApiError).status).toBe(404);
    expect((error as ConversationApiError).code).toBe('unknown_thread');
  });
});

describe('sharesApi.addShare', () => {
  it('posts the subject and role as JSON', async () => {
    const fetchSpy = spyFetch(jsonResponse(grant));

    const result = await addShare('thread-1', { subjectId: 'tid-1:oid-a', role: 'viewer' });

    expect(fetchSpy).toHaveBeenCalledWith('/api/conversations/thread-1/shares', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ subjectId: 'tid-1:oid-a', role: 'viewer' }),
    });
    expect(result).toEqual(grant);
  });

  it('preserves the grantee_may_not_reshare code on a 403', async () => {
    spyFetch(jsonResponse({ error: 'forbidden', code: 'grantee_may_not_reshare' }, 403));

    const error = await addShare('thread-1', { subjectId: 's', role: 'viewer' }).catch(
      (e: unknown) => e
    );

    expect((error as ConversationApiError).status).toBe(403);
    expect((error as ConversationApiError).code).toBe('grantee_may_not_reshare');
  });

  it('preserves the sharing_unavailable code on a 503', async () => {
    spyFetch(jsonResponse({ error: 'sharing_unavailable', code: 'sharing_unavailable' }, 503));

    const error = await addShare('thread-1', { subjectId: 's', role: 'viewer' }).catch(
      (e: unknown) => e
    );

    expect((error as ConversationApiError).status).toBe(503);
    expect((error as ConversationApiError).code).toBe('sharing_unavailable');
  });
});

describe('sharesApi.removeShare', () => {
  it('deletes the subject under the thread, encoding the tid:oid colon', async () => {
    const fetchSpy = spyFetch(new Response(null, { status: 204 }));

    await removeShare('thread-1', 'tid-1:oid-a');

    expect(fetchSpy).toHaveBeenCalledWith('/api/conversations/thread-1/shares/tid-1%3Aoid-a', {
      method: 'DELETE',
    });
  });

  it('resolves on the 204 the server returns whether or not a grant existed', async () => {
    spyFetch(new Response(null, { status: 204 }));

    await expect(removeShare('thread-1', 'nobody')).resolves.toBeUndefined();
  });
});
