<script setup lang="ts">
import { computed, ref, onMounted, onBeforeUnmount, provide } from 'vue';
import { useConversations } from '@/composables/useConversations';
import { useChat, getDisplayText } from '@/composables/useChat';
import { useChatModes } from '@/composables/useChatModes';
import { useProviders } from '@/composables/useProviders';
import { useWorkspaces } from '@/composables/useWorkspaces';
import { updateConversationMetadata } from '@/api/conversationsApi';
import type { ChatModeCreateUpdate } from '@/types/chatMode';
import type { WorkspaceCreate, WorkspaceUpdate } from '@/types/workspace';
import ConversationSidebar from './ConversationSidebar.vue';
import MessageList from './MessageList.vue';
import PendingMessageQueue from './PendingMessageQueue.vue';
import ChatInput from './ChatInput.vue';
import SubAgentListPanel from './SubAgentListPanel.vue';
import ModeSelector from './ModeSelector.vue';
import ProviderSelector from './ProviderSelector.vue';
import WorkspaceSelector from './WorkspaceSelector.vue';
import AuthRequiredBanner from './AuthRequiredBanner.vue';
import MarketplaceModal from './MarketplaceModal.vue';

const {
  conversations,
  currentThreadId,
  isLoading: conversationsLoading,
  loadConversations,
  createNewConversation,
  selectConversation,
  removeConversation,
  addOrUpdateConversation,
} = useConversations();

// Initialize chat modes first (need currentModeId for useChat)
const {
  modes,
  currentModeId,
  availableTools,
  isLoading: modesLoading,
  loadModes,
  loadTools,
  selectMode,
  switchMode,
  createMode,
  updateMode,
  deleteMode,
  copyMode,
} = useChatModes();

// Provider catalog + per-process selection for new conversations.
const {
  providers,
  selectedProviderId,
  isLoading: providersLoading,
  loadProviders,
  selectProvider,
  switchProvider,
} = useProviders();

// Workspace catalog + per-process selection for new conversations.
const {
  workspaces,
  selectedWorkspaceId,
  isLoading: workspacesLoading,
  loadWorkspaces,
  selectWorkspace,
  createWorkspace,
  updateWorkspace,
} = useWorkspaces();

const workspaceSelectorRef = ref<InstanceType<typeof WorkspaceSelector> | null>(null);

// Initialize chat with getters for the current mode and provider ids.
const {
  displayItems,
  isLoading: chatLoading,
  isSending,
  error,
  cumulativeUsage,
  pendingMessages,
  pendingAuthRequests,
  dismissAuthRequest,
  sendMessage,
  clearMessages,
  cancelStream,
  disconnectWebSocket,
  setThreadId,
  loadMessagesFromBackend,
  resumeStreamIfActive,
  markStreamIdle,
  markStreamLoading,
  getResultForToolCall,
  threadId: chatThreadId,
} = useChat({
  getModeId: () => currentModeId.value,
  getProviderId: () => selectedProviderId.value,
  getWorkspaceId: () => selectedWorkspaceId.value,
});

async function handleCancel(): Promise<void> {
  await cancelStream();
}

// Provide getResultForToolCall to child components
provide('getResultForToolCall', getResultForToolCall);

const sidebarCollapsed = ref(false);
const isSwitchingMode = ref(false);
const isSwitchingProvider = ref(false);
const marketplaceModalOpen = ref(false);
const modeSwitchDisabled = computed(
  () => modesLoading.value || chatLoading.value || isSending.value || isSwitchingMode.value
);

/**
 * The provider selector is editable while the conversation is idle and locked ONLY while a run is
 * streaming (mirrors mode). A brand-new, messageless thread applies the pick locally; a started
 * conversation switches the backend provider (which recreates the agent). There is no permanent
 * per-thread lock — provider is mutable once the run completes.
 */
const providerSelectorDisabled = computed(
  () => providersLoading.value || chatLoading.value || isSending.value || isSwitchingProvider.value
);

async function handleSelectProvider(providerId: string): Promise<void> {
  if (providerSelectorDisabled.value) {
    return;
  }

  // Mirror handleSelectMode: only switch on the backend once the conversation has actually started
  // (has a sidebar entry). A messageless thread just records the pick locally for the first send.
  const started =
    !!currentThreadId.value &&
    conversations.value.some((c) => c.threadId === currentThreadId.value);

  if (started) {
    isSwitchingProvider.value = true;
    try {
      await disconnectWebSocket();
      await switchProvider(currentThreadId.value!, providerId);
      // Reflect the switched-to provider in the sidebar summary so the Bug-3 restore path
      // (restoreBindingsFromConversation on select / refresh) shows the new provider.
      const existing = conversations.value.find((c) => c.threadId === currentThreadId.value);
      if (existing) {
        addOrUpdateConversation({ ...existing, provider: providerId });
      }
    } catch (e) {
      console.error('Failed to switch provider:', e);
    } finally {
      isSwitchingProvider.value = false;
    }
  } else {
    // Messageless thread: defer agent creation to the first send.
    selectProvider(providerId);
  }
}

/**
 * Workspace id locked to the current thread, derived from the conversation
 * summary (mirrors lockedProviderId). New conversations have no sidebar entry
 * yet, so this resolves to null and the dropdown stays editable.
 */
const lockedWorkspaceId = computed<string | null>(() => {
  if (!currentThreadId.value) return null;
  const conversation = conversations.value.find((c) => c.threadId === currentThreadId.value);
  return conversation?.workspace ?? null;
});

const workspaceSelectorDisabled = computed(
  () => workspacesLoading.value || chatLoading.value || isSending.value || isSwitchingMode.value
);

function handleSelectWorkspace(workspaceId: string): void {
  if (workspaceSelectorDisabled.value || lockedWorkspaceId.value) {
    return;
  }
  selectWorkspace(workspaceId);
}

async function handleCreateWorkspace(data: WorkspaceCreate): Promise<void> {
  try {
    await createWorkspace(data);
    workspaceSelectorRef.value?.closeForm();
  } catch (e) {
    const message = e instanceof Error ? e.message : 'Failed to create workspace';
    workspaceSelectorRef.value?.showFormError(message);
  }
}

async function handleUpdateWorkspace(workspaceId: string, data: WorkspaceUpdate): Promise<void> {
  try {
    await updateWorkspace(workspaceId, data);
    workspaceSelectorRef.value?.closeForm();
  } catch (e) {
    const message = e instanceof Error ? e.message : 'Failed to update workspace';
    workspaceSelectorRef.value?.showFormError(message);
  }
}

// A conversation requested via ?threadId= that isn't in the backend's conversation list (never
// provisioned, or deleted). Drives the not-found panel below; cleared whenever the user picks a
// real conversation or starts a new chat.
const notFoundThreadId = ref<string | null>(null);

/**
 * Reads the ?threadId= deep-link query param, mirroring the ?record= convention already used by
 * useChat's isRecordingEnabledFromPageQuery (plain URLSearchParams, no router in this app).
 */
function getDeepLinkThreadIdFromPageQuery(): string | null {
  const value = new URLSearchParams(window.location.search).get('threadId');
  return value && value.trim().length > 0 ? value : null;
}

// Load conversations and modes on mount
onMounted(async () => {
  // Load modes, tools, and providers in parallel with conversations
  await Promise.all([
    loadConversations(),
    loadModes(),
    loadTools(),
    loadProviders(),
    loadWorkspaces(),
  ]);

  // A ?threadId= deep link takes priority over the "select most recent" default below — it's an
  // explicit navigation to one conversation, so an unknown id should surface as not-found rather
  // than silently falling back to the most recent conversation.
  const deepLinkThreadId = getDeepLinkThreadIdFromPageQuery();
  if (deepLinkThreadId) {
    const exists = conversations.value.some((c) => c.threadId === deepLinkThreadId);
    if (exists) {
      await handleSelectConversation(deepLinkThreadId);
    } else {
      notFoundThreadId.value = deepLinkThreadId;
    }
    return;
  }

  // If there are existing conversations, select the most recent one
  if (conversations.value.length > 0) {
    await handleSelectConversation(conversations.value[0].threadId);
  }
});

// Handle creating a new chat
async function handleNewChat(): Promise<void> {
  notFoundThreadId.value = null;

  // Disconnect current WebSocket and clear state
  await disconnectWebSocket();
  await clearMessages();
  // A fresh chat is always idle — return the Send/Stop control to "Send" if we came from a
  // streaming conversation (clearMessages no longer lowers the flags to avoid a switch-back
  // flicker; see useChat.markStreamIdle).
  markStreamIdle();

  // Create new thread (without adding to sidebar yet)
  const newThreadId = createNewConversation();
  setThreadId(newThreadId);
}

// Handle selecting an existing conversation
async function handleSelectConversation(threadId: string): Promise<void> {
  notFoundThreadId.value = null;
  if (threadId === currentThreadId.value) return;

  // Disconnect current WebSocket and clear state
  await disconnectWebSocket();
  await clearMessages();

  // Switch to selected conversation
  selectConversation(threadId);
  setThreadId(threadId);

  // Restore the conversation's bound provider/mode/workspace so opening (or refreshing into) a
  // conversation shows its actual bindings instead of the process defaults. Without this, a refresh
  // reset the selectors to Anthropic / General Assistant even for a still-streaming conversation.
  // Done BEFORE resumeStreamIfActive so the resumed WebSocket carries the correct mode/provider.
  restoreBindingsFromConversation(threadId);

  // Load existing messages
  try {
    // Keep the Send/Stop control on "Stop" while we load + probe run state, so switching back into a
    // still-streaming conversation stays continuously "streaming" (no flash to "Send" during the
    // awaited load). resumeStreamIfActive resolves it: it keeps this raised for an in-flight run, or
    // lowers it via markStreamIdle for an idle target.
    markStreamLoading();
    await loadMessagesFromBackend(threadId);
    // If a run is still streaming on the backend (the pooled agent keeps running after we
    // disconnected on switch/refresh), re-open the WebSocket to resume the live stream instead
    // of leaving the partial frozen.
    await resumeStreamIfActive(threadId);
  } catch (e) {
    console.error('Failed to load messages:', e);
    // A load/resume failure must not strand the UI on "Stop" forever.
    markStreamIdle();
  }
}

/**
 * Reflects a conversation's persisted provider/mode/workspace into the header selectors. Uses the
 * local selectors (not the backend switch endpoints) — this only restores what the conversation is
 * already bound to; it does not change the conversation. Unknown ids are ignored (selectProvider /
 * selectWorkspace no-op them; an unknown mode simply leaves the current one).
 */
function restoreBindingsFromConversation(threadId: string): void {
  const conversation = conversations.value.find((c) => c.threadId === threadId);
  if (!conversation) return;
  if (conversation.provider) {
    selectProvider(conversation.provider);
  }
  if (conversation.workspace) {
    selectWorkspace(conversation.workspace);
  }
  if (conversation.mode) {
    selectMode(conversation.mode);
  }
}

// Handle deleting a conversation
async function handleDeleteConversation(threadId: string): Promise<void> {
  try {
    await removeConversation(threadId);

    if (threadId === currentThreadId.value) {
      // If we deleted the current conversation, start a new one or select another
      if (conversations.value.length > 0) {
        await handleSelectConversation(conversations.value[0].threadId);
      } else {
        handleNewChat();
      }
    }
  } catch (e) {
    console.error('Failed to delete conversation:', e);
  }
}

// Handle selecting a mode
async function handleSelectMode(modeId: string): Promise<void> {
  if (modeSwitchDisabled.value) {
    return;
  }

  // Only switch on the backend once the conversation has actually started (has a
  // sidebar entry / first message sent). For a brand-new, messageless thread —
  // even though handleNewChat has already assigned a threadId — apply the mode
  // locally like provider and workspace. Otherwise the backend RecreateAgentForModeSwitch
  // would pre-create the agent and bind its provider/workspace to defaults, so a
  // workspace picked before the first message would be silently ignored.
  const started =
    !!currentThreadId.value &&
    conversations.value.some((c) => c.threadId === currentThreadId.value);

  if (started) {
    isSwitchingMode.value = true;
    try {
      await disconnectWebSocket();
      await switchMode(currentThreadId.value!, modeId);
    } catch (e) {
      console.error('Failed to switch mode:', e);
    } finally {
      isSwitchingMode.value = false;
    }
  } else {
    // Messageless thread: defer agent creation to the first send.
    selectMode(modeId);
  }
}

// Handle creating a new mode
async function handleCreateMode(data: ChatModeCreateUpdate): Promise<void> {
  try {
    await createMode(data);
  } catch (e) {
    console.error('Failed to create mode:', e);
  }
}

// Handle updating a mode
async function handleUpdateMode(modeId: string, data: ChatModeCreateUpdate): Promise<void> {
  try {
    await updateMode(modeId, data);
  } catch (e) {
    console.error('Failed to update mode:', e);
  }
}

// Handle deleting a mode
async function handleDeleteMode(modeId: string): Promise<void> {
  try {
    await deleteMode(modeId);
  } catch (e) {
    console.error('Failed to delete mode:', e);
  }
}

// Handle copying a mode
async function handleCopyMode(modeId: string, newName: string): Promise<void> {
  try {
    await copyMode(modeId, newName);
  } catch (e) {
    console.error('Failed to copy mode:', e);
  }
}

// Handle sending a message
async function handleSend(text: string): Promise<void> {
  const isNewConversation = !conversations.value.find(
    (c) => c.threadId === currentThreadId.value
  );

  await sendMessage(text);

  // If this is a new conversation (first message), add it to the sidebar
  if (isNewConversation && currentThreadId.value) {
    const displayText = getDisplayText(text);
    const title = displayText.substring(0, 50);
    const preview = displayText.substring(0, 100);

    // Add to local sidebar immediately. Reflect the provider that was used for the
    // first connect so the dropdown locks to a badge without waiting for a refetch.
    addOrUpdateConversation({
      threadId: currentThreadId.value,
      title,
      preview,
      lastUpdated: Date.now(),
      provider: selectedProviderId.value,
      workspace: selectedWorkspaceId.value,
      mode: currentModeId.value,
    });

    // Update backend metadata asynchronously
    try {
      console.log('[ChatLayout] Calling updateConversationMetadata', { threadId: currentThreadId.value, title, preview });
      await updateConversationMetadata(currentThreadId.value, { title, preview });
      console.log('[ChatLayout] Metadata updated successfully');
    } catch (e) {
      console.error('Failed to update conversation metadata:', e);
    }
  }
}

// Handle toggling sidebar collapse
function handleToggleCollapse(): void {
  sidebarCollapsed.value = !sidebarCollapsed.value;
}

// Watch for mobile screen and auto-collapse
function checkMobile(): void {
  if (window.innerWidth <= 768) {
    sidebarCollapsed.value = true;
  }
}

onMounted(() => {
  checkMobile();
  window.addEventListener('resize', checkMobile);
});

onBeforeUnmount(() => {
  window.removeEventListener('resize', checkMobile);
});
</script>

<template>
  <div class="chat-layout" data-testid="chat-layout">
    <ConversationSidebar
      :conversations="conversations"
      :current-thread-id="currentThreadId"
      :is-loading="conversationsLoading"
      :is-collapsed="sidebarCollapsed"
      @new-chat="handleNewChat"
      @select-conversation="handleSelectConversation"
      @delete-conversation="handleDeleteConversation"
      @toggle-collapse="handleToggleCollapse"
    />

    <main class="chat-main">
      <div v-if="notFoundThreadId" class="chat-view not-found-view" data-testid="conversation-not-found">
        <button
          v-if="sidebarCollapsed"
          class="menu-btn not-found-menu-btn"
          @click="handleToggleCollapse"
          title="Open sidebar"
        >
          =
        </button>
        <div class="not-found-content">
          <h2>Conversation not found</h2>
          <p>The conversation "{{ notFoundThreadId }}" does not exist or is no longer available.</p>
          <button class="new-chat-btn" @click="handleNewChat">Start a new chat</button>
        </div>
      </div>
      <div v-else class="chat-view">
        <header class="chat-header">
          <button
            v-if="sidebarCollapsed"
            class="menu-btn"
            @click="handleToggleCollapse"
            title="Open sidebar"
          >
            =
          </button>
          <h1>LmStreaming Chat</h1>
          <div class="header-actions">
            <WorkspaceSelector
              ref="workspaceSelectorRef"
              :workspaces="workspaces"
              :selected-workspace-id="selectedWorkspaceId"
              :locked-workspace-id="lockedWorkspaceId"
              :is-loading="workspacesLoading"
              :disabled="workspaceSelectorDisabled"
              @select-workspace="handleSelectWorkspace"
              @create-workspace="handleCreateWorkspace"
              @update-workspace="handleUpdateWorkspace"
            />
            <ProviderSelector
              :providers="providers"
              :selected-provider-id="selectedProviderId"
              :is-loading="providersLoading"
              :disabled="providerSelectorDisabled"
              @select-provider="handleSelectProvider"
            />
            <ModeSelector
              :modes="modes"
              :current-mode-id="currentModeId"
              :tools="availableTools"
              :is-loading="modesLoading"
              :disabled="modeSwitchDisabled"
              @select-mode="handleSelectMode"
              @create-mode="handleCreateMode"
              @update-mode="handleUpdateMode"
              @delete-mode="handleDeleteMode"
              @copy-mode="handleCopyMode"
            />
            <button
              class="marketplace-btn"
              data-testid="marketplace-button"
              title="Browse marketplaces"
              @click="marketplaceModalOpen = true"
            >
              Marketplaces
            </button>
            <button
              class="clear-btn"
              data-testid="clear-button"
              @click="clearMessages"
              :disabled="chatLoading"
            >
              Clear
            </button>
          </div>
        </header>

        <MarketplaceModal
          v-if="marketplaceModalOpen"
          @close="marketplaceModalOpen = false"
        />

        <MessageList :display-items="displayItems" :is-loading="chatLoading" />

        <AuthRequiredBanner :requests="pendingAuthRequests" @dismiss="dismissAuthRequest" />

        <div v-if="error" class="error-banner" data-testid="error-banner">
          {{ error }}
        </div>

        <div v-if="cumulativeUsage.totalTokens > 0" class="usage-banner">
          Total: {{ cumulativeUsage.totalTokens }} |
          In: {{ cumulativeUsage.uncachedInputTokens }} |
          Out: {{ cumulativeUsage.completionTokens }}
          <template v-if="cumulativeUsage.cachedTokens > 0">
            | Cached: {{ cumulativeUsage.cachedTokens }}
          </template>
          <template v-if="cumulativeUsage.cacheCreationTokens > 0">
            | Cache created: {{ cumulativeUsage.cacheCreationTokens }}
          </template>
        </div>

        <PendingMessageQueue :pending-messages="pendingMessages" />

        <ChatInput
          :disabled="isSending && !chatLoading"
          :streaming="chatLoading"
          @send="handleSend"
          @cancel="handleCancel"
        />
      </div>
    </main>

    <!-- Bind the sub-agent panel to the ACTIVE chat thread (useChat's threadId), not the
         sidebar's currentThreadId: a freshly-started chat runs on useChat's thread before it is
         ever selected/persisted in the sidebar, and the panel must track where sub-agents spawn. -->
    <SubAgentListPanel :parent-thread-id="chatThreadId" />
  </div>
</template>

<style scoped>
.chat-layout {
  display: flex;
  height: 100vh;
  overflow: hidden;
}

.chat-main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
}

.chat-view {
  display: flex;
  flex-direction: column;
  height: 100%;
  max-width: 900px;
  margin: 0 auto;
  width: 100%;
  background: #fff;
}

.chat-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px;
  border-bottom: 1px solid #e0e0e0;
  background: #f8f9fa;
  gap: 12px;
  /* Let the control row drop below the title (and its own buttons wrap) instead of
     overflowing the row — otherwise the trailing "Clear" button is clipped off the
     right edge on typical laptop widths, since the selectors + buttons are wider
     than the 900px content column. */
  flex-wrap: wrap;
}

.menu-btn {
  width: 32px;
  height: 32px;
  padding: 0;
  background: transparent;
  border: 1px solid #ccc;
  border-radius: 4px;
  cursor: pointer;
  font-size: 16px;
  color: #666;
  flex-shrink: 0;
}

.menu-btn:hover {
  background: #e9ecef;
}

.chat-header h1 {
  margin: 0;
  font-size: 20px;
  font-weight: 600;
  flex: 1;
}

.header-actions {
  display: flex;
  align-items: center;
  gap: 12px;
  /* Wrap the controls (right-aligned) rather than clipping them when the row is tight. */
  flex-wrap: wrap;
  justify-content: flex-end;
}

.marketplace-btn {
  padding: 8px 16px;
  background: #2d6cdf;
  color: white;
  border: none;
  border-radius: 6px;
  font-size: 14px;
  cursor: pointer;
  transition: background 0.2s;
}

.marketplace-btn:hover {
  background: #2057bd;
}

.clear-btn {
  padding: 8px 16px;
  background: #dc3545;
  color: white;
  border: none;
  border-radius: 6px;
  font-size: 14px;
  cursor: pointer;
  transition: background 0.2s;
}

.clear-btn:hover:not(:disabled) {
  background: #c82333;
}

.clear-btn:disabled {
  background: #ccc;
  cursor: not-allowed;
}

.error-banner {
  padding: 12px 16px;
  background: #f8d7da;
  color: #721c24;
  border-top: 1px solid #f5c6cb;
}

.usage-banner {
  padding: 8px 16px;
  background: #d4edda;
  color: #155724;
  border-top: 1px solid #c3e6cb;
  font-size: 13px;
}

.not-found-view {
  display: flex;
  align-items: center;
  justify-content: center;
  position: relative;
}

.not-found-menu-btn {
  position: absolute;
  top: 16px;
  left: 16px;
}

.not-found-content {
  text-align: center;
  padding: 24px;
  max-width: 400px;
}

.not-found-content h2 {
  margin: 0 0 8px;
  font-size: 20px;
}

.not-found-content p {
  color: #666;
  margin: 0 0 16px;
  word-break: break-word;
}

.new-chat-btn {
  padding: 8px 16px;
  background: #2d6cdf;
  color: white;
  border: none;
  border-radius: 6px;
  font-size: 14px;
  cursor: pointer;
}

.new-chat-btn:hover {
  background: #2057bd;
}
</style>
