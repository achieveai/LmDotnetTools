import { ref, computed } from 'vue';
import type { ConversationSortMode, ConversationSummary } from '@/types/conversations';
import { DEFAULT_CONVERSATION_SORT_MODE } from '@/types/conversations';
import {
  listConversations as apiListConversations,
  deleteConversation as apiDeleteConversation,
  provisionConversation,
  updateConversationMetadata,
  type ProvisionConversationRequest,
} from '@/api/conversationsApi';

/**
 * How many conversations one page request asks for. Also the exhaustion test: the list endpoint
 * returns a bare array with no `hasMore`, so a page shorter than this is by definition the last one.
 */
export const CONVERSATIONS_PAGE_SIZE = 30;

/** Where the chosen sort mode is remembered across reloads. */
export const SORT_MODE_STORAGE_KEY = 'lmstreaming.conversations.sortMode';

function isSortMode(value: unknown): value is ConversationSortMode {
  return value === 'lastUsed' || value === 'created';
}

/**
 * Reads the remembered sort mode, falling back to the default.
 *
 * Every access to `localStorage` here is wrapped: private-browsing modes and storage-disabled
 * profiles throw on read as well as on write, and a preference is never worth failing to boot over.
 */
function readStoredSortMode(): ConversationSortMode {
  try {
    const stored = localStorage.getItem(SORT_MODE_STORAGE_KEY);
    return isSortMode(stored) ? stored : DEFAULT_CONVERSATION_SORT_MODE;
  } catch {
    return DEFAULT_CONVERSATION_SORT_MODE;
  }
}

/** Remembers the sort mode, silently doing nothing where storage is unavailable. */
function writeStoredSortMode(mode: ConversationSortMode): void {
  try {
    localStorage.setItem(SORT_MODE_STORAGE_KEY, mode);
  } catch {
    // Storage unavailable (private browsing, disabled, quota) — the preference just won't survive
    // this session, which is strictly better than breaking the sort switch.
  }
}

/**
 * Composable for managing the conversation list.
 */
export function useConversations() {
  const conversations = ref<ConversationSummary[]>([]);
  const currentThreadId = ref<string | null>(null);
  const isLoading = ref(false);
  const isLoadingMore = ref(false);
  const error = ref<string | null>(null);
  const sortMode = ref<ConversationSortMode>(readStoredSortMode());
  /** False once a short page has proved there is nothing left to fetch. */
  const hasMoreConversations = ref(true);

  /**
   * How many rows the backend has handed us so far, which is the offset of the next page.
   *
   * Deliberately NOT `conversations.value.length`: the list can also hold local-only entries that
   * the backend has never returned (see the merge in loadConversations below), and counting those
   * into the offset would skip real rows.
   */
  let fetchedRowCount = 0;

  /**
   * Guards against two page requests being in flight at once. Shared by the first page and every
   * subsequent one, because a scroll that fires while the initial load is still running is the same
   * hazard as two scrolls firing together.
   */
  let pageInFlight = false;

  /**
   * Bumped on every reset (mount load, sort switch). A response whose generation no longer matches
   * belongs to a list the user has already replaced, so it is dropped rather than appended — this
   * is what stops pages ordered by two different sorts from merging.
   */
  let generation = 0;

  /**
   * Loads (or reloads) the first page, resetting paging state.
   */
  async function loadConversations(): Promise<void> {
    generation += 1;
    const requestGeneration = generation;
    fetchedRowCount = 0;
    hasMoreConversations.value = true;
    pageInFlight = true;
    isLoading.value = true;
    // A load-more already in flight belongs to the generation this reset just superseded, so its
    // `finally` will decline to clear this flag - the guard there only releases state it still
    // owns. Nothing else would ever lower it, and the sidebar would sit on "Loading more..."
    // forever. Clearing it here, where the reset happens, keeps the flag owned by the CURRENT
    // generation rather than by whichever request happens to finish last.
    isLoadingMore.value = false;
    error.value = null;
    try {
      const fetched = await apiListConversations(
        CONVERSATIONS_PAGE_SIZE,
        0,
        sortMode.value
      );
      if (requestGeneration !== generation) return;
      // Merge rather than overwrite: this fetch is kicked off once, on mount, and can still be
      // in flight when the user's first send in a brand-new thread synchronously calls
      // addOrUpdateConversation() below. That new conversation has not been persisted to the
      // backend yet, so it is legitimately absent from this fetch's result. Blindly replacing
      // conversations.value would silently discard it if this fetch resolves after the local
      // add — keep any such local-only entries (newest first, ahead of the fetched list).
      const fetchedIds = new Set(fetched.map((c) => c.threadId));
      const localOnly = conversations.value.filter((c) => !fetchedIds.has(c.threadId));
      conversations.value = [...localOnly, ...fetched];
      fetchedRowCount = fetched.length;
      hasMoreConversations.value = fetched.length === CONVERSATIONS_PAGE_SIZE;
    } catch (e) {
      if (requestGeneration !== generation) return;
      error.value = e instanceof Error ? e.message : 'Failed to load conversations';
      console.error('Failed to load conversations:', e);
      // Leave hasMoreConversations true so a later scroll can retry rather than the sidebar being
      // permanently stuck at whatever it managed to load.
    } finally {
      if (requestGeneration === generation) {
        pageInFlight = false;
        isLoading.value = false;
      }
    }
  }

  /**
   * Appends the next page of older conversations. Safe to call on every scroll event: it is a no-op
   * while a page is already in flight and once the list is known to be exhausted.
   */
  async function loadMoreConversations(): Promise<void> {
    if (pageInFlight || !hasMoreConversations.value) return;
    const requestGeneration = generation;
    pageInFlight = true;
    isLoadingMore.value = true;
    error.value = null;
    try {
      const page = await apiListConversations(
        CONVERSATIONS_PAGE_SIZE,
        fetchedRowCount,
        sortMode.value
      );
      if (requestGeneration !== generation) return;
      fetchedRowCount += page.length;
      hasMoreConversations.value = page.length === CONVERSATIONS_PAGE_SIZE;
      // Dedupe on append: a conversation added locally before it was persisted can come back in a
      // later page, and a row can shift across the page boundary while the user pages.
      const seen = new Set(conversations.value.map((c) => c.threadId));
      conversations.value = [
        ...conversations.value,
        ...page.filter((c) => !seen.has(c.threadId)),
      ];
    } catch (e) {
      if (requestGeneration !== generation) return;
      error.value = e instanceof Error ? e.message : 'Failed to load conversations';
      console.error('Failed to load more conversations:', e);
    } finally {
      if (requestGeneration === generation) {
        pageInFlight = false;
        isLoadingMore.value = false;
      }
    }
  }

  /**
   * Switches the list ordering and reloads it from the first page.
   *
   * The list is cleared first: pages fetched under the old sort cannot be concatenated with pages
   * fetched under the new one, so the only coherent result is a fresh list. A conversation added
   * locally *while* that refetch is in flight still survives, via the merge in loadConversations.
   */
  async function setSortMode(mode: ConversationSortMode): Promise<void> {
    if (mode === sortMode.value) return;
    sortMode.value = mode;
    writeStoredSortMode(mode);
    conversations.value = [];
    await loadConversations();
  }

  /**
   * Reserves a new conversation on the server and returns the thread id the SERVER minted (#435).
   *
   * This is the SPA's only source of thread ids. Under `Identity:Enforce=true` the `/ws` gate
   * authorizes the conversation before accepting the handshake, and a thread id with no metadata
   * row is refused byte-identically to one owned by somebody else — deliberately, since minting a
   * row for an unknown id would make unknown ids succeed while taken ones are refused, which is the
   * existence oracle that 404 exists to close. A locally invented id therefore cannot ever open a
   * socket once the flag is on.
   *
   * A failure is thrown, never swallowed: falling back to a local id would hand the caller a
   * conversation that looks started, connects while enforcement is off, and is refused the moment
   * it is flipped on.
   */
  async function createNewConversation(
    binding: ProvisionConversationRequest
  ): Promise<string> {
    const { threadId: newThreadId } = await provisionConversation(binding);
    currentThreadId.value = newThreadId;
    return newThreadId;
  }

  /**
   * Selects an existing conversation.
   */
  function selectConversation(threadId: string): void {
    currentThreadId.value = threadId;
  }

  /**
   * Removes a conversation from the list and backend.
   */
  async function removeConversation(threadId: string): Promise<void> {
    try {
      await apiDeleteConversation(threadId);
      conversations.value = conversations.value.filter((c) => c.threadId !== threadId);
      if (currentThreadId.value === threadId) {
        currentThreadId.value = null;
      }
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to delete conversation';
      console.error('Failed to delete conversation:', e);
      throw e;
    }
  }

  /**
   * Adds or updates a conversation in the list.
   * Called after the first message is sent in a new conversation.
   *
   * Under `lastUsed` the touched conversation moves back to the top — that is what "most recently
   * used" means, and updating it in place instead is why an active conversation used to sink down
   * the list as the session went on. Under `created` the position is left alone: that ordering is
   * about creation time, and it must not shuffle while the user works.
   */
  function addOrUpdateConversation(summary: ConversationSummary): void {
    const existingIndex = conversations.value.findIndex(
      (c) => c.threadId === summary.threadId
    );
    if (existingIndex >= 0) {
      if (sortMode.value === 'lastUsed') {
        // Move to the top (live re-sort).
        const next = [...conversations.value];
        next.splice(existingIndex, 1);
        next.unshift(summary);
        conversations.value = next;
      } else {
        // Update in place — `created` order is stable.
        conversations.value[existingIndex] = summary;
      }
    } else {
      // Add new at the beginning: newest by either measure.
      conversations.value.unshift(summary);
    }
  }

  /**
   * Updates conversation metadata on the backend.
   */
  async function updateMetadata(
    threadId: string,
    title: string,
    preview?: string
  ): Promise<void> {
    try {
      await updateConversationMetadata(threadId, { title, preview });
      // Update local state
      const conversation = conversations.value.find((c) => c.threadId === threadId);
      if (conversation) {
        conversation.title = title;
        if (preview !== undefined) {
          conversation.preview = preview;
        }
      }
    } catch (e) {
      console.error('Failed to update conversation metadata:', e);
      throw e;
    }
  }

  /**
   * The currently selected conversation.
   */
  const currentConversation = computed(() =>
    conversations.value.find((c) => c.threadId === currentThreadId.value)
  );

  return {
    conversations,
    currentThreadId,
    currentConversation,
    isLoading,
    isLoadingMore,
    hasMoreConversations,
    sortMode,
    error,
    loadConversations,
    loadMoreConversations,
    setSortMode,
    createNewConversation,
    selectConversation,
    removeConversation,
    addOrUpdateConversation,
    updateMetadata,
  };
}
