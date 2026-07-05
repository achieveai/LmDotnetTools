import { describe, it, expect, afterEach, vi } from 'vitest';
import {
  getConversationStatus,
  provisionConversation,
  sendConversationMessage,
  ConversationApiError,
} from '@/api/conversationsApi';

function mockFetchOnce(status: number, body: unknown) {
  const original = globalThis.fetch;
  const fetchSpy = vi.fn(
    async () =>
      new Response(JSON.stringify(body), {
        status,
        headers: { 'Content-Type': 'application/json' },
      })
  );
  globalThis.fetch = fetchSpy as unknown as typeof fetch;
  return { fetchSpy, restore: () => (globalThis.fetch = original) };
}

// GetStatus's unknown-inputId/unknown-runId 404 is a distinct case from the unknown-threadId 404
// (same status code, different body `code`) — see ConversationsController.GetStatus and plan v5
// step 12. A caller polling status must be able to tell "this thread doesn't exist" apart from
// "this thread exists but I asked about an id it never accepted/assigned".
describe('conversationsApi.getConversationStatus — unknown-inputId vs unknown-thread 404', () => {
  let restore: (() => void) | undefined;
  afterEach(() => restore?.());

  it('resolves the status on success', async () => {
    const mock = mockFetchOnce(200, {
      threadId: 'thread-1',
      runId: 'run-1',
      status: 'Completed',
      response: { text: 'done' },
    });
    restore = mock.restore;

    const result = await getConversationStatus('thread-1', { inputId: 'input-1' });

    expect(result.status).toBe('Completed');
    expect(mock.fetchSpy).toHaveBeenCalledWith('/api/conversations/thread-1/status?inputId=input-1');
  });

  it('throws ConversationApiError with code unknown_thread for an unprovisioned thread', async () => {
    const mock = mockFetchOnce(404, {
      error: "Conversation 'thread-missing' not found.",
      code: 'unknown_thread',
    });
    restore = mock.restore;

    const error = await getConversationStatus('thread-missing', { inputId: 'input-1' }).catch((e) => e);

    expect(error).toBeInstanceOf(ConversationApiError);
    expect((error as ConversationApiError).status).toBe(404);
    expect((error as ConversationApiError).code).toBe('unknown_thread');
  });

  it('throws ConversationApiError with a distinct code unknown_inputId for an unrecognized inputId on a real thread', async () => {
    const mock = mockFetchOnce(404, {
      error: "Unknown inputId 'input-missing' for thread 'thread-1'.",
      code: 'unknown_inputId',
    });
    restore = mock.restore;

    const error = await getConversationStatus('thread-1', { inputId: 'input-missing' }).catch((e) => e);

    expect(error).toBeInstanceOf(ConversationApiError);
    expect((error as ConversationApiError).code).toBe('unknown_inputId');
    expect((error as ConversationApiError).code).not.toBe('unknown_thread');
  });

  it('throws ConversationApiError with code unknown_runId when polling by runId', async () => {
    const mock = mockFetchOnce(404, {
      error: "Unknown runId 'run-missing' for thread 'thread-1'.",
      code: 'unknown_runId',
    });
    restore = mock.restore;

    const error = await getConversationStatus('thread-1', { runId: 'run-missing' }).catch((e) => e);

    expect(error).toBeInstanceOf(ConversationApiError);
    expect((error as ConversationApiError).code).toBe('unknown_runId');
    expect(mock.fetchSpy).toHaveBeenCalledWith('/api/conversations/thread-1/status?runId=run-missing');
  });

  it('leaves code undefined when the error body is not the expected shape', async () => {
    const mock = mockFetchOnce(500, 'Internal Server Error');
    restore = mock.restore;

    const error = await getConversationStatus('thread-1', { inputId: 'input-1' }).catch((e) => e);

    expect(error).toBeInstanceOf(ConversationApiError);
    expect((error as ConversationApiError).code).toBeUndefined();
    expect((error as ConversationApiError).status).toBe(500);
  });
});

describe('conversationsApi.provisionConversation / sendConversationMessage', () => {
  let restore: (() => void) | undefined;
  afterEach(() => restore?.());

  it('provisions a conversation and returns its server-minted threadId', async () => {
    const mock = mockFetchOnce(200, { threadId: 'thread-new' });
    restore = mock.restore;

    const result = await provisionConversation({
      workspaceId: 'ws-1',
      providerId: 'anthropic',
      modeId: 'default',
    });

    expect(result.threadId).toBe('thread-new');
    expect(mock.fetchSpy).toHaveBeenCalledWith(
      '/api/conversations',
      expect.objectContaining({ method: 'POST' })
    );
  });

  it('sends a message and returns the accepted inputId', async () => {
    const mock = mockFetchOnce(202, { inputId: 'input-1', queued: true });
    restore = mock.restore;

    const result = await sendConversationMessage('thread-1', { text: 'hi' });

    expect(result).toEqual({ inputId: 'input-1', queued: true });
  });

  it('throws ConversationApiError with code unknown_thread when sending to an unprovisioned thread', async () => {
    const mock = mockFetchOnce(404, {
      error: "Conversation 'thread-missing' not found.",
      code: 'unknown_thread',
    });
    restore = mock.restore;

    const error = await sendConversationMessage('thread-missing', { text: 'hi' }).catch((e) => e);

    expect(error).toBeInstanceOf(ConversationApiError);
    expect((error as ConversationApiError).code).toBe('unknown_thread');
  });
});
