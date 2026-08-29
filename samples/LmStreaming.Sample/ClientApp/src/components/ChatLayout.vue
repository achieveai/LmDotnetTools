<script setup lang="ts">
import { computed, ref, nextTick, onMounted, onBeforeUnmount, provide } from 'vue';
import { useConversations } from '@/composables/useConversations';
import { useChat, getDisplayText } from '@/composables/useChat';
import { useChatModes } from '@/composables/useChatModes';
import { useProviders } from '@/composables/useProviders';
import { DEFAULT_WORKSPACE_ID, useWorkspaces } from '@/composables/useWorkspaces';
import { egressDialogRequest, closeEgressDialog } from '@/composables/useEgressAuth';
import { conversationExists, updateConversationMetadata } from '@/api/conversationsApi';
import { WorkspaceRevisionConflictError } from '@/api/workspacesApi';
import type { ChatModeCreateUpdate } from '@/types/chatMode';
import type { WorkspaceCreate, WorkspaceUpdate } from '@/types/workspace';
import ConversationSidebar from './ConversationSidebar.vue';
import MessageList from './MessageList.vue';
import PendingMessageQueue from './PendingMessageQueue.vue';
import ChatInput from './ChatInput.vue';
import PendingQuestionDock from './PendingQuestionDock.vue';
import SubAgentListPanel from './SubAgentListPanel.vue';
import TodoBoardPanel from './TodoBoardPanel.vue';
import ConversationTabs from './ConversationTabs.vue';
import SubAgentTranscript from './SubAgentTranscript.vue';
import { useSubAgentPanel } from '@/composables/useSubAgentPanel';
import { useTodoBoard } from '@/composables/useTodoBoard';
import { useConversationTabs, GO_TO_AGENT_TAB } from '@/composables/useConversationTabs';
import {
  GET_AGENT_COLOR,
  GET_AGENT_ROUTING,
  resolveAgentRoutingFromCall,
  type AgentRoutingLookup,
} from '@/utils/agentColors';
import { SUBMIT_CLIENT_TOOL_RESULT } from '@/composables/useClientToolSubmit';
import ModeSelector from './ModeSelector.vue';
import ProviderSelector from './ProviderSelector.vue';
import WorkspaceSelector from './WorkspaceSelector.vue';
import AuthRequiredBanner from './AuthRequiredBanner.vue';
import MarketplaceModal from './MarketplaceModal.vue';
import EgressAuthModal from './EgressAuthModal.vue';
import FileBrowserModal from './FileBrowserModal.vue';
import ShareConversationModal from './ShareConversationModal.vue';

const {
  conversations,
  currentThreadId,
  currentConversation,
  isLoading: conversationsLoading,
  isLoadingMore: conversationsLoadingMore,
  sortMode: conversationSortMode,
  loadConversations,
  loadMoreConversations,
  setSortMode: setConversationSortMode,
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
  settleCatalog: settleProviderCatalog,
  selectProvider,
  switchProvider,
} = useProviders();

// Workspace catalog + per-process selection for new conversations.
const {
  workspaces,
  gateway: workspaceGateway,
  selectedWorkspaceId,
  isLoading: workspacesLoading,
  loadWorkspaces,
  settleCatalog: settleWorkspaceCatalog,
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
  cumulativeCost,
  conversationTodo,
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
  hasPendingClientQuestion,
  submitClientToolResult,
  threadId: chatThreadId,
} = useChat({
  getModeId: () => currentModeId.value,
  getProviderId: () => selectedProviderId.value,
  getWorkspaceId: () => selectedWorkspaceId.value,
  provisionThreadId: provisionThread,
});

/**
 * The SPA's single source of thread ids (#435): reserve the conversation on the server and use the
 * id it minted, so nothing in the client can produce an id of its own.
 *
 * Under `Identity:Enforce=true` the `/ws` gate refuses a thread id with no metadata row, and refuses
 * it byte-identically to one owned by somebody else. That refusal is correct, and it means the row
 * has to exist before the socket opens.
 *
 * `useChat` calls this the first time a send needs an id — which is also the first moment a
 * conversation is real. "New chat" deliberately does NOT call it: see `handleNewChat`.
 */
async function provisionThread(): Promise<string> {
  // Both catalogs are fetched on mount, but the composer is interactive from the first paint — a
  // send can and does beat the responses. Reading the selections straight away would find
  // `selectedProviderId` still null and refuse a conversation that has nothing wrong with it, so
  // wait for whichever load will win before deciding anything is missing.
  await Promise.all([settleProviderCatalog(), settleWorkspaceCatalog()]);

  const providerId = selectedProviderId.value;
  if (providerId === null) {
    // The server resolves the provider and answers 503 for one it cannot serve, so there is nothing
    // useful to send yet. Say what is missing instead of posting a doomed request. A null here means
    // the provider catalog could not be read at all: `loadProviders` falls back to the backend's
    // declared default even when nothing in the list is available.
    throw new Error('Choose a provider before starting a conversation.');
  }

  // A null workspace selection is NOT a reason to refuse. It means the catalog listed nothing this
  // client could choose — an empty list, or one whose every entry the gateway checked and refused.
  // Refusing there would make provisioning stricter than the socket it replaced: that path sent
  // whatever it had and let the server resolve its own default. Fall back to the workspace the
  // backend always resolves.
  //
  // An UNREADABLE catalog no longer arrives here (#459). It used to: every workspace came back
  // `unknown`, `useWorkspaces` kept only `compatible` ones, so a gateway-less host reached this line
  // on every send. Now such rows report `unavailable`, stay selectable, and the user's own choice
  // survives — which matters because this fallback WRITES: the id below is persisted as the
  // conversation's workspace and is immutable afterwards, so substituting the default here for a
  // selection the user had already made would silently rewrite a binding, not just a default.
  const workspaceId = selectedWorkspaceId.value ?? DEFAULT_WORKSPACE_ID;
  return await createNewConversation({
    workspaceId,
    providerId,
    modeId: currentModeId.value,
  });
}

async function handleCancel(): Promise<void> {
  await cancelStream();
}

// Conversation-wide cost for the usage banner (#196). Prefers a provider-reported figure over the public
// estimate; renders null (no configured rate — e.g. flat-rate Copilot) as nothing rather than a bogus $0.
const usageCostDisplay = computed(() => {
  const c = cumulativeCost.value;
  const micros = c.providerReportedCostMicros ?? c.estimatedCostMicros;
  if (micros == null) return null;
  const amount = (micros / 1_000_000).toFixed(4);
  const prefix = c.currency === 'USD' ? '$' : `${c.currency} `;
  const label = c.providerReportedCostMicros != null ? 'Cost' : 'Est. cost';
  return `${label}: ${prefix}${amount}`;
});

// `chatThreadId` can be set well before the backend's agent pool has an entry for it — the first
// send reserves the thread and opens the socket in the same breath — so polling /subagents on it
// alone would 404-spam. Gate the sub-agent poll on the conversation having actually STARTED: it has rendered items
// (a message was sent or an existing conversation was loaded) OR it already has a sidebar entry. A
// fresh, empty New Chat matches neither, so the poll stays idle until the first message; every started
// conversation (including the E2E's scripted send) opens the gate so its sub-agent tabs surface.
const subAgentParentThreadId = computed(() =>
  chatThreadId.value &&
  (displayItems.value.length > 0 ||
    conversations.value.some((c) => c.threadId === chatThreadId.value))
    ? chatThreadId.value
    : null
);

// Sub-agent panel state is hoisted HERE (it used to live inside SubAgentListPanel) so the center-pane
// tabs and the right-side launcher share ONE instance/poller/socket. The tab selector/router drives
// which conversation the center pane shows. It is bound to subAgentParentThreadId (not the raw
// chatThreadId) so listSubAgents is never polled before the conversation has actually started.
const {
  children: subAgentChildren,
  focusedAgentId,
  focusedDisplayItems,
  isFocusedStreaming,
  error: subAgentError,
  startPolling: startSubAgentPolling,
  focusChild,
  unfocusChild,
  sendToFocusedChild,
  submitToFocusedChild,
  getResultForToolCall: getSubAgentResultForToolCall,
} = useSubAgentPanel(() => subAgentParentThreadId.value);

// ToDo board state (#583), hoisted here for the same reason the sub-agent panel is: ONE instance,
// owned by the layout, handed to a stateless panel. It reuses `subAgentParentThreadId` — despite the
// name, that computed is simply "the thread id once the conversation has actually started", which is
// exactly the gate the board wants too: a fresh, unsent New Chat has no board to fetch.
const { tasks: todoTasks, hasBoard: hasTodoBoard } = useTodoBoard(
  () => subAgentParentThreadId.value,
  () => conversationTodo.value
);

const { activeTabId, tabs, selectTab, getAgentColor } = useConversationTabs({
  children: subAgentChildren,
  focusedAgentId,
  focusChild,
  unfocusChild,
  getParentThreadId: () => chatThreadId.value,
});

function handleSubAgentSend(text: string): void {
  sendToFocusedChild(text);
}

// Provide getResultForToolCall to the MAIN view's pills. The sub-agent view (SubAgentTranscript)
// shadows this with the child's own resolver for its subtree.
provide('getResultForToolCall', getResultForToolCall);
// Provide the client-tool submit function (#246, e.g. AskUserQuestion) so a descendant question
// component can resolve a deferred tool call over the shared WebSocket without prop-drilling
// through MessageList/SubAgentTranscript.
provide(SUBMIT_CLIENT_TOOL_RESULT, submitClientToolResult);
// Provide the tab-navigation function (#246) so a client-notification pill (NotificationPill.vue)
// can jump the center pane straight to the reporting descendant's tab.
provide(GO_TO_AGENT_TAB, selectTab);
// Provide agentId → color so ToolPill (agent family) and NotificationPill (completion) can tint a
// sub-agent's inline calls to match its tab.
provide(GET_AGENT_COLOR, getAgentColor);
const getAgentRouting: AgentRoutingLookup = (parsedArgs, resultText) =>
  resolveAgentRoutingFromCall(parsedArgs, resultText, subAgentChildren.value);
provide(GET_AGENT_ROUTING, getAgentRouting);

const sidebarCollapsed = ref(false);
const isSwitchingMode = ref(false);
const isSwitchingProvider = ref(false);
const marketplaceModalOpen = ref(false);
const egressAuthModalOpen = ref(false);
const fileBrowserModalOpen = ref(false);
const shareModalOpen = ref(false);

/**
 * Closes the egress-auth modal, resetting both the header-button flag and any
 * programmatic open request (openEgressDialog).
 */
function handleCloseEgressModal(): void {
  egressAuthModalOpen.value = false;
  closeEgressDialog();
}
const modeSwitchDisabled = computed(
  () =>
    modesLoading.value ||
    chatLoading.value ||
    isSending.value ||
    isSwitchingMode.value ||
    hasPendingClientQuestion.value
);

/**
 * The provider selector is editable while the conversation is idle and locked ONLY while a run is
 * streaming (mirrors mode). A brand-new, messageless thread applies the pick locally; a started
 * conversation switches the backend provider (which recreates the agent). There is no permanent
 * per-thread lock — provider is mutable once the run completes.
 */
const providerSelectorDisabled = computed(
  () =>
    providersLoading.value ||
    chatLoading.value ||
    isSending.value ||
    isSwitchingProvider.value ||
    hasPendingClientQuestion.value
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

/**
 * TERMINAL reasons the workspace selector is unusable. `disabled` makes WorkspaceSelector tear its
 * dropdown down, so only conditions that will not reverse on their own belong here.
 *
 * `workspacesLoading` is deliberately NOT one of them, and the distinction is load-bearing: the
 * post-409 conflict path reloads the list, so the flag flips true and back WHILE the user's edit
 * form is open and this component is on its way to re-seed it and show the conflict message. Folding
 * it in here unmounted the form first, so `reseedEditForm()` bailed and `showFormError()` wrote to
 * nothing — the save silently failed with no visible error (F6). Blocking interaction during a
 * reload is still correct (the list is momentarily stale); that is what the separate `is-loading`
 * prop does, without the teardown.
 *
 * `gateway.available === false` is NOT one of them either, and removing it is the point of #459.
 * That flag says the marketplace CATALOG could not be read — nothing more. It is false in exactly
 * one situation: a gateway-less host, where it is the permanent answer. (A failed `/api/workspaces`
 * leaves `gateway` null, not false, so this never covered a broken list request; and a list with no
 * workspaces at all reports true.) Disabling on it therefore did not guard against anything — it
 * just made the picker inert on precisely the host whose rows the compatibility split now marks
 * selectable-but-unverified, so nothing downstream ever got asked. Choosing a workspace is safe
 * without a readable catalog; ACTING on one is what must fail closed, and that still happens
 * server-side (`ValidateForMutationAsync` / `ValidateForSessionAsync` both refuse on `Unavailable`)
 * with the error surfaced inline on the form.
 */
const workspaceSelectorDisabled = computed(
  () => chatLoading.value
    || isSending.value
    || isSwitchingMode.value
);

function handleSelectWorkspace(workspaceId: string): void {
  // `workspacesLoading` re-added explicitly: acting on a list that is mid-refresh is unsafe even
  // though it is not a teardown reason.
  if (workspaceSelectorDisabled.value || workspacesLoading.value || lockedWorkspaceId.value) {
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
    if (e instanceof WorkspaceRevisionConflictError) {
      // updateWorkspace has already re-listed, so the next save would carry a FRESH compare-and-swap
      // token while the form still held the pre-conflict selection — one more click would pass CAS
      // and silently overwrite whoever changed it. Re-seed the form from the refreshed workspace so
      // the pending change is dropped rather than the other writer's. `await nextTick()` first: the
      // refreshed list reaches the child as a prop only after the parent re-renders.
      await nextTick();
      workspaceSelectorRef.value?.reseedEditForm();
    }
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

/**
 * Reads the ?focus=1 query param (same URLSearchParams convention as the deep-link threadId and
 * ?record=). When set, the layout renders a read-focused single-conversation view — no left
 * sidebar and no header workspace/provider/mode pickers or action buttons — so a deep-link posted
 * on a PR opens straight into the review conversation + its sub-agent tabs, stripped of app chrome.
 * The value is fixed for the page load (query strings don't change without a navigation), so a
 * one-shot read is sufficient.
 */
const focusMode = computed(() => {
  const value = new URLSearchParams(window.location.search).get('focus');
  return value === '1' || value === 'true';
});

/**
 * The header line. Normally the static app name; in focus mode the deep-linked conversation's OWN
 * title, because focus mode hides the sidebar — the title bar is then the only thing telling a
 * reader which conversation they landed on (e.g. "Review PR #222 — Review Agent" for a link posted
 * on a PR). Falls back to the app name while the conversation list is still loading or when the
 * conversation carries no title.
 */
const headerTitle = computed(() => {
  const appName = 'LmStreaming Chat';
  if (!focusMode.value) return appName;
  const conversation = conversations.value.find((c) => c.threadId === currentThreadId.value);
  return conversation?.title?.trim() || appName;
});

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
    // Only the FIRST page is loaded here, so absence from the sidebar means "older than page one",
    // not "does not exist" - and a deep link is most often to an older conversation, which is the
    // case this screen used to report as not-found. Membership stays as a fast path for a link into
    // a conversation already on screen; anything else is resolved against the server.
    const exists =
      conversations.value.some((c) => c.threadId === deepLinkThreadId) ||
      (await conversationExists(deepLinkThreadId));
    if (exists) {
      await handleSelectConversation(deepLinkThreadId);
    } else {
      notFoundThreadId.value = deepLinkThreadId;
    }
    return;
  }

  // Select the most recently USED conversation OF THE FIRST PAGE. Explicitly picking the max
  // `lastUpdated` rather than index 0: under the `created` sort the top of the list is the
  // newest-created conversation, which is not necessarily the one the user was last working in.
  // Only the first page is loaded at this point, so under `created` a conversation that was used
  // recently but started long ago can sit on a later page and lose to this reduce. Paging the whole
  // list on mount to make it exact would defeat the incremental loading this sits on top of; under
  // the default `lastUsed` sort the first page always holds the true maximum anyway.
  if (conversations.value.length > 0) {
    const mostRecent = conversations.value.reduce((best, c) =>
      c.lastUpdated > best.lastUpdated ? c : best
    );
    await handleSelectConversation(mostRecent.threadId);
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

  // NOTHING is reserved here. Since #435 the id can only come from the server, and reserving one
  // per click would write a metadata row for every "New chat" the user never types into — rows that
  // GET /api/conversations lists, so the sidebar fills with empty "New Conversation" entries and a
  // reload auto-selects the newest of them instead of the conversation the user was reading.
  // Clearing the selection is what a blank chat IS; the reservation happens on the first send, via
  // the provisioning hook useChat calls when it needs an id (see `provisionThread`).
  currentThreadId.value = null;
  setThreadId(null);
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
        await handleNewChat();
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
  // Poll the active conversation's sub-agents so tabs/launcher populate as children spawn.
  startSubAgentPolling();
});

onBeforeUnmount(() => {
  window.removeEventListener('resize', checkMobile);
});
</script>

<template>
  <div class="chat-layout" data-testid="chat-layout">
    <ConversationSidebar
      v-if="!focusMode"
      :conversations="conversations"
      :current-thread-id="currentThreadId"
      :is-loading="conversationsLoading"
      :is-loading-more="conversationsLoadingMore"
      :sort-mode="conversationSortMode"
      :is-collapsed="sidebarCollapsed"
      @new-chat="handleNewChat"
      @select-conversation="handleSelectConversation"
      @delete-conversation="handleDeleteConversation"
      @toggle-collapse="handleToggleCollapse"
      @load-more="loadMoreConversations"
      @change-sort-mode="setConversationSortMode"
    />

    <main class="chat-main">
      <div v-if="notFoundThreadId" class="chat-view not-found-view" data-testid="conversation-not-found">
        <button
          v-if="sidebarCollapsed && !focusMode"
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
            v-if="sidebarCollapsed && !focusMode"
            class="menu-btn"
            @click="handleToggleCollapse"
            title="Open sidebar"
          >
            =
          </button>
          <h1>{{ headerTitle }}</h1>
          <div v-if="!focusMode" class="header-actions">
            <WorkspaceSelector
              ref="workspaceSelectorRef"
              :workspaces="workspaces"
              :gateway="workspaceGateway"
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
              class="egress-auth-btn"
              data-testid="egress-auth-button"
              title="Manage egress auth keys"
              @click="egressAuthModalOpen = true"
            >
              Egress Auth
            </button>
            <button
              class="file-browser-btn"
              data-testid="file-browser-button"
              title="Browse workspace files"
              :disabled="!currentThreadId"
              @click="fileBrowserModalOpen = true"
            >
              Files
            </button>
            <button
              class="share-btn"
              data-testid="share-button"
              title="Share this conversation"
              :disabled="!currentThreadId"
              @click="shareModalOpen = true"
            >
              Share
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

        <EgressAuthModal
          v-if="egressAuthModalOpen || egressDialogRequest.open"
          @close="handleCloseEgressModal"
        />

        <FileBrowserModal
          v-if="fileBrowserModalOpen"
          :thread-id="currentThreadId"
          @close="fileBrowserModalOpen = false"
        />

        <!--
          Gated on a thread id rather than accepting null: every share route is addressed by
          thread, so with no conversation open there is nothing to share and nothing to list.
        -->
        <!--
          `visibility` and `canShare` both come from the conversation LISTING, the only
          conversation-shaped document the client reads; the three share routes carry neither. The
          server flips visibility as the first grant is added and the last is revoked, so `changed`
          re-lists — otherwise the control would keep showing the visibility from before the grant it
          just made, and `canShare` would go stale with it (publishing a conversation takes sharing
          away from its own owner).
        -->
        <ShareConversationModal
          v-if="shareModalOpen && currentThreadId"
          :thread-id="currentThreadId"
          :visibility="currentConversation?.visibility"
          :can-share="currentConversation?.canShare"
          @changed="loadConversations"
          @close="shareModalOpen = false"
        />

        <ConversationTabs
          v-if="tabs.length > 1"
          :tabs="tabs"
          :active-tab-id="activeTabId"
          @select="selectTab"
        />

        <!-- MAIN conversation view: stays mounted (v-show) so its scroll/stream/pill state survives
             tab detours. Its banners, usage, pending queue and input are main-only by construction. -->
        <div v-show="activeTabId === 'main'" class="tab-view" data-testid="main-view">
          <MessageList :display-items="displayItems" :is-loading="chatLoading" />

          <AuthRequiredBanner :requests="pendingAuthRequests" @dismiss="dismissAuthRequest" />

          <div v-if="error" class="error-banner" data-testid="error-banner">
            {{ error }}
          </div>

          <div
            v-if="cumulativeUsage.totalTokens > 0"
            class="usage-banner"
            data-testid="usage-banner"
            title="Total sums per-call input tokens, so the cached prompt prefix is re-counted every turn; it already includes usage spent inside sub-agents and workflow tasks. In = fresh (uncached) input this conversation."
          >
            Total: {{ cumulativeUsage.totalTokens }} |
            In: {{ cumulativeUsage.uncachedInputTokens }} |
            Out: {{ cumulativeUsage.completionTokens }}
            <template v-if="cumulativeUsage.cachedTokens > 0">
              | Cached: {{ cumulativeUsage.cachedTokens }}
            </template>
            <template v-if="cumulativeUsage.cacheCreationTokens > 0">
              | Cache created: {{ cumulativeUsage.cacheCreationTokens }}
            </template>
            <template v-if="usageCostDisplay">
              | {{ usageCostDisplay }}
            </template>
          </div>

          <PendingMessageQueue :pending-messages="pendingMessages" />

          <!-- Docked directly above the input: a question the run is blocked on is something the
               user must ACT on, so it belongs where they act, not inside the transcript's pill. -->
          <PendingQuestionDock :display-items="displayItems" />

          <ChatInput
            :disabled="isSending && !chatLoading"
            :streaming="chatLoading"
            @send="handleSend"
            @cancel="handleCancel"
          />
        </div>

        <!-- SUB-AGENT view: mounted only while a sub-agent tab is active; its own error banner + input
             (routed to the focused child) + child-scoped tool-result provide live inside it. -->
        <SubAgentTranscript
          v-if="activeTabId !== 'main'"
          :active-agent-id="activeTabId"
          :focused-agent-id="focusedAgentId"
          :display-items="focusedDisplayItems"
          :is-streaming="isFocusedStreaming"
          :error="subAgentError"
          :get-result-for-tool-call="getSubAgentResultForToolCall"
          :submit-client-tool-result="submitToFocusedChild"
          @send="handleSubAgentSend"
        />
      </div>
    </main>

    <!-- Right-side WORK BOARD (#583). A SIBLING of the sub-agent panel, not nested with it: both stay
         direct flex children of .chat-layout, so each keeps full column height and independent
         collapse, and SubAgentListPanel needs no change at all. The board sits inboard of the
         sub-agent panel so the sub-agent rail stays where it has always been, at the true right edge.

         `v-if="hasTodoBoard"` is load-bearing, not an optimization: a conversation that never touched
         the task tools — every CLI-backed provider (codex/claude/copilot), and every ordinary chat —
         must render NOTHING here rather than an empty board eating the right edge. That is what keeps
         two right-hand panels affordable. -->
    <TodoBoardPanel v-if="hasTodoBoard" :tasks="todoTasks" />

    <!-- Right-side launcher: shares ChatLayout's hoisted sub-agent state (the panel no longer owns a
         composable). Clicking a row activates that sub-agent's center-pane tab via selectTab. -->
    <SubAgentListPanel
      :children="subAgentChildren"
      :active-tab-id="activeTabId"
      @select="selectTab"
    />
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

/* A single tab's content column (main or sub-agent view): grows to fill, letting its MessageList
   scroll and its ChatInput pin to the bottom, exactly as the pre-tabs layout did. */
.tab-view {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
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

.egress-auth-btn {
  padding: 8px 16px;
  background: #6f42c1;
  color: white;
  border: none;
  border-radius: 6px;
  font-size: 14px;
  cursor: pointer;
  transition: background 0.2s;
}

.file-browser-btn {
  padding: 8px 16px;
  background: #2d6cdf;
  color: white;
  border: none;
  border-radius: 6px;
  font-size: 14px;
  cursor: pointer;
  transition: background 0.2s;
}

.egress-auth-btn:hover {
  background: #5a34a0;
}

.file-browser-btn:hover:not(:disabled) {
  background: #2057bd;
}

.file-browser-btn:disabled {
  background: #ccc;
  cursor: not-allowed;
}

.share-btn {
  padding: 8px 16px;
  background: #2d6cdf;
  color: white;
  border: none;
  border-radius: 6px;
  font-size: 14px;
  cursor: pointer;
  transition: background 0.2s;
}

.share-btn:hover:not(:disabled) {
  background: #2057bd;
}

.share-btn:disabled {
  background: #ccc;
  cursor: not-allowed;
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
