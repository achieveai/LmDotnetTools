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

// #445 item 6. The Add control is live while the initial roster GET is still in flight (it sits
// outside the loading/list v-if pair, and deliberately so — see the component's header comment). So
// a grant can be added, and the roster re-read, before the FIRST read has answered. Whichever read
// resolves last used to win: the initial one, answering from before the grant existed, would land
// second and quietly erase the row the user just created.
describe('ShareConversationModal stale load race (#445)', () => {
  it('keeps the newer roster when the initial GET resolves after an add', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');

    // The initial GET, held open. It answers with the roster from BEFORE the add.
    let resolveInitial: (response: Response) => void = () => {};
    fetchSpy.mockReturnValueOnce(
      new Promise<Response>((resolve) => {
        resolveInitial = resolve;
      })
    );

    const wrapper = mount(ShareConversationModal, {
      props: { threadId: 'thread-1' },
      attachTo: document.body,
    });
    await flushPromises();

    const added: ConversationShare = { ...viewerGrant, subjectId: 'tid-1:oid-b', role: 'editor' };
    fetchSpy.mockResolvedValueOnce(jsonResponse(added));
    fetchSpy.mockResolvedValueOnce(jsonResponse([added]));

    await wrapper.find('[data-testid="share-subject-input"]').setValue('tid-1:oid-b');
    await wrapper.find('[data-testid="share-add-button"]').trigger('click');
    await flushPromises();

    // Only now does the read that was started first come back — with the pre-add roster.
    resolveInitial(jsonResponse([]));
    await flushPromises();

    expect(wrapper.find('[data-testid="share-row-tid-1:oid-b"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="share-empty"]').exists()).toBe(false);
  });
});

// #445 item 7. The modal is mounted per conversation today, but nothing in it says so: it reads
// `props.threadId` once, on mount. If it is ever kept alive across a conversation switch it goes on
// showing the previous conversation's roster — and, worse, its latched "you may not change this"
// state, which is a fact about the caller's rights on the OLD thread.
describe('ShareConversationModal threadId changes (#445)', () => {
  it('re-reads the roster for the new thread', async () => {
    const { wrapper, fetchSpy } = await mountModal([viewerGrant]);

    const otherGrant: ConversationShare = {
      ...viewerGrant,
      threadId: 'thread-2',
      subjectId: 'tid-1:oid-z',
    };
    fetchSpy.mockResolvedValueOnce(jsonResponse([otherGrant]));

    await wrapper.setProps({ threadId: 'thread-2' });
    await flushPromises();

    expect(fetchSpy).toHaveBeenLastCalledWith('/api/conversations/thread-2/shares');
    expect(wrapper.find('[data-testid="share-row-tid-1:oid-z"]').exists()).toBe(true);
    // The previous conversation's roster must not linger under the new conversation's name.
    expect(wrapper.find('[data-testid="share-row-tid-1:oid-a"]').exists()).toBe(false);
  });

  it('offers mutation again on the new thread after the previous one refused it', async () => {
    const { wrapper, fetchSpy } = await mountModal([viewerGrant]);

    fetchSpy.mockResolvedValueOnce(
      jsonResponse({ error: 'forbidden', code: 'grantee_may_not_reshare', threadId: 'thread-1' }, 403)
    );
    await wrapper.find('[data-testid="share-subject-input"]').setValue('tid-1:oid-b');
    await wrapper.find('[data-testid="share-add-button"]').trigger('click');
    await flushPromises();
    expect(wrapper.find('[data-testid="share-add-form"]').exists()).toBe(false);

    fetchSpy.mockResolvedValueOnce(jsonResponse([]));
    await wrapper.setProps({ threadId: 'thread-2' });
    await flushPromises();

    // "You were shared this one" was true of thread-1. Carrying it into thread-2 would withhold a
    // control the caller may well own there.
    expect(wrapper.find('[data-testid="share-add-form"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="share-refusal"]').exists()).toBe(false);
  });

  it('drops an unknown_thread verdict when the thread changes', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    fetchSpy.mockResolvedValueOnce(
      jsonResponse({ error: "Conversation 'thread-1' not found.", code: 'unknown_thread' }, 404)
    );
    const wrapper = mount(ShareConversationModal, {
      props: { threadId: 'thread-1' },
      attachTo: document.body,
    });
    await flushPromises();
    expect(wrapper.find('[data-testid="share-list"]').exists()).toBe(false);

    fetchSpy.mockResolvedValueOnce(jsonResponse([viewerGrant]));
    await wrapper.setProps({ threadId: 'thread-2' });
    await flushPromises();

    expect(wrapper.find('[data-testid="share-list"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="share-row-tid-1:oid-a"]').exists()).toBe(true);
  });
});

// #445 item 8. Five codes latch the control away, and only one of them was covered. They are one
// rule with five inputs, so they are tested as one rule with five inputs — a code added to the set
// and not to this list is the failure mode, and `it.each` over the set makes that visible.
describe('ShareConversationModal withdraws mutation for every refusal that will always fail', () => {
  const cases: Array<[string, string]> = [
    ['grantee_may_not_reshare', 'shared with you'],
    ['admin_may_not_reshare', 'administrator'],
    ['app_cannot_share', 'application identity'],
    ['publication_supersedes_sharing', 'published to the whole tenant'],
    ['tenant_member_read_only', 'not change who it is shared with'],
  ];

  it.each(cases)('withdraws the controls after %s', async (code, expectedText) => {
    const { wrapper, fetchSpy } = await mountModal();

    // The add control is offered optimistically: no client-visible DTO says whether the caller owns
    // the conversation, so the only honest way to find out is to try.
    expect(wrapper.find('[data-testid="share-add-button"]').exists()).toBe(true);

    fetchSpy.mockResolvedValueOnce(
      jsonResponse({ error: 'forbidden', code, threadId: 'thread-1' }, 403)
    );
    await wrapper.find('[data-testid="share-subject-input"]').setValue('tid-1:oid-b');
    await wrapper.find('[data-testid="share-add-button"]').trigger('click');
    await flushPromises();

    const refusal = wrapper.find('[data-testid="share-refusal"]');
    expect(refusal.exists()).toBe(true);
    expect(refusal.text()).toContain(expectedText);

    // Withdrawn, not disabled: a disabled button still claims the action is the caller's.
    expect(wrapper.find('[data-testid="share-add-form"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="share-remove-tid-1:oid-a"]').exists()).toBe(false);

    // ...and the roster the caller IS entitled to read stays on screen.
    expect(wrapper.find('[data-testid="share-row-tid-1:oid-a"]').exists()).toBe(true);
  });
});
