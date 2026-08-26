import { ref, computed } from 'vue';
import type { ConversationSummary } from '@/types/conversations';
import {
  listConversations as apiListConversations,
  deleteConversation as apiDeleteConversation,
  provisionConversation,
  updateConversationMetadata,
  type ProvisionConversationRequest,
} from '@/api/conversationsApi';

/**
 * Composable for managing the conversation list.
 */
export function useConversations() {
  const conversations = ref<ConversationSummary[]>([]);
  const currentThreadId = ref<string | null>(null);
  const isLoading = ref(false);
  const error = ref<string | null>(null);

  /**
   * Loads the list of conversations from the backend.
   */
  async function loadConversations(): Promise<void> {
    isLoading.value = true;
    error.value = null;
    try {
      const fetched = await apiListConversations();
      // Merge rather than overwrite: this fetch is kicked off once, on mount, and can still be
      // in flight when the user's first send in a brand-new thread synchronously calls
      // addOrUpdateConversation() below. That new conversation has not been persisted to the
      // backend yet, so it is legitimately absent from this fetch's result. Blindly replacing
      // conversations.value would silently discard it if this fetch resolves after the local
      // add — keep any such local-only entries (newest first, ahead of the fetched list).
      const fetchedIds = new Set(fetched.map((c) => c.threadId));
      const localOnly = conversations.value.filter((c) => !fetchedIds.has(c.threadId));
      conversations.value = [...localOnly, ...fetched];
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to load conversations';
      console.error('Failed to load conversations:', e);
    } finally {
      isLoading.value = false;
    }
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
   */
  function addOrUpdateConversation(summary: ConversationSummary): void {
    const existingIndex = conversations.value.findIndex(
      (c) => c.threadId === summary.threadId
    );
    if (existingIndex >= 0) {
      // Update existing
      conversations.value[existingIndex] = summary;
    } else {
      // Add new at the beginning
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
    error,
    loadConversations,
    createNewConversation,
    selectConversation,
    removeConversation,
    addOrUpdateConversation,
    updateMetadata,
  };
}
