import { ref, computed } from 'vue';
import type { ConversationSummary } from '@/types/conversations';
import {
  listConversations as apiListConversations,
  deleteConversation as apiDeleteConversation,
  updateConversationMetadata,
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
      conversations.value = await apiListConversations();
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to load conversations';
      console.error('Failed to load conversations:', e);
    } finally {
      isLoading.value = false;
    }
  }

  /**
   * Creates a new conversation and returns its thread ID.
   */
  function createNewConversation(): string {
    const newThreadId = `thread-${Date.now()}-${Math.random().toString(36).substring(2, 9)}`;
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
