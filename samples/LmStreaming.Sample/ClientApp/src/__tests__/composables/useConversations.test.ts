import { describe, it, expect, vi, afterEach } from 'vitest';
import { useConversations } from '@/composables/useConversations';

afterEach(() => vi.restoreAllMocks());

function jsonResponse(payload: unknown, status = 200): Response {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

const binding = { workspaceId: 'ws-1', providerId: 'anthropic', modeId: 'default' };

/**
 * #435. Under `Identity:Enforce=true` the WebSocket gate refuses a thread id with no metadata row,
 * byte-identically to one owned by somebody else, and deliberately does NOT mint a row for it — so
 * a client that invents its own id can never open a socket. The id must come from the server.
 */
describe('useConversations provisioning (#435)', () => {
  it('takes the thread id from POST /api/conversations rather than minting one locally', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    fetchSpy.mockResolvedValueOnce(jsonResponse({ threadId: 'thread-server-minted' }));
    const { createNewConversation, currentThreadId } = useConversations();

    const threadId = await createNewConversation(binding);

    expect(fetchSpy).toHaveBeenCalledWith('/api/conversations', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(binding),
    });
    // The SERVER's id, verbatim — not a locally generated one that happens to look similar.
    expect(threadId).toBe('thread-server-minted');
    expect(currentThreadId.value).toBe('thread-server-minted');
  });

  it('surfaces a provisioning failure instead of falling back to a locally minted id', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    fetchSpy.mockResolvedValueOnce(
      jsonResponse(
        { error: 'provider_unavailable', code: 'provider_unavailable', providerId: 'anthropic' },
        503
      )
    );
    const { createNewConversation, currentThreadId } = useConversations();

    // A fallback id would open a socket the gate then refuses — a conversation that looks started
    // and cannot stream is worse than one that visibly failed to start.
    await expect(createNewConversation(binding)).rejects.toThrow(/provider_unavailable/);
    expect(currentThreadId.value).toBeNull();
  });
});
