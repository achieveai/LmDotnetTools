import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import ChatLayout from '@/components/ChatLayout.vue';

interface ConversationSummary {
  threadId: string;
  title?: string;
  preview?: string;
  lastUpdated?: number;
  provider?: string | null;
  workspace?: string | null;
  mode?: string | null;
}

const sharedMocks = vi.hoisted(() => ({
  chatLoading: false,
  isSending: false,
  modesLoading: false,
  currentThreadId: 'thread-1' as string | null,
  // Sidebar conversations. A thread present here is "started" (first message
  // sent); an empty list with a non-null currentThreadId is a brand-new,
  // messageless thread (handleNewChat assigns the id before the first send).
  conversations: [] as ConversationSummary[],
  selectMode: vi.fn(),
  switchMode: vi.fn(),
  disconnectWebSocket: vi.fn(),
  selectProvider: vi.fn(),
  switchProvider: vi.fn(async () => {}),
  selectWorkspace: vi.fn(),
  addOrUpdateConversation: vi.fn(),
  resumeStreamIfActive: vi.fn(async () => {}),
  markStreamIdle: vi.fn(),
  markStreamLoading: vi.fn(),
}));

vi.mock('@/composables/useConversations', async () => {
  const { ref } = await import('vue');
  return {
    useConversations: () => ({
      conversations: ref(sharedMocks.conversations),
      currentThreadId: ref(sharedMocks.currentThreadId),
      isLoading: ref(false),
      loadConversations: vi.fn(async () => {}),
      createNewConversation: vi.fn(() => 'thread-new'),
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
    useChat: () => ({
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
      pendingMessages: ref([]),
      pendingAuthRequests: computed(() => []),
      dismissAuthRequest: vi.fn(),
      sendMessage: vi.fn(async () => {}),
      clearMessages: vi.fn(),
      cancelStream: vi.fn(async () => {}),
      disconnectWebSocket: sharedMocks.disconnectWebSocket,
      setThreadId: vi.fn(),
      loadMessagesFromBackend: vi.fn(async () => {}),
      resumeStreamIfActive: sharedMocks.resumeStreamIfActive,
      markStreamIdle: sharedMocks.markStreamIdle,
      markStreamLoading: sharedMocks.markStreamLoading,
      getResultForToolCall: vi.fn(() => null),
    }),
  };
});

vi.mock('@/composables/useProviders', async () => {
  const { ref } = await import('vue');
  return {
    useProviders: () => ({
      providers: ref([]),
      selectedProviderId: ref<string | null>(null),
      isLoading: ref(false),
      loadProviders: vi.fn(async () => {}),
      selectProvider: sharedMocks.selectProvider,
      switchProvider: sharedMocks.switchProvider,
    }),
  };
});

vi.mock('@/composables/useWorkspaces', async () => {
  const { ref } = await import('vue');
  return {
    useWorkspaces: () => ({
      workspaces: ref([]),
      selectedWorkspaceId: ref<string | null>('default'),
      isLoading: ref(false),
      loadWorkspaces: vi.fn(async () => {}),
      selectWorkspace: sharedMocks.selectWorkspace,
      createWorkspace: vi.fn(async () => {}),
      updateWorkspace: vi.fn(async () => {}),
    }),
  };
});

vi.mock('@/api/conversationsApi', () => ({
  updateConversationMetadata: vi.fn(async () => {}),
}));

describe('ChatLayout mode switching', () => {
  beforeEach(() => {
    sharedMocks.chatLoading = false;
    sharedMocks.isSending = false;
    sharedMocks.modesLoading = false;
    sharedMocks.currentThreadId = 'thread-1';
    // Default to a "started" thread (present in the sidebar) for the existing
    // mode-switch tests. The regression tests below override this per-case.
    sharedMocks.conversations = [{ threadId: 'thread-1' }];
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
    sharedMocks.conversations = [{ threadId: 'thread-1' }];

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
    sharedMocks.conversations = [{ threadId: 'thread-1', provider: 'test' } as ConversationSummary];

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
    sharedMocks.conversations = [{ threadId: 'thread-1', provider: 'test' } as ConversationSummary];
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
      { threadId: 'thread-1' },
      { threadId: 'thread-2', provider: 'openai', workspace: 'ws-1', mode: 'math-helper' },
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
    sharedMocks.conversations = [{ threadId: 'thread-1' }, { threadId: 'thread-2' }];

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
// and a not-found state when the id isn't in the backend's conversation list (never provisioned,
// or deleted) — see ChatLayout.vue's getDeepLinkThreadIdFromPageQuery/notFoundThreadId.
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
  });

  afterEach(() => {
    setQuery('');
  });

  it('selects the deep-linked conversation when it exists, in preference to the most recent one', async () => {
    sharedMocks.currentThreadId = 'thread-1';
    sharedMocks.conversations = [{ threadId: 'thread-1' }, { threadId: 'thread-2' }];
    setQuery('threadId=thread-2');

    const wrapper = mountLayout();
    await flushPromises();

    expect(wrapper.find('[data-testid="conversation-not-found"]').exists()).toBe(false);
    expect(sharedMocks.resumeStreamIfActive).toHaveBeenCalledWith('thread-2');
  });

  it('shows a not-found state for a deep-linked thread absent from the conversation list', async () => {
    sharedMocks.currentThreadId = null;
    sharedMocks.conversations = [{ threadId: 'thread-1' }];
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
    sharedMocks.conversations = [{ threadId: 'thread-1' }, { threadId: 'thread-2' }];
    setQuery('');

    const wrapper = mountLayout();
    await flushPromises();

    expect(wrapper.find('[data-testid="conversation-not-found"]').exists()).toBe(false);
    expect(sharedMocks.resumeStreamIfActive).toHaveBeenCalledWith('thread-1');
  });
});
