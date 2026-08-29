import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { defineComponent, inject, h, type Ref } from 'vue';
import fs from 'fs';
import path from 'path';
import ChatLayout from '@/components/ChatLayout.vue';
import { SUBMIT_CLIENT_TOOL_RESULT, type ClientToolSubmitFn } from '@/composables/useClientToolSubmit';
import { GO_TO_AGENT_TAB, type GoToAgentTab } from '@/composables/useConversationTabs';
import {
  UnsupportedPluginsError,
  WorkspaceRevisionConflictError,
} from '@/api/workspacesApi';
import type { ConversationSummary } from '@/types/conversations';
import type { Workspace, WorkspaceGateway } from '@/types/workspace';

// Build a fixture from the REAL wire type rather than a hand-rolled shadow (#488). The shadow this
// replaced optional-ized every field and silently drifted from the server DTO — `visibility` (#445)
// and `canShare` (#482) were added on the wire while the tests kept asserting against the old shape.
// Because the return value is annotated as the real `ConversationSummary`, adding a required field to
// that type fails this file under `npm run type-check:test` (tsconfig.vitest.json includes the suite;
// CI runs it), so a fixture can no longer diverge from the contract unnoticed. Overrides carry the
// per-case fields; `title`/`lastUpdated` (required on the wire, irrelevant to these tests) default.
const makeConversation = (overrides: Partial<ConversationSummary> = {}): ConversationSummary => ({
  threadId: 'thread-1',
  title: '',
  lastUpdated: 0,
  ...overrides,
});

const sharedMocks = vi.hoisted(() => ({
  chatLoading: false,
  isSending: false,
  modesLoading: false,
  currentThreadId: 'thread-1' as string | null,
  // Sidebar conversations. A thread present here is "started" (first message
  // sent); an empty list with a non-null currentThreadId is a brand-new,
  // messageless thread (handleNewChat assigns the id before the first send).
  conversations: [] as ConversationSummary[],
  // Held here rather than created inside the mock factory: the share modal asks for a re-list once
  // it has made the server flip the conversation's visibility, and that call is what a test asserts.
  loadConversations: vi.fn(async () => {}),
  selectMode: vi.fn(),
  switchMode: vi.fn(),
  disconnectWebSocket: vi.fn(),
  // #435: the SPA no longer mints a thread id — `createNewConversation` POSTs /api/conversations
  // and resolves to the id the SERVER minted. Held here so a test can assert what ChatLayout sent
  // it and make it reject.
  createNewConversation: vi.fn(async (_binding: unknown) => 'thread-provisioned'),
  setThreadId: vi.fn(),
  // The bindings ChatLayout has to send with a provisioning request; the server resolves each and
  // refuses the whole request if any is unknown.
  selectedProviderId: null as string | null,
  selectedWorkspaceId: 'default' as string | null,
  // The in-flight `/api/providers` load. Resolved by default so every existing test sees a catalog
  // that was already there; a test that cares about the mount-time race replaces it with one it
  // resolves by hand.
  providerCatalogLoad: Promise.resolve() as Promise<void>,
  providerSelectionRef: null as Ref<string | null> | null,
  // The same, for `/api/workspaces`. Its selection starts at 'default' rather than null, so a test
  // that only holds the PROVIDER catalog open cannot tell whether the workspace half is awaited.
  workspaceCatalogLoad: Promise.resolve() as Promise<void>,
  workspaceSelectionRef: null as Ref<string | null> | null,
  // The workspace ROWS and the gateway envelope the flat stub hands ChatLayout. Both were hard-coded
  // ([] and absent) until #459, and that is exactly why F-001 hid: with no `gateway` key at all,
  // `workspaceGateway?.value?.available` was `undefined` in every test here, so the parent's
  // gateway-down gate could never fire and nothing noticed it disabling the whole selector.
  // Defaults keep every pre-existing test byte-identical: an empty list, and a null envelope (which
  // reads as `undefined` through the same optional chain the old stub produced).
  workspaces: [] as Workspace[],
  workspaceGateway: null as WorkspaceGateway | null,
  // The options object ChatLayout handed to useChat, so a test can drive the provisioning hook the
  // real composable calls on the first send.
  chatOptions: null as { provisionThreadId?: () => Promise<string> } | null,
  // The mode mock's `currentModeId` is one ref shared by every test in this file, so a mode-switch
  // test leaves it changed for whatever runs next. Exposed here so a test that asserts on the mode
  // can start from a known one instead of inheriting the previous test's.
  setModeId: null as ((modeId: string) => void) | null,
  selectProvider: vi.fn(),
  switchProvider: vi.fn(async () => {}),
  selectWorkspace: vi.fn(),
  addOrUpdateConversation: vi.fn(),
  // Incremental sidebar paging + sort selection, wired through ChatLayout to ConversationSidebar.
  loadMoreConversations: vi.fn(async () => {}),
  setSortMode: vi.fn(async () => {}),
  // The server-side existence probe the deep link falls back to when the id is not on page one.
  // Defaults to false so an id absent from BOTH the list and the backend still reads as not-found.
  conversationExists: vi.fn(async (_threadId: string) => false),
  resumeStreamIfActive: vi.fn(async () => {}),
  markStreamIdle: vi.fn(),
  markStreamLoading: vi.fn(),
  // #246: browser-hosted client tools (AskUserQuestion) gating.
  hasPendingClientQuestion: false,
  submitClientToolResult: vi.fn(async () => ({ status: 'acked' as const, duplicate: false })),
  // #246 defect 1: the sub-agent-scoped submit useSubAgentPanel exposes, distinct from the root's
  // submitClientToolResult above — ChatLayout must bind THIS one to SubAgentTranscript's prop so a
  // descendant's answer submits over the focused child connection, not the root.
  submitToFocusedChild: vi.fn(async () => ({ status: 'acked' as const, duplicate: false })),
  // Workspace create/update, routed through sharedMocks so a test can make them REJECT with the
  // typed errors workspacesApi throws, and assert what the parent's catch forwards to the child.
  createWorkspace: vi.fn(async () => {}),
  updateWorkspace: vi.fn(async () => {}),
  // The WorkspaceSelector methods ChatLayout reaches through its template ref.
  showFormError: vi.fn(),
  closeForm: vi.fn(),
  reseedEditForm: vi.fn(),
  // Captures the thread-id getter ChatLayout passes to useSubAgentPanel, so a test can assert the
  // start-gating (the getter returns null until the conversation has a sidebar entry).
  subAgentThreadGetter: null as (() => string | null) | null,
  // When true, the useWorkspaces mock below delegates to the REAL composable instead of the stub,
  // so a test can drive the genuine loadWorkspaces/isLoading/409 chain with only `fetch` faked.
  useRealWorkspaces: false,
  // The live thread-id ref inside the useChat mock, published so a test can CHANGE conversations
  // after mount (the artifact-modal suite switches threads to fire ChatLayout's close watch).
  chatThreadIdRef: null as Ref<string | null> | null,
  // The latest conversation_todo frame ref inside the useChat mock. The REAL useTodoBoard watches
  // it, so pushing a frame here is how a test conjures a board (and with it the TodoBoardPanel).
  conversationTodoRef: null as Ref<unknown> | null,
}));

vi.mock('@/composables/useConversations', async () => {
  const { computed, ref } = await import('vue');
  return {
    useConversations: () => ({
      conversations: ref(sharedMocks.conversations),
      currentThreadId: ref(sharedMocks.currentThreadId),
      currentConversation: computed(() =>
        sharedMocks.conversations.find((c) => c.threadId === sharedMocks.currentThreadId)
      ),
      isLoading: ref(false),
      isLoadingMore: ref(false),
      hasMoreConversations: ref(false),
      sortMode: ref<'lastUsed' | 'created'>('lastUsed'),
      loadConversations: sharedMocks.loadConversations,
      loadMoreConversations: sharedMocks.loadMoreConversations,
      setSortMode: sharedMocks.setSortMode,
      createNewConversation: sharedMocks.createNewConversation,
      selectConversation: vi.fn(),
      removeConversation: vi.fn(async () => {}),
      addOrUpdateConversation: sharedMocks.addOrUpdateConversation,
    }),
  };
});

vi.mock('@/composables/useChatModes', async () => {
  const { ref, computed } = await import('vue');
  const modes = ref([
    {
      id: 'default',
      name: 'General Assistant',
      description: 'General',
      systemPrompt: 'You are helpful',
      enabledTools: undefined,
      isSystemDefined: true,
      createdAt: 0,
      updatedAt: 0,
    },
    {
      id: 'math-helper',
      name: 'Math Helper',
      description: 'Math',
      systemPrompt: 'Use calculate',
      enabledTools: ['calculate'],
      isSystemDefined: true,
      createdAt: 0,
      updatedAt: 0,
    },
  ]);

  const currentModeId = ref('default');
  sharedMocks.setModeId = (modeId: string) => {
    currentModeId.value = modeId;
  };

  return {
    useChatModes: () => ({
      modes,
      currentModeId,
      availableTools: ref([]),
      isLoading: ref(sharedMocks.modesLoading),
      loadModes: vi.fn(async () => {}),
      loadTools: vi.fn(async () => {}),
      selectMode: vi.fn((modeId: string) => {
        currentModeId.value = modeId;
        sharedMocks.selectMode(modeId);
      }),
      switchMode: sharedMocks.switchMode,
      createMode: vi.fn(async () => {}),
      updateMode: vi.fn(async () => {}),
      deleteMode: vi.fn(async () => {}),
      copyMode: vi.fn(async () => {}),
      currentMode: computed(() => modes.value[0]),
      systemModes: computed(() => modes.value),
      userModes: computed(() => []),
      error: ref(null),
      getModeById: vi.fn(),
    }),
  };
});

vi.mock('@/composables/useChat', async () => {
  const { ref, computed } = await import('vue');
  return {
    getDisplayText: vi.fn((text: string) => text),
    useChat: (options: { provisionThreadId?: () => Promise<string> }) => {
      // Captured so a test can invoke the provisioning hook the way the real `useChat` does on the
      // first send of a session — the send path, unlike the "New chat" button, is reachable before
      // ChatLayout's mount-time catalog loads have finished.
      sharedMocks.chatOptions = options;
      const threadId = ref<string | null>(sharedMocks.currentThreadId);
      const conversationTodo = ref<unknown>(null);
      // Published (like providerSelectionRef) so a test can mutate them after mount.
      sharedMocks.chatThreadIdRef = threadId;
      sharedMocks.conversationTodoRef = conversationTodo;
      return {
        displayItems: computed(() => []),
        isLoading: ref(sharedMocks.chatLoading),
        isSending: ref(sharedMocks.isSending),
        error: ref(null),
        usage: ref(null),
        cumulativeUsage: ref({
          promptTokens: 0,
          uncachedInputTokens: 0,
          completionTokens: 0,
          totalTokens: 0,
          cachedTokens: 0,
          cacheCreationTokens: 0,
        }),
        // Hoisted useTodoBoard(...) watches this eagerly to establish its dependency, so unlike the
        // lazily-read fields above it must actually be present here (#583).
        conversationTodo,
        pendingMessages: ref([]),
        pendingAuthRequests: computed(() => []),
        dismissAuthRequest: vi.fn(),
        sendMessage: vi.fn(async () => {}),
        clearMessages: vi.fn(),
        cancelStream: vi.fn(async () => {}),
        disconnectWebSocket: sharedMocks.disconnectWebSocket,
        // Hoisted useSubAgentPanel(() => chatThreadId.value) reads this; useConversationTabs watches it.
        threadId,
        setThreadId: sharedMocks.setThreadId,
        loadMessagesFromBackend: vi.fn(async () => {}),
        resumeStreamIfActive: sharedMocks.resumeStreamIfActive,
        markStreamIdle: sharedMocks.markStreamIdle,
        markStreamLoading: sharedMocks.markStreamLoading,
        getResultForToolCall: vi.fn(() => null),
        hasPendingClientQuestion: computed(() => sharedMocks.hasPendingClientQuestion),
        submitClientToolResult: sharedMocks.submitClientToolResult,
      };
    },
  };
});

vi.mock('@/composables/useProviders', async () => {
  const { ref } = await import('vue');
  return {
    useProviders: () => {
      const selectedProviderId = ref<string | null>(sharedMocks.selectedProviderId);
      // Published so a test can let the catalog land LATE — the real composable leaves this ref null
      // until `/api/providers` answers, and the composer is clickable long before that.
      sharedMocks.providerSelectionRef = selectedProviderId;
      return {
        providers: ref([]),
        selectedProviderId,
        isLoading: ref(false),
        loadProviders: vi.fn(() => sharedMocks.providerCatalogLoad),
        settleCatalog: vi.fn(async () => {
          await sharedMocks.providerCatalogLoad;
          return true;
        }),
        selectProvider: sharedMocks.selectProvider,
        switchProvider: sharedMocks.switchProvider,
      };
    },
  };
});

vi.mock('@/composables/useWorkspaces', async () => {
  const { ref } = await import('vue');
  const actual =
    await vi.importActual<typeof import('@/composables/useWorkspaces')>(
      '@/composables/useWorkspaces'
    );
  return {
    // The real constant, not a stand-in: ChatLayout's fallback is only meaningful if it names the
    // id the backend actually seeds.
    DEFAULT_WORKSPACE_ID: actual.DEFAULT_WORKSPACE_ID,
    // Most tests here only care that ChatLayout calls the right function, so they get a flat stub.
    // The conflict-visibility test needs the REAL composable — its `isLoading` flip during the
    // post-409 reload is the whole mechanism under test, and a stub cannot reproduce it honestly.
    useWorkspaces: () => {
      if (sharedMocks.useRealWorkspaces) {
        return actual.useWorkspaces();
      }
      const selectedWorkspaceId = ref<string | null>(sharedMocks.selectedWorkspaceId);
      sharedMocks.workspaceSelectionRef = selectedWorkspaceId;
      return {
        workspaces: ref(sharedMocks.workspaces),
        gateway: ref(sharedMocks.workspaceGateway),
        selectedWorkspaceId,
        isLoading: ref(false),
        loadWorkspaces: vi.fn(() => sharedMocks.workspaceCatalogLoad),
        settleCatalog: vi.fn(async () => {
          await sharedMocks.workspaceCatalogLoad;
          return true;
        }),
        selectWorkspace: sharedMocks.selectWorkspace,
        createWorkspace: sharedMocks.createWorkspace,
        updateWorkspace: sharedMocks.updateWorkspace,
      };
    },
  };
});

vi.mock('@/api/conversationsApi', () => ({
  updateConversationMetadata: vi.fn(async () => {}),
  conversationExists: (threadId: string) => sharedMocks.conversationExists(threadId),
}));

// The sub-agent panel is wired into ChatLayout but exercised by its own tests. Mock the composable so
// mounting ChatLayout doesn't fire real fetch/WebSocket polling (which would reject in jsdom).
vi.mock('@/composables/useSubAgentPanel', async () => {
  const { ref } = await import('vue');
  return {
    useSubAgentPanel: (getParentThreadId?: () => string | null) => {
      // Capture the gating getter so the start-gating tests can evaluate it directly (the panel no
      // longer receives a parentThreadId prop after the #221 tabs refactor).
      if (getParentThreadId) {
        sharedMocks.subAgentThreadGetter = getParentThreadId;
      }
      return {
        children: ref([]),
        focusedAgentId: ref<string | null>(null),
        focusedDisplayItems: ref([]),
        isFocusedStreaming: ref(false),
        error: ref<string | null>(null),
        startPolling: vi.fn(),
        stopPolling: vi.fn(),
        refreshChildren: vi.fn(async () => {}),
        focusChild: vi.fn(async () => {}),
        unfocusChild: vi.fn(async () => {}),
        sendToFocusedChild: vi.fn(),
        submitToFocusedChild: sharedMocks.submitToFocusedChild,
        getResultForToolCall: vi.fn(() => null),
      };
    },
  };
});

describe('ChatLayout mode switching', () => {
  beforeEach(() => {
    sharedMocks.chatLoading = false;
    sharedMocks.isSending = false;
    sharedMocks.modesLoading = false;
    sharedMocks.currentThreadId = 'thread-1';
    // Default to a "started" thread (present in the sidebar) for the existing
    // mode-switch tests. The regression tests below override this per-case.
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1' })];
    sharedMocks.selectMode.mockReset();
    sharedMocks.switchMode.mockReset();
    sharedMocks.disconnectWebSocket.mockReset();
  });

  it('disconnects websocket before switching mode on active conversation', async () => {
    const callOrder: string[] = [];
    sharedMocks.disconnectWebSocket.mockImplementation(async () => {
      callOrder.push('disconnect');
    });
    sharedMocks.switchMode.mockImplementation(async () => {
      callOrder.push('switch');
    });

    const wrapper = mount(ChatLayout, {
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
          ModeSelector: {
            props: ['disabled'],
            template:
              '<button data-test="mode-select" :disabled="disabled" @click="$emit(\'select-mode\', \'math-helper\')">Mode</button>',
          },
        },
      },
    });

    await flushPromises();
    await wrapper.get('[data-test="mode-select"]').trigger('click');
    await flushPromises();

    expect(callOrder).toEqual(['disconnect', 'switch']);
    expect(sharedMocks.switchMode).toHaveBeenCalledWith('thread-1', 'math-helper');
  });

  it('disables mode switching while streaming', async () => {
    sharedMocks.chatLoading = true;

    const wrapper = mount(ChatLayout, {
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
          ModeSelector: {
            props: ['disabled'],
            template:
              '<button data-test="mode-select" :disabled="disabled" @click="$emit(\'select-mode\', \'math-helper\')">Mode</button>',
          },
        },
      },
    });

    await flushPromises();
    const modeButton = wrapper.get('[data-test="mode-select"]');
    expect(modeButton.attributes('disabled')).toBeDefined();

    await modeButton.trigger('click');
    expect(sharedMocks.disconnectWebSocket).not.toHaveBeenCalled();
    expect(sharedMocks.switchMode).not.toHaveBeenCalled();
  });
});

// Regression: selecting a mode before the first message is sent must NOT trigger
// a backend agent recreation. handleNewChat assigns a threadId immediately, so a
// non-null currentThreadId alone is not enough to mean "started" — the thread must
// also have a sidebar entry. Otherwise a workspace picked before the first message
// would be silently overwritten when the backend pre-binds the agent to defaults.
describe('ChatLayout handleSelectMode start-gating regression', () => {
  const mountWithModeSelector = () =>
    mount(ChatLayout, {
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
          ModeSelector: {
            props: ['disabled'],
            template:
              '<button data-test="mode-select" :disabled="disabled" @click="$emit(\'select-mode\', \'math-helper\')">Mode</button>',
          },
        },
      },
    });

  beforeEach(() => {
    sharedMocks.chatLoading = false;
    sharedMocks.isSending = false;
    sharedMocks.modesLoading = false;
    sharedMocks.selectMode.mockReset();
    sharedMocks.switchMode.mockReset();
    sharedMocks.disconnectWebSocket.mockReset();
  });

  it('applies mode locally (no backend switch) on a messageless thread', async () => {
    // currentThreadId is set (handleNewChat assigned it) but the thread is NOT in
    // the sidebar yet -> messageless -> defer to local selectMode.
    sharedMocks.currentThreadId = 'thread-new';
    sharedMocks.conversations = [];

    const wrapper = mountWithModeSelector();
    await flushPromises();
    await wrapper.get('[data-test="mode-select"]').trigger('click');
    await flushPromises();

    expect(sharedMocks.selectMode).toHaveBeenCalledWith('math-helper');
    expect(sharedMocks.switchMode).not.toHaveBeenCalled();
    expect(sharedMocks.disconnectWebSocket).not.toHaveBeenCalled();
  });

  it('switches mode on the backend once the thread has started', async () => {
    // currentThreadId is set AND present in the sidebar (first message sent)
    // -> started -> call backend switchMode.
    sharedMocks.currentThreadId = 'thread-1';
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1' })];

    const wrapper = mountWithModeSelector();
    await flushPromises();
    await wrapper.get('[data-test="mode-select"]').trigger('click');
    await flushPromises();

    expect(sharedMocks.switchMode).toHaveBeenCalledWith('thread-1', 'math-helper');
    expect(sharedMocks.selectMode).not.toHaveBeenCalled();
  });
});

// Provider is mutable while the conversation is idle and locked only while streaming (mirrors mode).
// A messageless thread applies the pick locally; a started conversation switches the backend provider
// and reflects it in the sidebar summary; while streaming the selector is disabled and does neither.
describe('ChatLayout handleSelectProvider start-gating', () => {
  const mountWithProviderSelector = () =>
    mount(ChatLayout, {
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
          ProviderSelector: {
            props: ['disabled'],
            template:
              '<button data-test="provider-select" :disabled="disabled" @click="$emit(\'select-provider\', \'openai\')">Provider</button>',
          },
        },
      },
    });

  beforeEach(() => {
    sharedMocks.chatLoading = false;
    sharedMocks.isSending = false;
    sharedMocks.selectProvider.mockReset();
    sharedMocks.switchProvider.mockReset();
    sharedMocks.disconnectWebSocket.mockReset();
    sharedMocks.addOrUpdateConversation.mockReset();
  });

  it('applies the provider locally (no backend switch) on a messageless thread', async () => {
    sharedMocks.currentThreadId = 'thread-new';
    sharedMocks.conversations = [];

    const wrapper = mountWithProviderSelector();
    await flushPromises();
    await wrapper.get('[data-test="provider-select"]').trigger('click');
    await flushPromises();

    expect(sharedMocks.selectProvider).toHaveBeenCalledWith('openai');
    expect(sharedMocks.switchProvider).not.toHaveBeenCalled();
    expect(sharedMocks.disconnectWebSocket).not.toHaveBeenCalled();
  });

  it('switches the provider on the backend once the thread has started, and updates the summary', async () => {
    sharedMocks.currentThreadId = 'thread-1';
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1', provider: 'test' })];

    const wrapper = mountWithProviderSelector();
    await flushPromises();
    await wrapper.get('[data-test="provider-select"]').trigger('click');
    await flushPromises();

    expect(sharedMocks.disconnectWebSocket).toHaveBeenCalled();
    expect(sharedMocks.switchProvider).toHaveBeenCalledWith('thread-1', 'openai');
    expect(sharedMocks.selectProvider).not.toHaveBeenCalled();
    // The sidebar summary is updated to the new provider so restore-on-refresh reflects it.
    expect(sharedMocks.addOrUpdateConversation).toHaveBeenCalledWith(
      expect.objectContaining({ threadId: 'thread-1', provider: 'openai' })
    );
  });

  it('does nothing while a run is streaming (selector disabled)', async () => {
    sharedMocks.currentThreadId = 'thread-1';
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1', provider: 'test' })];
    sharedMocks.chatLoading = true; // streaming

    const wrapper = mountWithProviderSelector();
    await flushPromises();
    // The stubbed selector button is disabled; invoking the handler directly must also no-op.
    await wrapper.get('[data-test="provider-select"]').trigger('click');
    await flushPromises();

    expect(sharedMocks.switchProvider).not.toHaveBeenCalled();
    expect(sharedMocks.selectProvider).not.toHaveBeenCalled();
  });
});

// BUG 3: opening (or refreshing into) a conversation must restore its bound provider/mode/workspace
// into the header selectors, instead of leaving the process defaults (Anthropic / General Assistant).
// The selectors are process-local, so on a refresh they reset to defaults; handleSelectConversation
// must reflect the conversation's persisted bindings back onto them.
describe('ChatLayout restores bound provider/mode/workspace on conversation select (BUG 3)', () => {
  const mountWithSidebar = () =>
    mount(ChatLayout, {
      global: {
        stubs: {
          ConversationSidebar: {
            template:
              '<button data-test="select-conv" @click="$emit(\'select-conversation\', \'thread-2\')">select</button>',
          },
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
          ModeSelector: true,
          ProviderSelector: true,
          WorkspaceSelector: true,
        },
      },
    });

  beforeEach(() => {
    sharedMocks.chatLoading = false;
    sharedMocks.isSending = false;
    sharedMocks.modesLoading = false;
    // Current thread differs from the one we select, so handleSelectConversation does not early-return.
    sharedMocks.currentThreadId = 'thread-1';
    sharedMocks.conversations = [
      makeConversation({ threadId: 'thread-1' }),
      makeConversation({ threadId: 'thread-2', provider: 'openai', workspace: 'ws-1', mode: 'math-helper' }),
    ];
    sharedMocks.selectMode.mockReset();
    sharedMocks.selectProvider.mockReset();
    sharedMocks.selectWorkspace.mockReset();
    sharedMocks.resumeStreamIfActive.mockReset();
    sharedMocks.resumeStreamIfActive.mockResolvedValue(undefined);
  });

  it('applies the selected conversation provider/mode/workspace to the header selectors', async () => {
    const wrapper = mountWithSidebar();
    await flushPromises();

    await wrapper.get('[data-test="select-conv"]').trigger('click');
    await flushPromises();

    expect(sharedMocks.selectProvider).toHaveBeenCalledWith('openai');
    expect(sharedMocks.selectWorkspace).toHaveBeenCalledWith('ws-1');
    expect(sharedMocks.selectMode).toHaveBeenCalledWith('math-helper');
  });

  it('does not touch the selectors for a legacy conversation with no bindings', async () => {
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1' }), makeConversation({ threadId: 'thread-2' })];

    const wrapper = mountWithSidebar();
    await flushPromises();

    await wrapper.get('[data-test="select-conv"]').trigger('click');
    await flushPromises();

    expect(sharedMocks.selectProvider).not.toHaveBeenCalled();
    expect(sharedMocks.selectWorkspace).not.toHaveBeenCalled();
    expect(sharedMocks.selectMode).not.toHaveBeenCalled();
  });
});

// A ?threadId= deep link lets a caller (e.g. a headless REST integration handing a link back to
// a human) navigate straight to one conversation. Priority over the "select most recent" default,
// and a not-found state when the backend does not have it (never provisioned, deleted, or not
// readable by this caller) — see ChatLayout.vue's getDeepLinkThreadIdFromPageQuery/notFoundThreadId.
//
// Absence from the loaded conversation list is NOT that signal: only page one is loaded, so an older
// conversation is legitimately missing from it. The list is a fast path; the server decides.
describe('ChatLayout ?threadId= deep link', () => {
  const setQuery = (query: string) => {
    window.history.pushState({}, '', query ? `/?${query}` : '/');
  };

  const mountLayout = () =>
    mount(ChatLayout, {
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
        },
      },
    });

  beforeEach(() => {
    sharedMocks.chatLoading = false;
    sharedMocks.isSending = false;
    sharedMocks.modesLoading = false;
    sharedMocks.resumeStreamIfActive.mockReset();
    sharedMocks.resumeStreamIfActive.mockResolvedValue(undefined);
    sharedMocks.conversationExists.mockReset();
    sharedMocks.conversationExists.mockResolvedValue(false);
  });

  afterEach(() => {
    setQuery('');
  });

  it('selects the deep-linked conversation when it exists, in preference to the most recent one', async () => {
    sharedMocks.currentThreadId = 'thread-1';
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1' }), makeConversation({ threadId: 'thread-2' })];
    setQuery('threadId=thread-2');

    const wrapper = mountLayout();
    await flushPromises();

    expect(wrapper.find('[data-testid="conversation-not-found"]').exists()).toBe(false);
    expect(sharedMocks.resumeStreamIfActive).toHaveBeenCalledWith('thread-2');
  });

  /**
   * The reachability bug this whole change exists to fix, in its last remaining form. Every other
   * older conversation became reachable by scrolling; a deep link had no scroll to do, so page-one
   * membership was the only test it got and every older link reported not-found.
   */
  it('opens an older deep-linked conversation that is past the first loaded page', async () => {
    sharedMocks.currentThreadId = null;
    // Page one only. The target is real but older than everything loaded so far.
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1' })];
    sharedMocks.conversationExists.mockResolvedValue(true);
    setQuery('threadId=thread-older');

    const wrapper = mountLayout();
    await flushPromises();

    expect(sharedMocks.conversationExists).toHaveBeenCalledWith('thread-older');
    expect(wrapper.find('[data-testid="conversation-not-found"]').exists()).toBe(false);
    expect(sharedMocks.resumeStreamIfActive).toHaveBeenCalledWith('thread-older');
  });

  it('shows a not-found state for a deep-linked thread the backend does not have', async () => {
    sharedMocks.currentThreadId = null;
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1' })];
    setQuery('threadId=thread-missing');

    const wrapper = mountLayout();
    await flushPromises();

    const notFound = wrapper.find('[data-testid="conversation-not-found"]');
    expect(notFound.exists()).toBe(true);
    expect(notFound.text()).toContain('thread-missing');
    // Must not silently fall back to the most recent conversation instead.
    expect(sharedMocks.resumeStreamIfActive).not.toHaveBeenCalled();
  });

  it('clears the not-found state and starts a new chat when "Start a new chat" is clicked', async () => {
    sharedMocks.currentThreadId = null;
    sharedMocks.conversations = [];
    setQuery('threadId=thread-missing');

    const wrapper = mountLayout();
    await flushPromises();
    expect(wrapper.find('[data-testid="conversation-not-found"]').exists()).toBe(true);

    await wrapper.get('.new-chat-btn').trigger('click');
    await flushPromises();

    expect(wrapper.find('[data-testid="conversation-not-found"]').exists()).toBe(false);
  });

  it('falls back to selecting the most recent conversation when no ?threadId is present', async () => {
    sharedMocks.currentThreadId = null;
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1' }), makeConversation({ threadId: 'thread-2' })];
    setQuery('');

    const wrapper = mountLayout();
    await flushPromises();

    expect(wrapper.find('[data-testid="conversation-not-found"]').exists()).toBe(false);
    expect(sharedMocks.resumeStreamIfActive).toHaveBeenCalledWith('thread-1');
  });
});

// Regression: a freshly-created chat (handleNewChat) assigns useChat's threadId immediately,
// well before any message is sent, but the backend's agent pool has no entry for that thread
// yet. Feeding that id straight to useSubAgentPanel made it poll listSubAgents immediately and
// hit a 404 ("unknown_thread") on every new conversation. After the #221 tabs refactor the gating
// moved off a SubAgentListPanel prop onto the getter ChatLayout passes to useSubAgentPanel
// (`() => subAgentParentThreadId.value`), which stays null until the conversation has a sidebar
// entry (added by handleSend only after the first message is dispatched) — mirroring the same
// start-gating used for mode and provider switching above.
describe('ChatLayout sub-agent panel start-gating', () => {
  const mountLayout = () =>
    mount(ChatLayout, {
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
        },
      },
    });

  beforeEach(() => {
    sharedMocks.chatLoading = false;
    sharedMocks.isSending = false;
    sharedMocks.modesLoading = false;
    sharedMocks.subAgentThreadGetter = null;
  });

  it('withholds the thread id from the sub-agent panel on a fresh, messageless conversation', async () => {
    sharedMocks.currentThreadId = 'thread-new';
    sharedMocks.conversations = [];

    mountLayout();
    await flushPromises();

    expect(sharedMocks.subAgentThreadGetter).not.toBeNull();
    expect(sharedMocks.subAgentThreadGetter!()).toBeNull();
  });

  it('passes the thread id to the sub-agent panel once the conversation has a sidebar entry', async () => {
    sharedMocks.currentThreadId = 'thread-1';
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1' })];

    mountLayout();
    await flushPromises();

    expect(sharedMocks.subAgentThreadGetter).not.toBeNull();
    expect(sharedMocks.subAgentThreadGetter!()).toBe('thread-1');
  });
});

// #246: a deferred client tool (e.g. AskUserQuestion) must lock the mode/provider selectors (an
// answer, once submitted, is bound to a specific mode/provider context) the same way an in-flight
// stream does — but must NOT touch the composer (ordinary chat message queuing stays available
// while a question is pending, per the coordinator's contract).
describe('ChatLayout client-tool question gating (#246)', () => {
  const mountWithSelectors = () =>
    mount(ChatLayout, {
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
          ModeSelector: {
            props: ['disabled'],
            template: '<button data-test="mode-select" :disabled="disabled">Mode</button>',
          },
          ProviderSelector: {
            props: ['disabled'],
            template: '<button data-test="provider-select" :disabled="disabled">Provider</button>',
          },
        },
      },
    });

  beforeEach(() => {
    sharedMocks.chatLoading = false;
    sharedMocks.isSending = false;
    sharedMocks.modesLoading = false;
    sharedMocks.currentThreadId = 'thread-1';
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1' })];
    sharedMocks.hasPendingClientQuestion = false;
  });

  it('leaves the mode/provider selectors enabled while idle with no pending question', async () => {
    const wrapper = mountWithSelectors();
    await flushPromises();

    expect(wrapper.get('[data-test="mode-select"]').attributes('disabled')).toBeUndefined();
    expect(wrapper.get('[data-test="provider-select"]').attributes('disabled')).toBeUndefined();
  });

  it('disables the mode selector while a client question is pending, even though the run is idle', async () => {
    sharedMocks.hasPendingClientQuestion = true;

    const wrapper = mountWithSelectors();
    await flushPromises();

    expect(wrapper.get('[data-test="mode-select"]').attributes('disabled')).toBeDefined();
  });

  it('disables the provider selector while a client question is pending, even though the run is idle', async () => {
    sharedMocks.hasPendingClientQuestion = true;

    const wrapper = mountWithSelectors();
    await flushPromises();

    expect(wrapper.get('[data-test="provider-select"]').attributes('disabled')).toBeDefined();
  });
});

// #246: ChatLayout must provide submitClientToolResult under SUBMIT_CLIENT_TOOL_RESULT, mirroring
// the existing getResultForToolCall provide, so a descendant "rich" tool component (QuestionRich.vue,
// via useClientToolSubmit()) can resolve a deferred AskUserQuestion without prop-drilling through
// MessageList/ToolPill.
describe('ChatLayout provides SUBMIT_CLIENT_TOOL_RESULT to descendants (#246)', () => {
  it('injects useChat.submitClientToolResult for a descendant to call', async () => {
    let injectedSubmit: ClientToolSubmitFn | undefined;
    const Probe = defineComponent({
      setup() {
        injectedSubmit = inject<ClientToolSubmitFn>(SUBMIT_CLIENT_TOOL_RESULT);
        return () => h('div', { 'data-test': 'probe' });
      },
    });

    sharedMocks.chatLoading = false;
    sharedMocks.isSending = false;
    sharedMocks.modesLoading = false;
    sharedMocks.currentThreadId = 'thread-1';
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1' })];
    sharedMocks.submitClientToolResult.mockClear();

    mount(ChatLayout, {
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: Probe,
          PendingMessageQueue: true,
          ChatInput: true,
        },
      },
    });
    await flushPromises();

    expect(injectedSubmit).toBe(sharedMocks.submitClientToolResult);

    await injectedSubmit?.('call-1', '{"answers":[]}', false);
    expect(sharedMocks.submitClientToolResult).toHaveBeenCalledWith('call-1', '{"answers":[]}', false);
  });
});

// #246: a client-notification pill (NotificationPill.vue) reports a descendant blocked on a
// browser-hosted client tool. Clicking it must jump the center pane to that descendant's tab —
// wired via GO_TO_AGENT_TAB -> useConversationTabs' selectTab, mirroring the GET_AGENT_COLOR /
// GET_AGENT_ROUTING provides already on ChatLayout.
describe('ChatLayout provides GO_TO_AGENT_TAB to descendants (#246)', () => {
  it('injects a function that switches the center pane away from the main tab', async () => {
    let goToAgentTab: GoToAgentTab | undefined;
    const Probe = defineComponent({
      setup() {
        goToAgentTab = inject<GoToAgentTab>(GO_TO_AGENT_TAB);
        return () => h('div', { 'data-test': 'probe' });
      },
    });

    sharedMocks.chatLoading = false;
    sharedMocks.isSending = false;
    sharedMocks.modesLoading = false;
    sharedMocks.currentThreadId = 'thread-1';
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1' })];

    const wrapper = mount(ChatLayout, {
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: Probe,
          PendingMessageQueue: true,
          ChatInput: true,
        },
      },
    });
    await flushPromises();

    expect(typeof goToAgentTab).toBe('function');

    goToAgentTab?.('agent-42');
    await flushPromises();

    const mainView = wrapper.get('[data-testid="main-view"]');
    expect((mainView.element as HTMLElement).style.display).toBe('none');
  });
});

// #246 defect 1: a descendant's AskUserQuestion must submit over the FOCUSED sub-agent's own
// connection (useSubAgentPanel.submitToFocusedChild), not the root's submitClientToolResult — the
// root doesn't know the descendant's toolCallId and would reply not_found. ChatLayout must bind
// SubAgentTranscript's submit-client-tool-result prop to submitToFocusedChild once the center pane
// switches to a sub-agent tab.
describe('ChatLayout binds SubAgentTranscript to the focused-child submit (#246 defect 1)', () => {
  it('provides submitToFocusedChild (not the root submit) inside the sub-agent tab subtree', async () => {
    let injectedSubmit: ClientToolSubmitFn | undefined;
    let goToAgentTab: GoToAgentTab | undefined;
    // Mounted as MessageList in BOTH the main pane and (once activated) SubAgentTranscript. Each
    // mount re-runs setup() in its own provide scope, so the LAST-mounted instance's inject wins —
    // that's SubAgentTranscript's, once we switch tabs below.
    const Probe = defineComponent({
      setup() {
        injectedSubmit = inject<ClientToolSubmitFn>(SUBMIT_CLIENT_TOOL_RESULT);
        goToAgentTab = inject<GoToAgentTab>(GO_TO_AGENT_TAB);
        return () => h('div', { 'data-test': 'probe' });
      },
    });

    sharedMocks.chatLoading = false;
    sharedMocks.isSending = false;
    sharedMocks.modesLoading = false;
    sharedMocks.currentThreadId = 'thread-1';
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1' })];
    sharedMocks.submitClientToolResult.mockClear();
    sharedMocks.submitToFocusedChild.mockClear();

    mount(ChatLayout, {
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: Probe,
          PendingMessageQueue: true,
          ChatInput: true,
        },
      },
    });
    await flushPromises();

    // Before switching tabs, the main pane's MessageList (outside SubAgentTranscript) injected the
    // root submit — sanity-checked fully by the SUBMIT_CLIENT_TOOL_RESULT describe above.
    expect(injectedSubmit).toBe(sharedMocks.submitClientToolResult);

    goToAgentTab?.('agent-42');
    await flushPromises();

    // Once the sub-agent tab is active, SubAgentTranscript mounts its OWN MessageList, which injects
    // the CHILD-scoped submit (submitToFocusedChild) instead — never the root's.
    expect(injectedSubmit).toBe(sharedMocks.submitToFocusedChild);
    expect(injectedSubmit).not.toBe(sharedMocks.submitClientToolResult);

    await injectedSubmit?.('call-1', '{"answers":[]}', false);
    expect(sharedMocks.submitToFocusedChild).toHaveBeenCalledWith('call-1', '{"answers":[]}', false);
    expect(sharedMocks.submitClientToolResult).not.toHaveBeenCalled();
  });
});

// The workspace form has NO global error banner: the parent catches and the child renders
// (handleCreateWorkspace / handleUpdateWorkspace -> workspaceSelectorRef.value?.showFormError).
// Both new typed failures — HTTP 409 workspace_revision_conflict and HTTP 400 unsupported_plugins —
// must arrive at the child with their actionable detail intact. Two ways that silently breaks:
//   * the catch extracts the wrong property (e.g. `e.name`) or falls back to its generic string, so
//     the user sees "Failed to update workspace" and the plugin names / staleness hint are gone;
//   * `workspaceSelectorRef.value` is null, in which case `?.` makes the ENTIRE call vanish without
//     throwing — so these tests assert POSITIVELY that showFormError was called, never merely that
//     nothing threw. An assertion phrased as "did not throw" would pass vacuously in exactly the
//     case it is meant to catch.
describe('ChatLayout surfaces workspace plugin-selection failures inline', () => {
  // Options-API stub, deliberately NOT `<script setup>`: the template ref then resolves to the
  // public instance proxy, so `methods` are reachable the same way the real component's
  // defineExpose'd showFormError/closeForm are.
  const WorkspaceSelectorStub = defineComponent({
    emits: ['create-workspace', 'update-workspace'],
    methods: {
      showFormError(message: string) {
        sharedMocks.showFormError(message);
      },
      closeForm() {
        sharedMocks.closeForm();
      },
      reseedEditForm() {
        sharedMocks.reseedEditForm();
      },
    },
    template: `<div>
      <button data-test="ws-create" @click="$emit('create-workspace', { name: 'New WS', marketplaces: ['demo'] })"></button>
      <button data-test="ws-update" @click="$emit('update-workspace', 'ws-user', { marketplaces: ['demo'], pluginSelection: [], pluginsRevision: 1 })"></button>
    </div>`,
  });

  const mountLayout = () =>
    mount(ChatLayout, {
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
          WorkspaceSelector: WorkspaceSelectorStub,
        },
      },
    });

  beforeEach(() => {
    sharedMocks.chatLoading = false;
    sharedMocks.isSending = false;
    sharedMocks.modesLoading = false;
    sharedMocks.currentThreadId = 'thread-1';
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1' })];
    sharedMocks.createWorkspace.mockReset();
    sharedMocks.updateWorkspace.mockReset();
    sharedMocks.showFormError.mockReset();
    sharedMocks.closeForm.mockReset();
    sharedMocks.reseedEditForm.mockReset();
  });

  const conflictMessage =
    'This workspace was changed elsewhere, so your plugin selection was not saved. '
    + 'The form has been reloaded with the current selection and your pending change was '
    + 'discarded — re-apply it and save again.';

  /**
   * RED when the catch's message extraction is mutated (verified: `e.message` -> `e.name` yields
   * "WorkspaceRevisionConflictError", and hard-coding the generic fallback yields "Failed to update
   * workspace" — both fail the substring assertions below).
   */
  it('routes a 409 revision conflict to the inline form error with the staleness hint intact', async () => {
    sharedMocks.updateWorkspace.mockRejectedValueOnce(
      new WorkspaceRevisionConflictError(conflictMessage, 1, 4)
    );

    const wrapper = mountLayout();
    await flushPromises();
    await wrapper.get('[data-test="ws-update"]').trigger('click');
    await flushPromises();

    // Positive assertion: the optional-chained call actually happened (the ref resolved).
    expect(sharedMocks.showFormError).toHaveBeenCalledTimes(1);
    const message = sharedMocks.showFormError.mock.calls[0][0] as string;
    expect(message).toContain('changed elsewhere');
    expect(message).toContain('discarded');
    // The form stays open so the message is visible and the user can re-apply.
    expect(sharedMocks.closeForm).not.toHaveBeenCalled();
  });

  /**
   * F2 (lost update). The composable's 409 branch reloads the workspace list, which hands the NEXT
   * save a fresh CAS token. If the open form still holds the pre-conflict selection at that point,
   * one more click passes compare-and-swap and silently overwrites whatever the other writer stored.
   * The parent must therefore re-seed the form from the refreshed workspace before it shows the
   * error — and the message it shows must say the pending change was dropped.
   *
   * Mutation proving non-vacuity: delete `workspaceSelectorRef.value?.reseedEditForm()` from
   * ChatLayout's `handleUpdateWorkspace` catch -> RED here (0 calls), everything else green.
   */
  it('re-seeds the edit form from the refreshed workspace after a 409, before showing the error', async () => {
    sharedMocks.updateWorkspace.mockRejectedValueOnce(
      new WorkspaceRevisionConflictError(conflictMessage, 1, 4)
    );

    const wrapper = mountLayout();
    await flushPromises();
    await wrapper.get('[data-test="ws-update"]').trigger('click');
    await flushPromises();

    expect(sharedMocks.reseedEditForm).toHaveBeenCalledTimes(1);
    // Order matters: re-seeding after showFormError would clear nothing, but re-seeding must not
    // wipe the error either — the message is what makes the discard honest rather than silent.
    expect(
      sharedMocks.reseedEditForm.mock.invocationCallOrder[0]
    ).toBeLessThan(sharedMocks.showFormError.mock.invocationCallOrder[0]);
  });

  /**
   * The re-seed is 409-ONLY. A 400 means the server stored nothing and our revision is still current,
   * so the user's pending selection is still valid and must survive for them to correct it. RED if
   * the re-seed is hoisted out of the `instanceof WorkspaceRevisionConflictError` branch.
   */
  it('does not re-seed the edit form for a non-conflict failure', async () => {
    sharedMocks.updateWorkspace.mockRejectedValueOnce(
      new UnsupportedPluginsError(
        'These plugins are not available in the selected marketplaces: demo/ghost.',
        ['demo/ghost']
      )
    );

    const wrapper = mountLayout();
    await flushPromises();
    await wrapper.get('[data-test="ws-update"]').trigger('click');
    await flushPromises();

    expect(sharedMocks.showFormError).toHaveBeenCalledTimes(1);
    expect(sharedMocks.reseedEditForm).not.toHaveBeenCalled();
  });

  it('routes a 400 unsupported_plugins to the inline form error, naming the offending plugin', async () => {
    sharedMocks.createWorkspace.mockRejectedValueOnce(
      new UnsupportedPluginsError(
        'These plugins are not available in the selected marketplaces: demo/ghost.',
        ['demo/ghost']
      )
    );

    const wrapper = mountLayout();
    await flushPromises();
    await wrapper.get('[data-test="ws-create"]').trigger('click');
    await flushPromises();

    expect(sharedMocks.showFormError).toHaveBeenCalledTimes(1);
    expect(sharedMocks.showFormError.mock.calls[0][0]).toContain('demo/ghost');
    expect(sharedMocks.closeForm).not.toHaveBeenCalled();
  });

  it('closes the form and reports nothing when the save succeeds', async () => {
    const wrapper = mountLayout();
    await flushPromises();
    await wrapper.get('[data-test="ws-update"]').trigger('click');
    await flushPromises();

    expect(sharedMocks.closeForm).toHaveBeenCalledTimes(1);
    expect(sharedMocks.showFormError).not.toHaveBeenCalled();
  });
});

/**
 * F6. Every test above stubs `WorkspaceSelector` and `useWorkspaces`, so they can only observe that
 * ChatLayout CALLED `showFormError` — never that the component was still mounted to render it. It
 * was not: `useWorkspaces.updateWorkspace` awaits `loadWorkspaces()` inside its 409 branch, which
 * raises `isLoading`; `workspaceSelectorDisabled` folded that flag in; and WorkspaceSelector's
 * `watch([disabled, lockedWorkspaceId])` called `closeDropdown()` on the rising edge. By the time
 * the catch ran, the dropdown was gone — `reseedEditForm()` bailed (`formMode` was no longer
 * `'edit'`) and `showFormError()` set a field on nothing. The user's edit vanished silently.
 *
 * Same class as F5: a control unmounted out from under the code about to act on it. F5 was
 * triggered by a click, this by a reactive flag.
 *
 * This test therefore mocks NOTHING between ChatLayout and the DOM except `fetch`: the real
 * `useWorkspaces`, the real `workspacesApi` (so the 409 body is genuinely parsed into
 * `WorkspaceRevisionConflictError`), the real `WorkspaceSelector` and its real watcher.
 */
describe('ChatLayout keeps the workspace edit form alive across a 409 (F6)', () => {
  const gateway = { canonicalBaseUrl: 'http://gw', appId: 'app', available: true, error: null };

  const workspaceAt = (revision: number, plugin: string) => ({
    id: 'ws-user',
    name: 'My Project',
    directoryRelPath: 'my-project',
    marketplaces: ['demo'],
    isSystemDefined: false,
    createdAt: 0,
    updatedAt: 0,
    compatibility: 'compatible',
    unsupportedMarketplaces: [],
    pluginSelection: [{ marketplace: 'demo', plugin }],
    pluginsRevision: revision,
  });

  const catalog = {
    selected: ['demo'],
    marketplaces: [
      {
        alias: 'demo',
        error: null,
        plugins: [
          { name: 'toolkit', version: null, description: '', skills: [], agents: [] },
          { name: 'extras', version: null, description: '', skills: [], agents: [] },
        ],
      },
    ],
    capabilities: { pluginFiltering: true },
  };

  const json = (body: unknown, status = 200) =>
    Promise.resolve({
      ok: status >= 200 && status < 300,
      status,
      json: () => Promise.resolve(body),
    } as Response);

  let fetchMock: ReturnType<typeof vi.fn>;
  let listCalls = 0;
  let putBodies: Array<Record<string, unknown>> = [];

  beforeEach(() => {
    sharedMocks.chatLoading = false;
    sharedMocks.isSending = false;
    sharedMocks.modesLoading = false;
    sharedMocks.currentThreadId = 'thread-1';
    // No `workspace` on the summary: the selector must render as an editable dropdown, not a
    // locked badge (a locked selector would hide the form for an unrelated reason).
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1' })];
    sharedMocks.useRealWorkspaces = true;
    listCalls = 0;
    putBodies = [];

    fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.startsWith('/api/marketplaces')) return json(catalog);
      if (url.startsWith('/api/workspaces/') && init?.method === 'PUT') {
        const body = JSON.parse(String(init.body)) as Record<string, unknown>;
        putBodies.push(body);
        // Only the FIRST save conflicts. A retry is served by a server that accepts the current
        // revision — so a test can tell "the reload made the retry viable" apart from "the retry is
        // rejected forever", which is what a missing/deferred reload would produce.
        if (putBodies.length === 1) {
          return json(
            {
              code: 'workspace_revision_conflict',
              message: 'stale revision',
              expectedRevision: 1,
              actualRevision: 4,
            },
            409
          );
        }
        if (body.pluginsRevision !== 4) {
          return json(
            {
              code: 'workspace_revision_conflict',
              message: 'stale revision replayed',
              expectedRevision: body.pluginsRevision,
              actualRevision: 4,
            },
            409
          );
        }
        return json(workspaceAt(5, 'extras'));
      }
      if (url === '/api/workspaces') {
        listCalls += 1;
        // First list = what the form is seeded from. Every later list is the post-conflict reload,
        // which returns the OTHER writer's selection at a newer revision.
        return json({
          gateway,
          workspaces: [listCalls === 1 ? workspaceAt(1, 'toolkit') : workspaceAt(4, 'extras')],
        });
      }
      return json({});
    });
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    sharedMocks.useRealWorkspaces = false;
    vi.unstubAllGlobals();
  });

  it('leaves the edit form mounted with the conflict message visible in the DOM', async () => {
    const wrapper = mount(ChatLayout, {
      attachTo: document.body,
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
          ModeSelector: true,
          ProviderSelector: true,
          // WorkspaceSelector deliberately NOT stubbed — its watcher is the code under test.
        },
      },
    });
    await flushPromises();

    await wrapper.get('[data-testid="workspace-selector-button"]').trigger('click');
    await flushPromises();
    await wrapper.get('[data-testid="workspace-edit-ws-user"]').trigger('click');
    await flushPromises();

    // Sanity: the form is open and seeded from revision 1 before anything can go wrong.
    expect(wrapper.find('[data-testid="workspace-edit-form"]').exists()).toBe(true);
    expect(
      wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-toolkit"]').element
        .checked
    ).toBe(true);

    // A genuine plugin change, so the PUT actually carries pluginSelection + pluginsRevision.
    await wrapper.get('[data-testid="workspace-edit-plugin-demo-extras"]').trigger('change');
    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await flushPromises();
    await flushPromises();

    // The PUT really happened and really came back 409.
    expect(fetchMock.mock.calls.some(([, init]) => (init as RequestInit)?.method === 'PUT')).toBe(
      true
    );
    expect(listCalls).toBeGreaterThan(1);

    // THE POINT: the dropdown and the form survived the transient `isLoading` flip...
    expect(wrapper.find('.dropdown-menu').exists()).toBe(true);
    expect(wrapper.find('[data-testid="workspace-edit-form"]').exists()).toBe(true);
    // ...the message is actually RENDERED, not merely handed to a component that no longer exists...
    const error = wrapper.get('[data-testid="workspace-form-error"]');
    expect(error.text()).toContain('changed elsewhere');
    expect(error.text()).toContain('discarded');
    // ...and the re-seed reached real form state: the OTHER writer's selection is now displayed.
    expect(
      wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-toolkit"]').element
        .checked
    ).toBe(false);
    expect(
      wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-extras"]').element
        .checked
    ).toBe(true);

    // ATTRIBUTABILITY: the revision we sent is exactly the one the form was seeded with, so the
    // conflict is caused by the out-of-band write to revision 4 and not by incidental staleness.
    expect(putBodies).toHaveLength(1);
    expect(putBodies[0].pluginsRevision).toBe(1);
    expect(putBodies[0].pluginSelection).toEqual([
      { marketplace: 'demo', plugin: 'toolkit' },
      { marketplace: 'demo', plugin: 'extras' },
    ]);

    wrapper.unmount();
  });

  /**
   * The 409 branch's `await loadWorkspaces()` is what makes a RETRY viable: it is the only thing
   * that gives the form a current `pluginsRevision`. Without it the user replays the stale revision
   * forever — an unbreakable conflict loop, strictly worse than the silence F6 fixed. Asserted
   * separately from the message so a future change cannot trade one for the other; the mock rejects
   * ANY second PUT that does not carry the refreshed revision.
   */
  it('lets the user re-apply the change and save successfully after the 409', async () => {
    const wrapper = mount(ChatLayout, {
      attachTo: document.body,
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
          ModeSelector: true,
          ProviderSelector: true,
        },
      },
    });
    await flushPromises();

    await wrapper.get('[data-testid="workspace-selector-button"]').trigger('click');
    await flushPromises();
    await wrapper.get('[data-testid="workspace-edit-ws-user"]').trigger('click');
    await flushPromises();
    await wrapper.get('[data-testid="workspace-edit-plugin-demo-extras"]').trigger('change');
    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await flushPromises();
    await flushPromises();

    // Precondition for the retry: the conflict was reported and the form is still usable.
    expect(wrapper.get('[data-testid="workspace-form-error"]').text()).toContain('discarded');
    expect(wrapper.find('[data-testid="workspace-edit-form"]').exists()).toBe(true);

    // The user re-applies what was discarded (toolkit back on, alongside the other writer's extras)
    // and saves again. This must reach the server carrying revision 4, not the replayed 1.
    await wrapper.get('[data-testid="workspace-edit-plugin-demo-toolkit"]').trigger('change');
    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await flushPromises();
    await flushPromises();

    expect(putBodies).toHaveLength(2);
    expect(putBodies[1].pluginsRevision).toBe(4);
    expect(putBodies[1].pluginSelection).toEqual([
      { marketplace: 'demo', plugin: 'extras' },
      { marketplace: 'demo', plugin: 'toolkit' },
    ]);
    // Success closes the form and clears the error — not a second conflict.
    expect(wrapper.find('[data-testid="workspace-edit-form"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="workspace-form-error"]').exists()).toBe(false);

    wrapper.unmount();
  });

  /**
   * The 400 path never calls `loadWorkspaces()` (only the 409 branch does), so it never raises
   * `isLoading` and was never affected. Pinned so a later "reload on every failure" refactor cannot
   * reintroduce the teardown here unnoticed.
   */
  it('leaves the edit form mounted with the message visible for a 400 unsupported_plugins', async () => {
    fetchMock.mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.startsWith('/api/marketplaces')) return json(catalog);
      if (url === '/api/workspaces') {
        listCalls += 1;
        return json({ gateway, workspaces: [workspaceAt(1, 'toolkit')] });
      }
      if (url.startsWith('/api/workspaces/') && init?.method === 'PUT') {
        return json(
          {
            code: 'unsupported_plugins',
            message: 'These plugins are not available in the selected marketplaces: demo/ghost.',
            unsupportedPlugins: ['demo/ghost'],
          },
          400
        );
      }
      return json({});
    });

    const wrapper = mount(ChatLayout, {
      attachTo: document.body,
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
          ModeSelector: true,
          ProviderSelector: true,
        },
      },
    });
    await flushPromises();

    await wrapper.get('[data-testid="workspace-selector-button"]').trigger('click');
    await flushPromises();
    await wrapper.get('[data-testid="workspace-edit-ws-user"]').trigger('click');
    await flushPromises();
    await wrapper.get('[data-testid="workspace-edit-plugin-demo-extras"]').trigger('change');
    await wrapper.get('[data-testid="workspace-edit-form"]').trigger('submit');
    await flushPromises();
    await flushPromises();

    expect(wrapper.find('[data-testid="workspace-edit-form"]').exists()).toBe(true);
    expect(wrapper.get('[data-testid="workspace-form-error"]').text()).toContain('demo/ghost');
    // No reload on this path, so the user's pending selection is still theirs to correct.
    expect(listCalls).toBe(1);
    expect(
      wrapper.get<HTMLInputElement>('[data-testid="workspace-edit-plugin-demo-extras"]').element
        .checked
    ).toBe(true);

    wrapper.unmount();
  });

  /**
   * The CREATE path reloads on SUCCESS (`createWorkspace` -> `loadWorkspaces`), which also flips
   * `isLoading` — harmless there, since the form is closing anyway. On FAILURE it does not reload at
   * all, so the error is visible. Pinned because "create also reloads" is the obvious place to
   * assume the same bug exists.
   */
  it('leaves the create form mounted with the message visible when creation fails', async () => {
    fetchMock.mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.startsWith('/api/marketplaces')) return json(catalog);
      if (url === '/api/workspaces' && init?.method === 'POST') {
        return json(
          {
            code: 'unsupported_plugins',
            message: 'These plugins are not available in the selected marketplaces: demo/ghost.',
            unsupportedPlugins: ['demo/ghost'],
          },
          400
        );
      }
      if (url === '/api/workspaces') {
        listCalls += 1;
        return json({ gateway, workspaces: [workspaceAt(1, 'toolkit')] });
      }
      return json({});
    });

    const wrapper = mount(ChatLayout, {
      attachTo: document.body,
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
          ModeSelector: true,
          ProviderSelector: true,
        },
      },
    });
    await flushPromises();

    await wrapper.get('[data-testid="workspace-selector-button"]').trigger('click');
    await flushPromises();
    await wrapper.get('[data-testid="workspace-create-open"]').trigger('click');
    await flushPromises();
    await wrapper.get('[data-testid="workspace-create-name"]').setValue('New WS');
    await wrapper.get('[data-testid="workspace-create-form"]').trigger('submit');
    await flushPromises();
    await flushPromises();

    expect(wrapper.find('[data-testid="workspace-create-form"]').exists()).toBe(true);
    expect(wrapper.get('[data-testid="workspace-form-error"]').text()).toContain('demo/ghost');

    wrapper.unmount();
  });
});

// #435. "New chat" used to mint a thread id in the browser and hand it straight to useChat, which
// opened a socket on it. Under `Identity:Enforce=true` that id has no metadata row and `/ws` refuses
// the handshake — byte-identically to a thread somebody else owns, so there is nothing in the
// refusal to act on. The component must reserve the conversation on the server first and adopt the
// id the server minted, and must never fall back to one of its own when that fails.
describe('ChatLayout new-chat provisioning (#435)', () => {
  // The "Start a new chat" button lives in the not-found panel, so a deep link to an unknown
  // thread is the cheapest way to get a real click on handleNewChat.
  const setQuery = (query: string) => {
    window.history.pushState({}, '', query ? `/?${query}` : '/');
  };

  const mountLayout = () =>
    mount(ChatLayout, {
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
        },
      },
    });

  beforeEach(() => {
    sharedMocks.currentThreadId = null;
    sharedMocks.conversations = [];
    sharedMocks.selectedProviderId = 'anthropic';
    sharedMocks.selectedWorkspaceId = 'ws-1';
    sharedMocks.createNewConversation.mockReset();
    sharedMocks.createNewConversation.mockResolvedValue('thread-provisioned');
    sharedMocks.setThreadId.mockReset();
    sharedMocks.setModeId?.('default');
    setQuery('threadId=thread-missing');
  });

  afterEach(() => {
    sharedMocks.selectedProviderId = null;
    sharedMocks.selectedWorkspaceId = 'default';
    sharedMocks.providerCatalogLoad = Promise.resolve();
    sharedMocks.workspaceCatalogLoad = Promise.resolve();
  });

  it('provisions with the current workspace/provider/mode and adopts the server-minted id', async () => {
    mountLayout();
    await flushPromises();

    const provisioned = await sharedMocks.chatOptions!.provisionThreadId!();

    expect(sharedMocks.createNewConversation).toHaveBeenCalledWith({
      workspaceId: 'ws-1',
      providerId: 'anthropic',
      modeId: 'default',
    });
    // The SERVER's id is what the socket will be opened on.
    expect(provisioned).toBe('thread-provisioned');
  });

  it('refuses rather than falling back to an id of its own when the server will not reserve one', async () => {
    sharedMocks.createNewConversation.mockRejectedValue(
      new Error('Provider "anthropic" is currently unavailable.')
    );

    mountLayout();
    await flushPromises();

    // The refusal has to reach useChat, which is what surfaces it and dequeues the pending prompt.
    // Answering with an id of our own would open a socket that connects today and is refused the
    // moment enforcement is flipped on — worse than a send that visibly failed.
    await expect(sharedMocks.chatOptions!.provisionThreadId!()).rejects.toThrow(
      /currently unavailable/
    );
    for (const call of sharedMocks.setThreadId.mock.calls) {
      expect(call[0]).toBeNull();
    }
  });

  // A null selection means the catalog listed nothing this client could choose — an empty list, or
  // one whose every entry the gateway checked and refused. Provisioning must not be stricter than
  // the socket it replaced, which sent whatever it had and let the backend resolve its own default.
  //
  // Since #459 an UNREADABLE catalog no longer lands here: it reports `unavailable` rather than
  // `unknown`, and `useWorkspaces` keeps the user's selection through it (see
  // "empty picker cannot clobber a stored workspace binding" in useWorkspaces.test.ts) precisely so
  // this default-substitution cannot silently rewrite a binding the user had already made. The
  // 'ws-1' case above is the other half: a selection that survives is the one that reaches the write.
  it('falls back to the default workspace when the catalog can vouch for none', async () => {
    sharedMocks.selectedWorkspaceId = null;

    mountLayout();
    await flushPromises();

    const provisioned = await sharedMocks.chatOptions!.provisionThreadId!();

    expect(sharedMocks.createNewConversation).toHaveBeenCalledWith({
      workspaceId: 'default',
      providerId: 'anthropic',
      modeId: 'default',
    });
    expect(provisioned).toBe('thread-provisioned');
  });

  // The provider is a different matter: a null there means the catalog could not be read at all
  // (`loadProviders` falls back to the backend's declared default even when nothing is available),
  // and the backend answers 503 for a provider it cannot serve.
  it('refuses when no provider could be resolved at all', async () => {
    sharedMocks.selectedProviderId = null;

    mountLayout();
    await flushPromises();

    await expect(sharedMocks.chatOptions!.provisionThreadId!()).rejects.toThrow(/provider/);
    expect(sharedMocks.createNewConversation).not.toHaveBeenCalled();
  });

  // Reserving on click would write a metadata row per "New chat", and GET /api/conversations lists
  // every row: the sidebar fills with empty "New Conversation" entries, and after a reload the
  // most-recent-first default selects the newest of THOSE instead of the conversation the user was
  // reading. A blank chat is a cleared selection, not a reservation.
  it('reserves nothing when the user opens a new chat', async () => {
    const wrapper = mountLayout();
    await flushPromises();
    sharedMocks.setThreadId.mockReset();

    await wrapper.get('.new-chat-btn').trigger('click');
    await flushPromises();

    expect(sharedMocks.createNewConversation).not.toHaveBeenCalled();
    expect(sharedMocks.setThreadId).toHaveBeenCalledWith(null);
  });

  // The regression that took the browser suite down: `/api/providers` is fetched on mount but the
  // composer is live from the first paint, so a conversation started in that window found
  // `selectedProviderId` still null and was refused — with a message telling the user to choose a
  // provider from a picker that had not been populated yet. Provisioning has to wait for the load
  // that will win before it decides anything is missing.
  it('waits for a provider catalog still in flight instead of refusing the conversation', async () => {
    sharedMocks.selectedProviderId = null;
    let landCatalog!: () => void;
    sharedMocks.providerCatalogLoad = new Promise<void>((resolve) => {
      landCatalog = () => {
        // What the real composable does when the response applies: pick the default provider.
        if (sharedMocks.providerSelectionRef) {
          sharedMocks.providerSelectionRef.value = 'anthropic';
        }
        resolve();
      };
    });

    mountLayout();
    await flushPromises();

    // Exactly what useChat does on the first send of a session, while the mount-time catalog load
    // is still outstanding.
    let provisioned: string | null = null;
    let refusal: unknown = null;
    const sending = sharedMocks.chatOptions!.provisionThreadId!()
      .then((id) => {
        provisioned = id;
      })
      .catch((e: unknown) => {
        refusal = e;
      });

    await flushPromises();
    // Still in flight: nothing provisioned, and nothing refused either.
    expect(sharedMocks.createNewConversation).not.toHaveBeenCalled();
    expect(refusal).toBeNull();

    landCatalog();
    await sending;

    expect(sharedMocks.createNewConversation).toHaveBeenCalledWith({
      workspaceId: 'ws-1',
      providerId: 'anthropic',
      modeId: 'default',
    });
    expect(refusal).toBeNull();
    expect(provisioned).toBe('thread-provisioned');
  });

  // The workspace catalog needs the same wait for a different reason. Its selection is seeded with
  // 'default' rather than null, so it never LOOKS missing — but the load reconciles that guess
  // against what the host actually has, dropping it when no such workspace exists or when the one
  // that does is incompatible with the gateway. Provisioning on the un-reconciled guess sends the
  // server a binding the load was about to reject.
  it('sends the workspace the catalog reconciled to, not the seeded guess', async () => {
    let landCatalog!: () => void;
    sharedMocks.workspaceCatalogLoad = new Promise<void>((resolve) => {
      landCatalog = () => {
        if (sharedMocks.workspaceSelectionRef) {
          sharedMocks.workspaceSelectionRef.value = 'ws-reconciled';
        }
        resolve();
      };
    });

    mountLayout();
    await flushPromises();

    let provisioned: string | null = null;
    const sending = sharedMocks.chatOptions!.provisionThreadId!().then((id) => {
      provisioned = id;
    });

    await flushPromises();
    expect(sharedMocks.createNewConversation).not.toHaveBeenCalled();

    landCatalog();
    await sending;

    expect(sharedMocks.createNewConversation).toHaveBeenCalledWith({
      workspaceId: 'ws-reconciled',
      providerId: 'anthropic',
      modeId: 'default',
    });
    expect(provisioned).toBe('thread-provisioned');
  });
});

// #445 item 9, the host half of #375's visibility criterion. Visibility is stored server-side and
// reaches the client on the conversation LISTING, so ChatLayout is what hands it to the share
// control — and what re-lists once that control has made the server flip it.
describe('ChatLayout share control visibility (#375)', () => {
  const mountWithShareModal = () =>
    mount(ChatLayout, {
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
          // Stubbed: this is about what ChatLayout hands the control and what it does with the
          // control's answer, not about the control's own roster reads.
          ShareConversationModal: true,
        },
      },
    });

  beforeEach(() => {
    // A ?threadId= left over from an earlier file would put the layout in its not-found view, which
    // has no header at all — and therefore no share button to find.
    window.history.pushState({}, '', '/');
    sharedMocks.chatLoading = false;
    sharedMocks.isSending = false;
    sharedMocks.modesLoading = false;
    sharedMocks.currentThreadId = 'thread-1';
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1', visibility: 'shared' })];
    sharedMocks.loadConversations.mockClear();
  });

  it('hands the share control the visibility the listing reported for the open conversation', async () => {
    const wrapper = mountWithShareModal();
    await flushPromises();

    await wrapper.get('[data-testid="share-button"]').trigger('click');
    await flushPromises();

    const modal = wrapper.findComponent({ name: 'ShareConversationModal' });
    expect(modal.exists()).toBe(true);
    expect(modal.props('visibility')).toBe('shared');
  });

  // #482. `canShare` reaches the client on the same listing as `visibility`, so ChatLayout hands it
  // over the same way. Without this the modal would be back to offering a mutation and finding out
  // by being refused — the flag would exist on the wire and reach nobody.
  it('hands the share control whether this viewer may share the open conversation', async () => {
    sharedMocks.conversations = [makeConversation({ threadId: 'thread-1', visibility: 'shared', canShare: false })];

    const wrapper = mountWithShareModal();
    await flushPromises();

    await wrapper.get('[data-testid="share-button"]').trigger('click');
    await flushPromises();

    const modal = wrapper.findComponent({ name: 'ShareConversationModal' });
    expect(modal.props('canShare')).toBe(false);
  });

  it('re-lists conversations when the share control reports a change', async () => {
    const wrapper = mountWithShareModal();
    await flushPromises();

    await wrapper.get('[data-testid="share-button"]').trigger('click');
    await flushPromises();
    // The mount-time list is not what is under test here.
    sharedMocks.loadConversations.mockClear();

    // Adding or revoking a grant is what flips visibility server-side; nothing else re-reads it.
    wrapper.findComponent({ name: 'ShareConversationModal' }).vm.$emit('changed');
    await flushPromises();

    expect(sharedMocks.loadConversations).toHaveBeenCalledTimes(1);
  });
});


/**
 * #459 / F-001. Every other test for this feature sits BELOW the parent gate: they mount
 * `WorkspaceSelector` directly, or drive `useWorkspaces` on its own. Neither can see that
 * `ChatLayout` was terminally disabling the selector on the very signal the split reinterprets, so
 * on a gateway-less host the dropdown could not be opened at all and none of the new behaviour —
 * relaxed rows, the `unverified` caveat, `isWorkspaceWithheld` — ever got asked a question.
 *
 * This test is therefore deliberately end-to-end through the real `WorkspaceSelector`: the assertion
 * is not "the flag is false", it is "a human can open this thing and click the row".
 */
describe('ChatLayout keeps the workspace picker usable on a gateway-less host (#459 F-001)', () => {
  const unverifiedRow = (id: string, name: string): Workspace => ({
    id,
    name,
    directoryRelPath: id,
    marketplaces: [],
    isSystemDefined: id === 'default',
    createdAt: 0,
    updatedAt: 0,
    compatibility: 'unavailable',
    unsupportedMarketplaces: [],
  });

  beforeEach(() => {
    sharedMocks.selectWorkspace.mockReset();
    sharedMocks.conversations = [];
    sharedMocks.currentThreadId = null;
    sharedMocks.workspaces = [unverifiedRow('default', 'Default'), unverifiedRow('repo', 'Repo')];
    // What the backend actually sends from a host with no sandbox gateway: the workspace list is
    // served fine, and the marketplace catalog behind it is not readable.
    sharedMocks.workspaceGateway = {
      canonicalBaseUrl: 'http://gateway:3000',
      appId: 'sample',
      available: false,
      error: 'gateway offline',
    };

    // WorkspaceSelector fetches /api/marketplaces on mount; 503 is what a gateway-less host answers.
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        if (url.startsWith('/api/marketplaces')) {
          return { ok: false, status: 503, statusText: 'Service Unavailable', json: async () => ({}) };
        }
        return { ok: true, status: 200, statusText: 'OK', json: async () => ({}) };
      })
    );
  });

  afterEach(() => {
    sharedMocks.workspaces = [];
    sharedMocks.workspaceGateway = null;
    vi.unstubAllGlobals();
  });

  const mountWithRealSelector = () =>
    mount(ChatLayout, {
      attachTo: document.body,
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
          ModeSelector: true,
          ProviderSelector: true,
          // WorkspaceSelector deliberately NOT stubbed — reaching its rows is the whole point.
        },
      },
    });

  it('opens the dropdown and selects an unverified row while the catalog is unavailable', async () => {
    const wrapper = mountWithRealSelector();
    await flushPromises();

    // The gate itself: the trigger button must be live, not merely present.
    const button = wrapper.get<HTMLButtonElement>('[data-testid="workspace-selector-button"]');
    expect(button.element.disabled).toBe(false);

    await button.trigger('click');
    await flushPromises();

    const row = wrapper.get<HTMLButtonElement>('[data-testid="workspace-option-repo"]');
    expect(row.element.disabled).toBe(false);
    // The caveat still renders, so "selectable" has not quietly become "vouched for".
    expect(wrapper.find('[data-testid="workspace-unverified-repo"]').exists()).toBe(true);

    await row.trigger('click');
    await flushPromises();

    // Reached the composable through ChatLayout's own handler, which shares the same gate.
    expect(sharedMocks.selectWorkspace).toHaveBeenCalledWith('repo');
  });

  /**
   * The gate that WAS there is not simply gone — the streaming/sending conditions still close the
   * picker. Without this, deleting the whole computed would also pass the test above.
   */
  it('still disables the picker while a run is streaming', async () => {
    sharedMocks.isSending = true;
    try {
      const wrapper = mountWithRealSelector();
      await flushPromises();

      expect(
        wrapper.get<HTMLButtonElement>('[data-testid="workspace-selector-button"]').element.disabled
      ).toBe(true);
    } finally {
      sharedMocks.isSending = false;
    }
  });
});

/**
 * 596/F-001 + #594 D6: the artifact preview modal is OWNED by ChatLayout (the panel only emits a
 * path), and this level was the one part of the feature with no test — deleting the
 * `watch(subAgentParentThreadId, ...)` that closes it on a conversation switch left the whole suite
 * green. These tests mount ChatLayout with the REAL useTodoBoard and drive the board through the
 * same `conversation_todo` frame ref the app uses, so the modal's open/close state is exercised
 * where it lives.
 */
describe('ChatLayout artifact preview modal lifecycle (596/F-001, #594 D6)', () => {
  /** Matches the `conversation_todo` push frame `useChat` hands to `useTodoBoard` (#583, PR 2). */
  const boardFrame = (threadId: string) => ({
    $type: 'conversation_todo',
    threadId,
    tasks: [
      {
        id: '1',
        status: 'InProgress',
        title: 'Write the spec',
        notes: [],
        artifacts: ['docs/spec.md'],
        subTasks: [],
      },
    ],
  });

  // A real chip click bubbles `openArtifact` up from TodoBoardPanel; the stub keeps that emit and
  // nothing else, so the test drives ChatLayout's handler through the same event the panel uses.
  const TodoBoardPanelStub = defineComponent({
    emits: ['open-artifact'],
    template:
      '<button data-test="board-chip" @click="$emit(\'open-artifact\', \'docs/spec.md\')">chip</button>',
  });

  // A stub with a body: `ArtifactPreviewModal: true` would render none of the real component's
  // template, so there would be no `data-test-id="artifact-preview-modal"` element to assert on.
  // This one carries the marker and a close control wired to the same `close` emit the real modal
  // raises (through BaseModal), and skips the preview fetch the real one fires on mount.
  const ArtifactPreviewModalStub = defineComponent({
    emits: ['close'],
    template:
      '<div data-test-id="artifact-preview-modal">'
      + '<button data-test="modal-close" @click="$emit(\'close\')">close</button>'
      + '</div>',
  });

  const mountLayout = () =>
    mount(ChatLayout, {
      global: {
        stubs: {
          ConversationSidebar: true,
          MessageList: true,
          PendingMessageQueue: true,
          ChatInput: true,
          TodoBoardPanel: TodoBoardPanelStub,
          // The SUBJECT here is ChatLayout's mount gate, not the modal's internals
          // (ArtifactPreviewModal.test.ts owns those).
          ArtifactPreviewModal: ArtifactPreviewModalStub,
        },
      },
    });

  /** Mounts, conjures a board via a push frame, clicks the chip: the modal is open on return. */
  const mountWithOpenModal = async () => {
    const wrapper = mountLayout();
    await flushPromises();
    sharedMocks.conversationTodoRef!.value = boardFrame('thread-1');
    await flushPromises();
    await wrapper.get('[data-test="board-chip"]').trigger('click');
    expect(wrapper.find('[data-test-id="artifact-preview-modal"]').exists()).toBe(true);
    return wrapper;
  };

  beforeEach(() => {
    sharedMocks.chatLoading = false;
    sharedMocks.isSending = false;
    sharedMocks.modesLoading = false;
    sharedMocks.currentThreadId = 'thread-1';
    // BOTH threads are started (present in the sidebar): after the switch below,
    // subAgentParentThreadId is non-null again, so the modal's v-if stays satisfied on its own and
    // ONLY the close watch can unmount it — the mutation "delete the watch" goes red here.
    sharedMocks.conversations = [
      makeConversation({ threadId: 'thread-1' }),
      makeConversation({ threadId: 'thread-2' }),
    ];
    // The REAL useTodoBoard hydrates over REST when the thread changes; answer 404 ("no board") so
    // the switch path exercises the same tolerant handling the app ships.
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({ ok: false, status: 404, statusText: 'Not Found', json: async () => ({}) }))
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('closes the modal when the conversation switches (the F-001 watch)', async () => {
    const wrapper = await mountWithOpenModal();

    sharedMocks.chatThreadIdRef!.value = 'thread-2';
    await flushPromises();

    // Without the watch the modal would still be here: thread-2 satisfies the v-if's thread gate.
    expect(wrapper.find('[data-test-id="artifact-preview-modal"]').exists()).toBe(false);
  });

  it('leaves the modal open while the conversation does NOT change', async () => {
    // Non-vacuity guard for the case above: the closing is caused by the switch, not by any
    // re-render that happens to follow the chip click.
    const wrapper = await mountWithOpenModal();

    sharedMocks.conversationTodoRef!.value = boardFrame('thread-1');
    await flushPromises();

    expect(wrapper.find('[data-test-id="artifact-preview-modal"]').exists()).toBe(true);
  });

  it('lifts the sidebar above the backdrop only while the modal is open (#594 D6)', async () => {
    // The class is the testable seam of the D6 fix: jsdom computes no stacking contexts, so the
    // z-index CSS itself is pinned by the source-text guard below; THIS pins that the class comes
    // and goes with the modal. Fallthrough class lands on the stub's root element.
    const wrapper = await mountWithOpenModal();
    expect(wrapper.find('.sidebar-over-artifact-preview').exists()).toBe(true);

    await wrapper.get('[data-test="modal-close"]').trigger('click');
    expect(wrapper.find('.sidebar-over-artifact-preview').exists()).toBe(false);
  });
});

/**
 * Source-text guard for the D6 stacking CSS, following the `AppShellLayout.test.ts` precedent:
 * jsdom lays nothing out, so "the sidebar is clickable above the backdrop" reduces to these
 * declarations existing and agreeing with BaseModal's backdrop.
 */
describe('ChatLayout sidebar-over-backdrop stacking (#594 D6, source text)', () => {
  const readSrc = (rel: string) =>
    fs.readFileSync(path.resolve(__dirname, rel), 'utf-8') as string;

  it('raises .sidebar-over-artifact-preview above BaseModal\'s z-index 1000 backdrop', () => {
    const chatLayout = readSrc('../../components/ChatLayout.vue');
    expect(chatLayout).toMatch(/\.sidebar-over-artifact-preview\s*\{[^}]*position:\s*relative/);
    expect(chatLayout).toMatch(/\.sidebar-over-artifact-preview\s*\{[^}]*z-index:\s*1001/);
    // The relationship this depends on: if the backdrop's layer ever moves, this fails and points
    // straight at the pair that must move together.
    const baseModal = readSrc('../../components/BaseModal.vue');
    expect(baseModal).toMatch(/\.modal-backdrop\s*\{[^}]*z-index:\s*1000/);
  });
});
