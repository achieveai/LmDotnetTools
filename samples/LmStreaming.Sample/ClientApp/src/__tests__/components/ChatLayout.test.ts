import { describe, expect, it, beforeEach, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import ChatLayout from '@/components/ChatLayout.vue';

const sharedMocks = vi.hoisted(() => ({
  chatLoading: false,
  isSending: false,
  modesLoading: false,
  currentThreadId: 'thread-1' as string | null,
  switchMode: vi.fn(),
  disconnectWebSocket: vi.fn(),
}));

vi.mock('@/composables/useConversations', async () => {
  const { ref } = await import('vue');
  return {
    useConversations: () => ({
      conversations: ref([]),
      currentThreadId: ref(sharedMocks.currentThreadId),
      isLoading: ref(false),
      loadConversations: vi.fn(async () => {}),
      createNewConversation: vi.fn(() => 'thread-new'),
      selectConversation: vi.fn(),
      removeConversation: vi.fn(async () => {}),
      addOrUpdateConversation: vi.fn(),
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
      pendingMessages: ref([]),
      sendMessage: vi.fn(async () => {}),
      clearMessages: vi.fn(),
      disconnectWebSocket: sharedMocks.disconnectWebSocket,
      setThreadId: vi.fn(),
      loadMessagesFromBackend: vi.fn(async () => {}),
      getResultForToolCall: vi.fn(() => null),
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
