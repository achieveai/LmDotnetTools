import { describe, it, expect, vi, afterEach } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import ShareConversationModal from '@/components/ShareConversationModal.vue';
import type { ConversationShare } from '@/types/shares';

afterEach(() => vi.restoreAllMocks());

/** Builds a JSON `Response`, matching the fixture helper the file-browser tests use. */
function jsonResponse(payload: unknown, status = 200): Response {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

const viewerGrant: ConversationShare = {
  threadId: 'thread-1',
  subjectId: 'tid-1:oid-a',
  role: 'viewer',
  grantedBy: 'tid-1:oid-owner',
  grantedAtUnixMs: 1_700_000_000_000,
  expiresAtUnixMs: null,
};

/** Mounts the modal with the initial `GET .../shares` already answered. */
async function mountModal(initial: ConversationShare[] = [viewerGrant]) {
  const fetchSpy = vi.spyOn(globalThis, 'fetch');
  fetchSpy.mockResolvedValueOnce(jsonResponse(initial));
  const wrapper = mount(ShareConversationModal, {
    props: { threadId: 'thread-1' },
    attachTo: document.body,
  });
  await flushPromises();
  return { wrapper, fetchSpy };
}

describe('ShareConversationModal roster', () => {
  it('lists one row per grant returned by the API, with its role', async () => {
    const editorGrant: ConversationShare = {
      ...viewerGrant,
      subjectId: 'tid-1:oid-b',
      role: 'editor',
    };
    const { wrapper, fetchSpy } = await mountModal([viewerGrant, editorGrant]);

    expect(fetchSpy).toHaveBeenCalledWith('/api/conversations/thread-1/shares');
    expect(wrapper.find('[data-testid="share-row-tid-1:oid-a"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="share-role-tid-1:oid-a"]').text()).toBe('viewer');
    expect(wrapper.find('[data-testid="share-role-tid-1:oid-b"]').text()).toBe('editor');
    expect(wrapper.find('[data-testid="share-empty"]').exists()).toBe(false);
  });

  it('renders the empty state when nothing is shared', async () => {
    const { wrapper } = await mountModal([]);

    expect(wrapper.find('[data-testid="share-empty"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="share-add-form"]').exists()).toBe(true);
  });
});

describe('ShareConversationModal mutations', () => {
  it('posts the subject and role, then re-reads the roster', async () => {
    const { wrapper, fetchSpy } = await mountModal([]);

    const added: ConversationShare = { ...viewerGrant, subjectId: 'tid-1:oid-b', role: 'editor' };
    fetchSpy.mockResolvedValueOnce(jsonResponse(added));
    fetchSpy.mockResolvedValueOnce(jsonResponse([added]));

    await wrapper.find('[data-testid="share-subject-input"]').setValue('tid-1:oid-b');
    await wrapper.find('[data-testid="share-role-select"]').setValue('editor');
    await wrapper.find('[data-testid="share-add-button"]').trigger('click');
    await flushPromises();

    expect(fetchSpy).toHaveBeenNthCalledWith(2, '/api/conversations/thread-1/shares', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ subjectId: 'tid-1:oid-b', role: 'editor' }),
    });
    // The third call is the refresh, and its result is what is on screen.
    expect(fetchSpy).toHaveBeenNthCalledWith(3, '/api/conversations/thread-1/shares');
    expect(wrapper.find('[data-testid="share-row-tid-1:oid-b"]').exists()).toBe(true);
    // The input is cleared so the next grant does not silently repeat the last one.
    expect(
      (wrapper.find('[data-testid="share-subject-input"]').element as HTMLInputElement).value
    ).toBe('');
  });

  it('deletes the subject, then re-reads the roster', async () => {
    const { wrapper, fetchSpy } = await mountModal([viewerGrant]);

    fetchSpy.mockResolvedValueOnce(new Response(null, { status: 204 }));
    fetchSpy.mockResolvedValueOnce(jsonResponse([]));

    await wrapper.find('[data-testid="share-remove-tid-1:oid-a"]').trigger('click');
    await flushPromises();

    expect(fetchSpy).toHaveBeenNthCalledWith(2, '/api/conversations/thread-1/shares/tid-1%3Aoid-a', {
      method: 'DELETE',
    });
    expect(fetchSpy).toHaveBeenNthCalledWith(3, '/api/conversations/thread-1/shares');
    expect(wrapper.find('[data-testid="share-row-tid-1:oid-a"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="share-empty"]').exists()).toBe(true);
  });
});

describe('ShareConversationModal sharing_unavailable', () => {
  it('disables the add control and explains that sharing is off on this host', async () => {
    const { wrapper, fetchSpy } = await mountModal([]);

    fetchSpy.mockResolvedValueOnce(
      jsonResponse(
        { error: 'sharing_unavailable', code: 'sharing_unavailable', threadId: 'thread-1' },
        503
      )
    );
    await wrapper.find('[data-testid="share-subject-input"]').setValue('tid-1:oid-b');
    await wrapper.find('[data-testid="share-add-button"]').trigger('click');
    await flushPromises();

    expect(wrapper.find('[data-testid="share-refusal"]').text()).toContain('not enabled on this host');

    // Kept on screen but inert: this is a fact about the deployment, not about this caller's
    // rights, so withdrawing the control entirely would misdescribe it.
    const addButton = wrapper.find('[data-testid="share-add-button"]');
    expect(addButton.exists()).toBe(true);
    expect(addButton.attributes('disabled')).toBeDefined();
  });
});

describe('ShareConversationModal grantee refusal', () => {
  it('shows the grantee refusal and stops offering mutation after grantee_may_not_reshare', async () => {
    const { wrapper, fetchSpy } = await mountModal();

    // The add control is offered optimistically: no client-visible DTO says whether the caller
    // owns the conversation, so the only honest way to find out is to try.
    expect(wrapper.find('[data-testid="share-add-button"]').exists()).toBe(true);

    fetchSpy.mockResolvedValueOnce(
      jsonResponse({ error: 'forbidden', code: 'grantee_may_not_reshare', threadId: 'thread-1' }, 403)
    );
    await wrapper.find('[data-testid="share-subject-input"]').setValue('tid-1:oid-b');
    await wrapper.find('[data-testid="share-add-button"]').trigger('click');
    await flushPromises();

    // The refusal is SHOWN — the click must not silently no-op.
    const refusal = wrapper.find('[data-testid="share-refusal"]');
    expect(refusal.exists()).toBe(true);
    expect(refusal.text()).toContain('shared with you');

    // ...and the control that always fails is withdrawn rather than left as a trap.
    expect(wrapper.find('[data-testid="share-add-form"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="share-remove-tid-1:oid-a"]').exists()).toBe(false);

    // The roster the grantee is entitled to read stays on screen.
    expect(wrapper.find('[data-testid="share-row-tid-1:oid-a"]').exists()).toBe(true);
  });
});

describe('ShareConversationModal unknown_thread', () => {
  it('renders a non-committal message for a 404 unknown_thread and never confirms the conversation exists', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    fetchSpy.mockResolvedValueOnce(
      jsonResponse({ error: "Conversation 'thread-1' not found.", code: 'unknown_thread' }, 404)
    );
    const wrapper = mount(ShareConversationModal, {
      props: { threadId: 'thread-1' },
      attachTo: document.body,
    });
    await flushPromises();

    const refusal = wrapper.find('[data-testid="share-refusal"]');
    expect(refusal.exists()).toBe(true);
    expect(refusal.text()).toContain('not available');

    // A 404 here is either "no such conversation" or "you may not see this one", byte-identical by
    // design. Anything that reads as "it exists but you were refused" reopens the existence oracle.
    const text = wrapper.text().toLowerCase();
    expect(text).not.toContain('permission');
    expect(text).not.toContain('not allowed');
    expect(text).not.toContain('forbidden');
    expect(text).not.toContain('access denied');

    // Nothing to list, nothing to mutate.
    expect(wrapper.find('[data-testid="share-add-form"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="share-list"]').exists()).toBe(false);
  });
});
