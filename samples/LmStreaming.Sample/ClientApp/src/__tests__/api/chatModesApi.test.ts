import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createChatMode, updateChatMode, InvalidEnvError } from '@/api/chatModesApi';
import type { ChatModeCreateUpdate } from '@/types/chatMode';

const fetchMock = vi.fn();
vi.stubGlobal('fetch', fetchMock);

const fail = (status: number, statusText: string, body: unknown) =>
  Promise.resolve({ ok: false, status, statusText, json: async () => body });

const unreadable = (status: number, statusText: string) =>
  Promise.resolve({
    ok: false,
    status,
    statusText,
    json: async () => {
      throw new SyntaxError('Unexpected token < in JSON');
    },
  });

const mode = { name: 'M', systemPrompt: 'p', env: { FOO: 'bar' } } as unknown as ChatModeCreateUpdate;

/**
 * The adapter's failure classification is what ModeEditor and ChatLayout branch on, and their own
 * tests mock this module — so without these, a wire-shape change would leave every component test
 * green while env rejections silently degrade to a generic error.
 */
describe('chatModesApi typed failures', () => {
  beforeEach(() => fetchMock.mockReset());

  it('maps 400 invalid_env on update to InvalidEnvError with the keys, the layer and a message naming them', async () => {
    fetchMock.mockReturnValue(
      fail(400, 'Bad Request', {
        error: "Mode 'm1' was saved, but its environment could not be applied.",
        code: 'invalid_env',
        saved: true,
        layer: 'mode',
        keys: ['NO_PROXY'],
      })
    );

    const error = (await updateChatMode('m1', mode).catch((e: unknown) => e)) as InvalidEnvError;

    expect(error).toBeInstanceOf(InvalidEnvError);
    expect(error.keys).toEqual(['NO_PROXY']);
    expect(error.layer).toBe('mode');
    expect(error.message).toBe("Mode 'm1' was saved, but its environment could not be applied. (NO_PROXY)");
  });

  it('maps 400 invalid_env on create, ignoring a keys field that is not a list', async () => {
    fetchMock.mockReturnValue(
      fail(400, 'Bad Request', { error: 'Invalid env.', code: 'invalid_env', keys: 'NO_PROXY' })
    );

    const error = (await createChatMode(mode).catch((e: unknown) => e)) as InvalidEnvError;

    expect(error).toBeInstanceOf(InvalidEnvError);
    expect(error.keys).toEqual([]);
    expect(error.layer).toBeNull();
    expect(error.message).toBe('Invalid env.');
  });

  it('falls back to the server error text for any other failure', async () => {
    fetchMock.mockReturnValue(fail(400, 'Bad Request', { error: 'Name is required.', code: 'other' }));

    const error = (await updateChatMode('m1', mode).catch((e: unknown) => e)) as Error;

    expect(error).not.toBeInstanceOf(InvalidEnvError);
    expect(error.message).toBe('Name is required.');
  });

  it('falls back to the status text when the error body is not JSON', async () => {
    fetchMock.mockReturnValue(unreadable(502, 'Bad Gateway'));

    const error = (await createChatMode(mode).catch((e: unknown) => e)) as Error;

    expect(error).not.toBeInstanceOf(InvalidEnvError);
    expect(error.message).toBe('Failed to create chat mode: Bad Gateway');
  });

  // PAIRED POSITIVE: the adapter must still send the env it was given and return the saved mode.
  it('sends env on the wire and returns the saved mode on success', async () => {
    fetchMock.mockReturnValue(
      Promise.resolve({ ok: true, status: 200, statusText: 'OK', json: async () => ({ id: 'm1' }) })
    );

    const saved = await updateChatMode('m1', mode);

    expect(saved).toEqual({ id: 'm1' });
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toContain('/api/chat-modes/m1');
    expect(init.method).toBe('PUT');
    expect(JSON.parse(init.body as string).env).toEqual({ FOO: 'bar' });
  });
});
