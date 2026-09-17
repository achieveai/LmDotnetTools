<script setup lang="ts">
import { computed, ref, nextTick, onMounted, onBeforeUnmount, provide, watch } from 'vue';
import { useConversations } from '@/composables/useConversations';
import { useChat, getDisplayText } from '@/composables/useChat';
import { useChatModes } from '@/composables/useChatModes';
import { useProviders } from '@/composables/useProviders';
import { DEFAULT_WORKSPACE_ID, useWorkspaces } from '@/composables/useWorkspaces';
import { egressDialogRequest, closeEgressDialog } from '@/composables/useEgressAuth';
import { conversationExists, updateConversationMetadata } from '@/api/conversationsApi';
import { WorkspaceRevisionConflictError } from '@/api/workspacesApi';
import { InvalidEnvError } from '@/api/chatModesApi';
import type { ChatModeCreateUpdate } from '@/types/chatMode';
import type { WorkspaceCreate, WorkspaceUpdate } from '@/types/workspace';
import { isWorkspaceSelectable } from '@/types/workspace';
import ConversationSidebar from './ConversationSidebar.vue';
import MessageList from './MessageList.vue';
import PendingMessageQueue from './PendingMessageQueue.vue';
import ChatInput from './ChatInput.vue';
import PendingQuestionDock from './PendingQuestionDock.vue';
import QuestionInbox from './QuestionInbox.vue';
import { useQuestionInbox, type QuestionInboxEntry } from '@/composables/useQuestionInbox';
import ConversationInspector from './ConversationInspector.vue';
import PanelSplitter from './PanelSplitter.vue';
import ContextCostPanel from './ContextCostPanel.vue';
import ArtifactPreviewModal from './ArtifactPreviewModal.vue';
import ConversationTabs from './ConversationTabs.vue';
import SubAgentTranscript from './SubAgentTranscript.vue';
import { useSubAgentPanel } from '@/composables/useSubAgentPanel';
import { useTodoBoard } from '@/composables/useTodoBoard';
import { useContextReport } from '@/composables/useContextReport';
import { useManualCompaction } from '@/composables/useManualCompaction';
import { useViewPreference, type ViewPreference } from '@/composables/useViewPreference';
import { useConversationTabs, GO_TO_AGENT_TAB } from '@/composables/useConversationTabs';
import {
  GET_AGENT_COLOR,
  GET_AGENT_ROUTING,
  resolveAgentRoutingFromCall,
  type AgentRoutingLookup,
} from '@/utils/agentColors';
import { SUBMIT_CLIENT_TOOL_RESULT } from '@/composables/useClientToolSubmit';
import { WORKSPACE_FILE_LINKS, type WorkspaceFileLinksContext } from '@/utils/workspaceLinks';
import { GET_CHECKPOINT_STATE, type CheckpointStateLookup } from '@/composables/messageDisplay';
import ModeSelector from './ModeSelector.vue';
import ProviderSelector from './ProviderSelector.vue';
import WorkspaceSelector from './WorkspaceSelector.vue';
import AuthRequiredBanner from './AuthRequiredBanner.vue';
import MarketplaceModal from './MarketplaceModal.vue';
import EgressAuthModal from './EgressAuthModal.vue';
import FileBrowserModal from './FileBrowserModal.vue';
import ShareConversationModal from './ShareConversationModal.vue';
import HeaderActionsMenu from './HeaderActionsMenu.vue';

const {
  conversations,
  currentThreadId,
  currentConversation,
  isLoading: conversationsLoading,
  isLoadingMore: conversationsLoadingMore,
  hasMoreConversations,
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
const workspaceManagementRef = ref<InstanceType<typeof WorkspaceSelector> | null>(null);
const modeSelectorRef = ref<InstanceType<typeof ModeSelector> | null>(null);
const { viewPreference, selectViewPreference } = useViewPreference();
const showDeveloperDiagnostics = computed(() => viewPreference.value === 'developer');

function handleViewPreferenceChange(event: Event): void {
  const preference = (event.target as HTMLInputElement).value as ViewPreference;
  selectViewPreference(preference);
  if (preference === 'consumer') closeInspector(false);
}

// Initialize chat with getters for the current mode and provider ids.
const {
  displayItems,
  isLoading: chatLoading,
  isSending,
  error,
  cumulativeUsage,
  cumulativeCost,
  conversationTodo,
  contextPressure,
  compactionStatus,
  connectionEpoch,
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
  refreshChildren: refreshSubAgentChildren,
} = useSubAgentPanel(() => subAgentParentThreadId.value);

// ToDo board state (#583), hoisted here for the same reason the sub-agent panel is: ONE instance,
// owned by the layout, handed to a stateless panel. It reuses `subAgentParentThreadId` — despite the
// name, that computed is simply "the thread id once the conversation has actually started", which is
// exactly the gate the board wants too: a fresh, unsent New Chat has no board to fetch.
const { tasks: todoTasks, hasBoard: hasTodoBoard } = useTodoBoard(
  () => subAgentParentThreadId.value,
  () => conversationTodo.value
);

// Context/cost panel state (#685), hoisted like the board. Same start gate; the endpoint is
// authoritative and re-read when the run goes idle (usage rows persist at run completion) or the
// sub-agent roster changes (a new child gets its own row) — child loops publish their live frames to
// their own sockets, not this one, so their rows only move on a re-read.
const {
  rows: contextRows,
  total: contextTotal,
  status: contextStatus,
  generatedAtUtc: contextGeneratedAtUtc,
  hydrate: hydrateContextReport,
} = useContextReport(
  () => subAgentParentThreadId.value,
  () => contextPressure.value,
  () => `${chatLoading.value ? 'busy' : 'idle'}:${subAgentChildren.value.map((c) => c.agentId).join(',')}`
);

// Compact now (manual compaction) for the same conversation the panel shows. A committed compaction,
// manual or automatic, re-reads the report so the panel's compaction state catches up at once.
const { view: compactionControl, request: requestManualCompaction } = useManualCompaction(
  () => subAgentParentThreadId.value,
  () => compactionStatus.value,
  (report) => void hydrateContextReport(report),
  { getConnectionEpoch: () => connectionEpoch.value }
);

// File preview (#583, PR 5; chat file links): two openers bubble a file up here, because the modal
// needs the thread id and neither opener owns it. The board panel passes a chip's workspace-relative
// `path`; an assistant message's file link passes its raw `target`, which the modal resolves on the
// server. This object is the whole modal state — null means closed. Keyed to the BOARD's thread
// (subAgentParentThreadId): the conversation whose task carries the chip and whose workspace the
// message's links point into (sub-agent transcripts share it).
type FilePreviewRequest = { id: string; path: string; target?: undefined; label: string }
  | { id: string; path?: undefined; target: string; label: string };
const previewTabs = ref<FilePreviewRequest[]>([]);
const activePreviewId = ref<string | null>(null);
const artifactPreview = computed(() => previewTabs.value.find((tab) => tab.id === activePreviewId.value) ?? null);

function previewLabel(value: string): string {
  const parts = value.replace(/\\/g, '/').split('/').filter(Boolean);
  return parts[parts.length - 1] || value;
}

function addPreview(request: { path: string } | { target: string }): void {
  const value = 'path' in request ? request.path : request.target;
  const id = `${'path' in request ? 'path' : 'target'}:${value}`;
  if (!previewTabs.value.some((tab) => tab.id === id)) {
    previewTabs.value.push({ ...request, id, label: previewLabel(value) } as FilePreviewRequest);
  }
  activePreviewId.value = id;
  if (!hasWorkspaceWidthPreference.value && previewTabs.value.length === 1) inspectorWidth.value = 640;
  inspectorOpen.value = true;
}

function openArtifactPreview(path: string): void {
  addPreview({ path });
}

// Provided to every TextMessage below (main chat and sub-agent transcripts). A link rendered for an
// earlier conversation that is somehow still clicked after a switch carries the OLD thread id, so it is
// ignored rather than resolved against the new conversation's workspace.
provide<WorkspaceFileLinksContext>(WORKSPACE_FILE_LINKS, {
  threadId: subAgentParentThreadId,
  open: (link) => {
    if (link.threadId !== subAgentParentThreadId.value) return;
    addPreview({ target: link.target });
  },
});

// A conversation switch unmounts the modal rather than leaving it previewing the OLD thread's file
// against the NEW thread's workspace. This is also the second half of the #594 D6 fix: with the
// preview's backdrop stopping at the sidebar's edge (`.artifact-preview-beside-sidebar`,
// ArtifactPreviewModal.vue), clicking another conversation actually reaches the sidebar, and THIS
// watch is what closes the modal for it.
watch(subAgentParentThreadId, () => {
  previewTabs.value = [];
  activePreviewId.value = null;
  previewExpanded.value = false;
});

const { activeTabId, tabs, selectTab: selectConversationTab, getAgentColor } = useConversationTabs({
  children: subAgentChildren,
  focusedAgentId,
  focusChild,
  unfocusChild,
  getParentThreadId: () => chatThreadId.value,
});

function handleSubAgentSend(text: string): void {
  sendToFocusedChild(text);
}

const questionInbox = useQuestionInbox(() => currentThreadId.value);
const questionInboxEntries = computed(() => questionInbox.entries.value.map((entry) => ({
  ...entry,
  agentName: entry.agentName || 'Main agent',
})));
const requestedQuestionId = ref('');
const pendingQuestionAgentIds = computed(() => [...new Set(questionInbox.entries.value
  .filter((entry) => entry.rootThreadId === currentThreadId.value && entry.agentId)
  .map((entry) => entry.agentId!))]);
const questionBusy = ref(false);
const questionOpen = ref(false);
const questionNavigating = ref(false);
async function selectTab(id: string): Promise<void> {
  if (questionBusy.value) return;
  await selectConversationTab(id);
}
const questionNavigationError = ref<string | null>(null);
const autoOpenedQuestions = new Set<string>();
const questionScope = computed(() => `${currentThreadId.value ?? 'draft'}:${activeTabId.value}`);
const questionSource = computed(() => `${currentConversation.value?.title || 'Conversation'} · ${
  activeTabId.value === 'main' ? 'Main agent' : subAgentChildren.value.find((child) => child.agentId === activeTabId.value)?.name || 'Agent'
}`);

function questionOpened(id: string): void {
  autoOpenedQuestions.add(JSON.stringify([currentThreadId.value, activeTabId.value, id]));
}

function autoQuestionKey(entry: QuestionInboxEntry): string {
  return JSON.stringify([entry.rootThreadId, entry.agentId || 'main', entry.toolCallId]);
}

async function openInboxQuestion(entry: QuestionInboxEntry): Promise<void> {
  if (questionBusy.value || questionNavigating.value) return;
  questionNavigating.value = true;
  questionNavigationError.value = null;
  requestedQuestionId.value = '';
  try {
    if (entry.rootThreadId !== currentThreadId.value) {
      addOrUpdateConversation(entry.conversation);
      await handleSelectConversation(entry.rootThreadId);
    }
    if (currentThreadId.value !== entry.rootThreadId) return;
    if (entry.agentId) {
      await refreshSubAgentChildren();
      if (!subAgentChildren.value.some((child) => child.agentId === entry.agentId)) {
        throw new Error('This agent is no longer available. Refresh the question inbox.');
      }
      await selectTab(entry.agentId);
      if (focusedAgentId.value !== entry.agentId) throw new Error('Could not connect to this agent. Try again.');
    } else {
      await selectTab('main');
    }
    await nextTick();
    const result = entry.agentId ? getSubAgentResultForToolCall(entry.toolCallId) : getResultForToolCall(entry.toolCallId);
    if (!result?.is_deferred) {
      questionNavigationError.value = 'This question is no longer waiting for an answer.';
      void questionInbox.refresh();
      return;
    }
    requestedQuestionId.value = entry.toolCallId;
    if (window.innerWidth <= 768) sidebarCollapsed.value = true;
  } catch (err) {
    // A partial tab change can mount a dock before child focus finishes. Do not let that transient
    // `opened` event suppress a later automatic retry when navigation itself did not succeed.
    autoOpenedQuestions.delete(autoQuestionKey(entry));
    questionNavigationError.value = err instanceof Error ? err.message : 'Could not open this question. Try again.';
  } finally {
    questionNavigating.value = false;
  }
}

function selectInboxQuestion(key: string): void {
  const entry = questionInbox.entries.value.find((candidate) => candidate.key === key);
  if (entry) void openInboxQuestion(entry);
}

watch([questionInbox.entries, currentThreadId, questionOpen, questionBusy], () => {
  if (questionOpen.value || questionBusy.value || questionNavigating.value || document.visibilityState === 'hidden') return;
  if (document.querySelector('[role="dialog"]')) return;
  const entry = questionInbox.entries.value.find((candidate) =>
    candidate.rootThreadId === currentThreadId.value && !autoOpenedQuestions.has(autoQuestionKey(candidate)));
  if (entry) void openInboxQuestion(entry);
}, { flush: 'post' });

watch(questionScope, () => { questionOpen.value = false; questionBusy.value = false; });

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
// Provide checkpointId → compaction state (#721) so a compaction divider can badge a checkpoint the
// context report says was rolled back; the persisted row itself never learns that.
const getCheckpointState: CheckpointStateLookup = (checkpointId) =>
  contextRows.value.find((row) => row.compaction.checkpointId === checkpointId)?.compaction.state ?? null;
provide(GET_CHECKPOINT_STATE, getCheckpointState);

const sidebarCollapsed = ref(false);
const LEFT_WIDTH_KEY = 'lmstreaming.projectsWidth';
const RIGHT_WIDTH_KEY = 'lmstreaming.workspaceWidth';
const PREVIEW_HEIGHT_KEY = 'lmstreaming.previewHeight';
const LEFT_DEFAULT = 280;
const RIGHT_DEFAULT = 320;
const MIN_CHAT_WIDTH = 360;
const hasWorkspaceWidthPreference = ref(false);
function storedNumber(key: string, fallback: number): number {
  try {
    const stored = localStorage.getItem(key);
    if (key === RIGHT_WIDTH_KEY) {
      hasWorkspaceWidthPreference.value = stored !== null;
    }
    if (stored === null) return fallback;
    const value = Number(stored);
    return Number.isFinite(value) ? value : fallback;
  } catch {
    return fallback;
  }
}
const sidebarWidth = ref(storedNumber(LEFT_WIDTH_KEY, LEFT_DEFAULT));
const inspectorWidth = ref(storedNumber(RIGHT_WIDTH_KEY, RIGHT_DEFAULT));
const previewHeight = ref(storedNumber(PREVIEW_HEIGHT_KEY, Math.round((window.innerHeight - 54) * 0.65)));
const previewExpanded = ref(false);
const shellDragging = ref(false);
const viewportWidth = ref(typeof window === 'undefined' ? 1440 : window.innerWidth);
const viewportHeight = ref(typeof window === 'undefined' ? 900 : window.innerHeight);
const preferredRightWidth = computed(() => clamp(inspectorWidth.value, 300, 960));
const effectiveRightWidth = computed(() =>
  viewportWidth.value > 1100 && inspectorOpen.value ? preferredRightWidth.value : 0
);
const desktopLeftMax = computed(() =>
  Math.min(420, Math.max(220, viewportWidth.value - effectiveRightWidth.value - MIN_CHAT_WIDTH - 14))
);
const clampedSidebarWidth = computed(() => clamp(sidebarWidth.value, 220, desktopLeftMax.value));
const effectiveLeftWidth = computed(() =>
  viewportWidth.value <= 768 || sidebarCollapsed.value ? 0 : clampedSidebarWidth.value
);
const desktopRightMax = computed(() =>
  Math.min(960, Math.max(300, viewportWidth.value - effectiveLeftWidth.value - MIN_CHAT_WIDTH - 14))
);
const clampedInspectorWidth = computed(() => clamp(inspectorWidth.value, 300, desktopRightMax.value));
const renderedInspectorWidth = computed(() =>
  previewExpanded.value
    ? Math.max(300, viewportWidth.value - effectiveLeftWidth.value - 7)
    : clampedInspectorWidth.value
);
const previewMaxHeight = computed(() => Math.max(180, viewportHeight.value - 220));
const previewDefaultHeight = computed(() =>
  clamp(Math.round((viewportHeight.value - 54) * 0.65), 180, previewMaxHeight.value)
);
const clampedPreviewHeight = computed(() => clamp(previewHeight.value, 180, previewMaxHeight.value));
function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, Math.round(value)));
}

function setSidebarWidth(value: number): void {
  sidebarWidth.value = clamp(value, 220, desktopLeftMax.value);
  try {
    localStorage.setItem(LEFT_WIDTH_KEY, String(sidebarWidth.value));
  } catch {
    // Storage is optional.
  }
}

function setInspectorWidth(value: number): void {
  inspectorWidth.value = clamp(value, 300, desktopRightMax.value);
  hasWorkspaceWidthPreference.value = true;
  try {
    localStorage.setItem(RIGHT_WIDTH_KEY, String(inspectorWidth.value));
  } catch {
    // Storage is optional.
  }
}

function setPreviewHeight(value: number): void {
  previewHeight.value = clamp(value, 180, previewMaxHeight.value);
  try {
    localStorage.setItem(PREVIEW_HEIGHT_KEY, String(previewHeight.value));
  } catch {
    // Storage is optional.
  }
}
function closePreview(id: string): void {
  const index = previewTabs.value.findIndex((tab) => tab.id === id);
  if (index < 0) return;
  previewTabs.value.splice(index, 1);
  if (activePreviewId.value === id) activePreviewId.value = previewTabs.value[Math.min(index, previewTabs.value.length - 1)]?.id ?? null;
  if (!previewTabs.value.length) {
    previewExpanded.value = false;
    if (!hasWorkspaceWidthPreference.value) inspectorWidth.value = RIGHT_DEFAULT;
  }
  void nextTick(() => {
    const active = Array.from(document.querySelectorAll<HTMLElement>('[data-preview-id]'))
      .find((element) => element.dataset.previewId === activePreviewId.value);
    (active ?? inspectorLauncherRef.value)?.focus();
  });
}
const isSwitchingMode = ref(false);
const isSwitchingProvider = ref(false);
const marketplaceModalOpen = ref(false);
const egressAuthModalOpen = ref(false);
const fileBrowserModalOpen = ref(false);
const shareModalOpen = ref(false);
const headerActionsMenuRef = ref<InstanceType<typeof HeaderActionsMenu> | null>(null);
const modalOpenedFromHeaderActions = ref<'marketplace' | 'egress' | 'files' | 'share' | null>(null);
const inspectorOpen = ref(false);
const inspectorSection = ref<'work' | 'agents'>('work');
const inspectorLauncherRef = ref<HTMLButtonElement | null>(null);
let inspectorInitialized = false;

function openInspector(): void {
  inspectorOpen.value = true;
}

function toggleInspector(): void {
  if (inspectorOpen.value) closeInspector();
  else openInspector();
}

function closeInspector(restoreFocus = true): void {
  inspectorOpen.value = false;
  previewExpanded.value = false;
  if (restoreFocus) void nextTick(() => inspectorLauncherRef.value?.focus());
}

function handleInspectorAgentSelect(agentId: string, closeDrawer: boolean): void {
  selectTab(agentId);
  if (!closeDrawer) return;
  closeInspector(false);
  void nextTick(() => {
    const tab = Array.from(document.querySelectorAll<HTMLButtonElement>('[data-testid="conversation-tab"]'))
      .find((button) => button.dataset.tabId === agentId);
    tab?.focus();
  });
}

/**
 * Closes the egress-auth modal, resetting both the header-button flag and any
 * programmatic open request (openEgressDialog).
 */
function handleCloseEgressModal(): void {
  egressAuthModalOpen.value = false;
  closeEgressDialog();
  restoreHeaderActionsFocus('egress');
}

function closeMarketplaceModal(): void {
  marketplaceModalOpen.value = false;
  restoreHeaderActionsFocus('marketplace');
}

function closeFileBrowserModal(): void {
  fileBrowserModalOpen.value = false;
  restoreHeaderActionsFocus('files');
}

function closeShareModal(): void {
  shareModalOpen.value = false;
  restoreHeaderActionsFocus('share');
}

function openHeaderActionModal(modal: 'marketplace' | 'egress' | 'files' | 'share'): void {
  modalOpenedFromHeaderActions.value = modal;
  if (modal === 'marketplace') marketplaceModalOpen.value = true;
  else if (modal === 'egress') egressAuthModalOpen.value = true;
  else if (modal === 'files') fileBrowserModalOpen.value = true;
  else shareModalOpen.value = true;
}

function restoreHeaderActionsFocus(modal: 'marketplace' | 'egress' | 'files' | 'share'): void {
  if (modalOpenedFromHeaderActions.value !== modal) return;
  modalOpenedFromHeaderActions.value = null;
  void nextTick(() => headerActionsMenuRef.value?.focusTrigger());
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

async function handleCreateWorkspace(
  data: WorkspaceCreate,
  origin: InstanceType<typeof WorkspaceSelector> | null = workspaceSelectorRef.value
): Promise<void> {
  const selectedBeforeManagementCreate = origin === workspaceManagementRef.value
    && currentThreadId.value !== null
    ? selectedWorkspaceId.value
    : null;
  try {
    await createWorkspace(data);
    if (selectedBeforeManagementCreate) selectWorkspace(selectedBeforeManagementCreate);
    origin?.closeForm();
  } catch (e) {
    const message = e instanceof Error ? e.message : 'Failed to create workspace';
    origin?.showFormError(message);
  }
}

async function handleUpdateWorkspace(
  workspaceId: string,
  data: WorkspaceUpdate,
  origin: InstanceType<typeof WorkspaceSelector> | null = workspaceSelectorRef.value
): Promise<void> {
  try {
    await updateWorkspace(workspaceId, data);
    origin?.closeForm();
  } catch (e) {
    const message = e instanceof Error ? e.message : 'Failed to update workspace';
    if (e instanceof WorkspaceRevisionConflictError || e instanceof InvalidEnvError) {
      // Two different causes, one required response: the server's copy has moved on and the form has
      // not. On a CONFLICT, updateWorkspace has already re-listed, so the next save would carry a
      // FRESH compare-and-swap token while the form still held the pre-conflict selection — one more
      // click would pass CAS and silently overwrite whoever changed it. On an INVALID_ENV, the write
      // PARTIALLY succeeded (the store took it, the gateway refused the env), and the env map is sent
      // as a wholesale REPLACEMENT with no CAS token at all — so a reseed from stale rows would
      // delete keys the server had actually kept. Re-seed from the refreshed workspace either way, so
      // the pending change is dropped rather than the stored one. `await nextTick()` first: the
      // refreshed list reaches the child as a prop only after the parent re-renders.
      await nextTick();
      origin?.reseedEditForm();
    }
    origin?.showFormError(message);
  }
}

function handleNewProject(): void {
  workspaceSelectorRef.value?.closeDropdown();
  workspaceManagementRef.value?.openCreateForm();
}

function handleEditProject(workspaceId: string): void {
  workspaceSelectorRef.value?.closeDropdown();
  workspaceManagementRef.value?.openEditForm(workspaceId);
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
  if (questionBusy.value) return;
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

/** Starts an unreserved draft bound to the workspace whose sidebar folder launched it. */
async function handleNewChatInWorkspace(workspaceId: string): Promise<void> {
  await settleWorkspaceCatalog();
  const workspace = workspaces.value.find((candidate) => candidate.id === workspaceId);
  if (!workspace || !isWorkspaceSelectable(workspace)) {
    return;
  }

  await handleNewChat();
  selectWorkspace(workspaceId);
  if (window.innerWidth <= 768) {
    sidebarCollapsed.value = true;
  }
}

// Handle selecting an existing conversation
async function handleSelectConversation(threadId: string): Promise<void> {
  if (questionBusy.value) return;
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
  if (questionBusy.value) return;
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
    modeSelectorRef.value?.closeManageForm();
  } catch (e) {
    if (e instanceof InvalidEnvError) {
      // Keeps the create form open (with the entered env rows intact) instead of the previous
      // silent console.error — this is the one failure mode worth surfacing inline, since it names
      // exactly which keys are wrong and the user can fix them without re-entering everything.
      modeSelectorRef.value?.showManageFormError(e.message);
    } else {
      console.error('Failed to create mode:', e);
      modeSelectorRef.value?.closeManageForm();
    }
  }
}

// Handle updating a mode
async function handleUpdateMode(modeId: string, data: ChatModeCreateUpdate): Promise<void> {
  try {
    await updateMode(modeId, data);
    modeSelectorRef.value?.closeManageForm();
  } catch (e) {
    if (e instanceof InvalidEnvError) {
      modeSelectorRef.value?.showManageFormError(e.message);
    } else {
      console.error('Failed to update mode:', e);
      modeSelectorRef.value?.closeManageForm();
    }
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
  viewportWidth.value = window.innerWidth;
  viewportHeight.value = window.innerHeight;
  if (window.innerWidth <= 768) {
    sidebarCollapsed.value = true;
  }
}

onMounted(() => {
  checkMobile();
  if (!inspectorInitialized) {
    inspectorOpen.value = viewPreference.value === 'developer' && window.innerWidth > 1100;
    inspectorInitialized = true;
  }
  window.addEventListener('resize', checkMobile);
  // Poll the active conversation's sub-agents so tabs/launcher populate as children spawn.
  startSubAgentPolling();
});

watch(focusMode, (focused) => {
  if (focused) closeInspector(false);
});

onBeforeUnmount(() => {
  window.removeEventListener('resize', checkMobile);
});
</script>

<template>
  <div class="chat-layout" data-testid="chat-layout">
    <header :class="['app-header', { 'focus-mode': focusMode }]" data-testid="app-header">
      <div class="app-header-left">
        <button
          v-if="!focusMode"
          class="sidebar-toggle"
          data-testid="sidebar-toggle"
          :aria-label="sidebarCollapsed ? 'Expand sidebar' : 'Collapse sidebar'"
          :title="sidebarCollapsed ? 'Expand sidebar' : 'Collapse sidebar'"
          :aria-expanded="!sidebarCollapsed"
          @click="handleToggleCollapse"
        >
          <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
            <rect x="2.5" y="3" width="15" height="14" rx="2" />
            <path d="M7.5 3v14" />
          </svg>
        </button>
        <h1>{{ headerTitle }}</h1>
      </div>

      <div v-if="!focusMode" class="app-header-center">
        <fieldset class="view-preference" aria-label="Conversation view">
          <legend class="sr-only">Conversation view</legend>
          <label>
            <input
              v-model="viewPreference"
              type="radio"
              name="view-preference"
              value="consumer"
              data-testid="view-preference-consumer"
              @change="handleViewPreferenceChange"
            />
            <span>Consumer</span>
          </label>
          <label>
            <input
              v-model="viewPreference"
              type="radio"
              name="view-preference"
              value="developer"
              data-testid="view-preference-developer"
              @change="handleViewPreferenceChange"
            />
            <span>Developer</span>
          </label>
        </fieldset>
      </div>

      <div v-if="!focusMode" class="app-header-right">
        <QuestionInbox :entries="questionInboxEntries" :refreshing="questionInbox.isRefreshing.value"
          :error="questionInbox.error.value" :disabled="questionBusy || questionNavigating"
          @select="selectInboxQuestion" @refresh="questionInbox.refresh()" />
        <HeaderActionsMenu
          ref="headerActionsMenuRef"
          :files-disabled="!currentThreadId"
          :share-disabled="!currentThreadId"
          :clear-disabled="chatLoading"
          @open-marketplaces="openHeaderActionModal('marketplace')"
          @open-egress="openHeaderActionModal('egress')"
          @open-files="openHeaderActionModal('files')"
          @open-share="openHeaderActionModal('share')"
          @clear="clearMessages"
        />
        <button
          id="conversation-inspector-toggle"
          ref="inspectorLauncherRef"
          :class="['inspector-launcher', { 'above-inspector-overlay': inspectorOpen }]"
          data-testid="conversation-inspector-launcher"
          :aria-label="`${inspectorOpen ? 'Close' : 'Open'} Work and agents`"
          :title="`${inspectorOpen ? 'Close' : 'Open'} Work and agents`"
          aria-controls="conversation-inspector"
          :aria-expanded="inspectorOpen"
          @click="toggleInspector"
        >
          <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
            <rect x="2.5" y="3" width="15" height="14" rx="2" />
            <path d="M12.5 3v14" />
          </svg>
        </button>
      </div>
    </header>

    <div :class="['shell-body', { resizing: shellDragging }]" data-testid="shell-body">
    <ConversationSidebar
      v-if="!focusMode"
      id="projects-sidebar"
      class="hosted-sidebar"
      :conversations="conversations"
      :current-thread-id="currentThreadId"
      :is-loading="conversationsLoading"
      :is-loading-more="conversationsLoadingMore"
      :workspaces="workspaces"
      :has-more="hasMoreConversations"
      :sort-mode="conversationSortMode"
      :is-collapsed="sidebarCollapsed"
      :desktop-width="clampedSidebarWidth"
      @new-chat="handleNewChat"
      @new-chat-in-workspace="handleNewChatInWorkspace"
      @new-project="handleNewProject"
      @edit-project="handleEditProject"
      @select-conversation="handleSelectConversation"
      @delete-conversation="handleDeleteConversation"
      @toggle-collapse="handleToggleCollapse"
      @load-more="loadMoreConversations"
      @change-sort-mode="setConversationSortMode"
    />
    <PanelSplitter
      v-if="!focusMode && !sidebarCollapsed && viewportWidth > 768"
      data-testid="projects-splitter" label="Resize projects" orientation="vertical"
      controls="projects-sidebar"
      :value="clampedSidebarWidth" :min="220" :max="desktopLeftMax" :default-value="LEFT_DEFAULT"
      @update:value="setSidebarWidth" @dragging="shellDragging = $event"
    />

    <WorkspaceSelector
      v-if="!focusMode"
      ref="workspaceManagementRef"
      presentation="management"
      :workspaces="workspaces"
      :gateway="workspaceGateway"
      :selected-workspace-id="selectedWorkspaceId"
      :locked-workspace-id="null"
      :is-loading="workspacesLoading"
      @create-workspace="handleCreateWorkspace($event, workspaceManagementRef)"
      @update-workspace="(workspaceId, data) => handleUpdateWorkspace(workspaceId, data, workspaceManagementRef)"
    />

    <main id="chat-main" v-show="!previewExpanded" class="chat-main">
      <p v-if="questionNavigationError" class="question-navigation-error" role="status">{{ questionNavigationError }}</p>
      <div v-if="notFoundThreadId" class="chat-view not-found-view" data-testid="conversation-not-found">
        <div class="not-found-content">
          <h2>Conversation not found</h2>
          <p>The conversation "{{ notFoundThreadId }}" does not exist or is no longer available.</p>
          <button class="new-chat-btn" @click="handleNewChat">Start a new chat</button>
        </div>
      </div>
      <div v-else class="chat-view">
        <MarketplaceModal
          v-if="marketplaceModalOpen"
          @close="closeMarketplaceModal"
        />

        <EgressAuthModal
          v-if="egressAuthModalOpen || egressDialogRequest.open"
          @close="handleCloseEgressModal"
        />

        <FileBrowserModal
          v-if="fileBrowserModalOpen"
          :thread-id="currentThreadId"
          @close="closeFileBrowserModal"
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
          @close="closeShareModal"
        />

        <ConversationTabs
          v-if="tabs.length > 1"
          :tabs="tabs"
          :active-tab-id="activeTabId"
          :pending-question-agent-ids="pendingQuestionAgentIds"
          @select="selectTab"
        />

        <!-- MAIN conversation view: stays mounted (v-show) so its scroll/stream/pill state survives
             tab detours. Its banners, usage, pending queue and input are main-only by construction. -->
        <div id="conversation-main-view" v-show="activeTabId === 'main'" class="tab-view" data-testid="main-view"
          role="region" :aria-labelledby="tabs.length > 1 ? 'conversation-main-selector' : undefined"
          :aria-label="tabs.length > 1 ? undefined : 'Main conversation'">
          <MessageList
            :display-items="displayItems"
            :is-loading="chatLoading"
            :view-preference="viewPreference"
          />

          <AuthRequiredBanner :requests="pendingAuthRequests" @dismiss="dismissAuthRequest" />

          <div v-if="error" class="error-banner" data-testid="error-banner">
            {{ error }}
          </div>

          <ContextCostPanel
            v-if="subAgentParentThreadId"
            v-show="showDeveloperDiagnostics"
            :rows="contextRows"
            :total="contextTotal"
            :status="contextStatus"
            :generated-at-utc="contextGeneratedAtUtc"
            :compaction="compactionControl"
            @compact="requestManualCompaction"
          />

          <div
            v-if="cumulativeUsage.totalTokens > 0"
            v-show="showDeveloperDiagnostics"
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
          <PendingQuestionDock :key="currentThreadId || 'draft'" :display-items="displayItems"
            :scope-key="`${currentThreadId || 'draft'}:main`" :source-label="`${currentConversation?.title || 'Conversation'} · Main agent`"
            :active="activeTabId === 'main'" :requested-question-id="activeTabId === 'main' ? requestedQuestionId : ''"
            @busy-change="questionBusy = $event" @open-change="questionOpen = $event" @opened="questionOpened" />

          <ChatInput
            :disabled="isSending && !chatLoading"
            :streaming="chatLoading"
            @send="handleSend"
            @cancel="handleCancel"
          >
            <template v-if="!focusMode" #mode-control>
              <ModeSelector
                ref="modeSelectorRef"
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
            </template>
            <template v-if="currentThreadId === null && !focusMode" #project-control>
              <WorkspaceSelector
                ref="workspaceSelectorRef"
                presentation="project"
                :workspaces="workspaces"
                :gateway="workspaceGateway"
                :selected-workspace-id="selectedWorkspaceId"
                :locked-workspace-id="null"
                :is-loading="workspacesLoading"
                :disabled="workspaceSelectorDisabled"
                @select-workspace="handleSelectWorkspace"
                @create-workspace="handleCreateWorkspace($event, workspaceSelectorRef)"
                @update-workspace="(workspaceId, data) => handleUpdateWorkspace(workspaceId, data, workspaceSelectorRef)"
              />
            </template>
            <template #context-control>
              <ProviderSelector
                :providers="providers"
                :selected-provider-id="selectedProviderId"
                :is-loading="providersLoading"
                :disabled="providerSelectorDisabled"
                @select-provider="handleSelectProvider"
              />
            </template>
          </ChatInput>
        </div>

        <!-- SUB-AGENT view: mounted only while a sub-agent tab is active; its own error banner + input
             (routed to the focused child) + child-scoped tool-result provide live inside it. -->
        <SubAgentTranscript
          v-if="activeTabId !== 'main'"
          :key="questionScope"
          :question-scope="questionScope" :question-source="questionSource" :requested-question-id="requestedQuestionId"
          @question-busy="questionBusy = $event" @question-open="questionOpen = $event" @question-opened="questionOpened"
          :active-agent-id="activeTabId"
          :focused-agent-id="focusedAgentId"
          :display-items="focusedDisplayItems"
          :is-streaming="isFocusedStreaming"
          :error="subAgentError"
          :get-result-for-tool-call="getSubAgentResultForToolCall"
          :submit-client-tool-result="submitToFocusedChild"
          :view-preference="viewPreference"
          @send="handleSubAgentSend"
        />
      </div>
    </main>

    <PanelSplitter
      v-if="!focusMode && inspectorOpen && !previewExpanded && viewportWidth > 1100"
      data-testid="workspace-splitter" label="Resize workspace" orientation="vertical" :direction="-1"
      controls="conversation-inspector"
      :value="clampedInspectorWidth" :min="300" :max="desktopRightMax" :default-value="RIGHT_DEFAULT"
      @update:value="setInspectorWidth" @dragging="shellDragging = $event"
    />
    <ConversationInspector
      v-if="!focusMode"
      :open="inspectorOpen"
      :active-section="inspectorSection"
      :desktop-width="renderedInspectorWidth"
      :preview-tabs="previewTabs.map(tab => ({ id: tab.id, label: tab.label, path: tab.path ?? tab.target }))"
      :active-preview-id="activePreviewId"
      :preview-height="clampedPreviewHeight"
      :preview-max-height="previewMaxHeight"
      :preview-default-height="previewDefaultHeight"
      :expanded="previewExpanded"
      :tasks="todoTasks"
      :has-work="hasTodoBoard"
      :children="subAgentChildren"
      :active-conversation-tab-id="activeTabId"
      external-close-control-id="conversation-inspector-toggle"
      @close="closeInspector"
      @select-section="inspectorSection = $event"
      @open-artifact="openArtifactPreview"
      @select-agent="handleInspectorAgentSelect"
      @select-preview="activePreviewId = $event"
      @close-preview="closePreview"
      @update:preview-height="setPreviewHeight"
    >
      <template #preview>
        <ArtifactPreviewModal
          v-if="artifactPreview && subAgentParentThreadId"
          :key="`${subAgentParentThreadId}:${artifactPreview.id}`"
          :thread-id="subAgentParentThreadId" :path="artifactPreview.path" :target="artifactPreview.target"
          embedded :expanded="previewExpanded" @toggle-expand="previewExpanded = !previewExpanded"
          @close="closePreview(artifactPreview.id)"
        />
      </template>
    </ConversationInspector>
    </div>
  </div>
</template>

<style scoped>
.question-navigation-error { margin: 8px 16px; padding: 10px 12px; color: #795719; background: #fff8ed; border-radius: 6px; font-size: 13px; }
.chat-layout {
  position: relative;
  display: flex;
  flex-direction: column;
  height: 100vh;
  overflow: hidden;
}

.app-header {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr);
  align-items: center;
  gap: 12px;
  flex: none;
  min-width: 0;
  padding: 12px;
  border-bottom: 1px solid #e0e0e0;
  background: #f8f9fa;
}

.app-header.focus-mode {
  grid-template-columns: minmax(0, 1fr);
}

.app-header-left,
.app-header-center,
.app-header-right {
  display: flex;
  min-width: 0;
  align-items: center;
}

.app-header-left {
  gap: 10px;
}

.app-header-center {
  justify-content: center;
}

.app-header-right {
  justify-content: flex-end;
  gap: 8px;
}

.app-header h1 {
  min-width: 0;
  margin: 0;
  overflow: hidden;
  font-size: 20px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.shell-body {
  position: relative;
  display: flex;
  flex: 1;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}

.shell-body.resizing :deep(.conversation-sidebar) {
  transition: none;
}

.hosted-sidebar :deep(.toggle-btn) {
  display: none;
}

.shell-body > .hosted-sidebar.conversation-sidebar.collapsed {
  width: 0;
  min-width: 0;
  overflow: hidden;
  border-right: 0;
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

.sidebar-toggle,
.inspector-launcher {
  display: inline-flex;
  width: 34px;
  height: 34px;
  flex: 0 0 34px;
  align-items: center;
  justify-content: center;
  padding: 0;
  border: 1px solid #cbd1d8;
  border-radius: 6px;
  background: #fff;
  color: #394553;
  cursor: pointer;
  transition: background 0.2s, border-color 0.2s;
}

.sidebar-toggle svg,
.inspector-launcher svg {
  width: 18px;
  height: 18px;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.5;
}

.sidebar-toggle:hover,
.inspector-launcher:hover {
  border-color: #aeb7c2;
  background: #eef1f4;
}

.view-preference {
  display: inline-flex;
  height: 34px;
  box-sizing: border-box;
  align-items: center;
  margin: 0;
  padding: 2px;
  border: 1px solid #cbd1d8;
  border-radius: 7px;
  background: #eef1f4;
}

.view-preference label {
  position: relative;
  cursor: pointer;
}

.view-preference input {
  position: absolute;
  opacity: 0;
  pointer-events: none;
}

.view-preference span {
  display: block;
  padding: 4px 8px;
  border-radius: 5px;
  color: #59636e;
  font-size: 14px;
  line-height: 18px;
}

.view-preference input:checked + span {
  color: #25313d;
  background: #fff;
  box-shadow: 0 1px 2px rgb(0 0 0 / 12%);
}

.view-preference input:focus-visible + span {
  outline: 2px solid #2d6cdf;
  outline-offset: 1px;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}

.inspector-launcher {
  position: static;
}

.inspector-launcher.above-inspector-overlay {
  position: relative;
  z-index: 102;
}

.sidebar-toggle:focus-visible,
.inspector-launcher:focus-visible {
  outline: 2px solid #2d6cdf;
  outline-offset: 2px;
}

@media (max-width: 520px) {
  .app-header {
    grid-template-columns: minmax(0, 1fr) auto;
    padding: 10px 12px;
  }

  .app-header-left {
    grid-column: 1;
    grid-row: 1;
  }

  .app-header-right {
    grid-column: 2;
    grid-row: 1;
  }

  .app-header-center {
    grid-column: 1 / -1;
    grid-row: 2;
    justify-self: center;
  }

  .app-header h1 {
    font-size: 18px;
  }

  .app-header.focus-mode {
    grid-template-columns: minmax(0, 1fr);
  }
}

@media (max-width: 768px) {
  .shell-body > .hosted-sidebar.conversation-sidebar {
    position: absolute;
    top: 0;
    bottom: 0;
  }
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
