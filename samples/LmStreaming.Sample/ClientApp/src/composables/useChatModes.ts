import { ref, computed } from 'vue';
import type { ChatMode, ChatModeCreateUpdate, ToolDefinition } from '@/types/chatMode';
import {
  listChatModes,
  createChatMode as apiCreateMode,
  updateChatMode as apiUpdateMode,
  deleteChatMode as apiDeleteMode,
  copyChatMode as apiCopyMode,
  listTools as apiListTools,
  switchConversationMode as apiSwitchMode,
} from '@/api/chatModesApi';

const DEFAULT_MODE_ID = 'default';

/**
 * Composable for managing chat modes.
 */
export function useChatModes() {
  const modes = ref<ChatMode[]>([]);
  const currentModeId = ref<string>(DEFAULT_MODE_ID);
  const availableTools = ref<ToolDefinition[]>([]);
  const isLoading = ref(false);
  const isToolsLoading = ref(false);
  const error = ref<string | null>(null);

  /**
   * The currently selected mode.
   */
  const currentMode = computed(() =>
    modes.value.find((m) => m.id === currentModeId.value)
  );

  /**
   * System-defined modes (read-only).
   */
  const systemModes = computed(() =>
    modes.value.filter((m) => m.isSystemDefined)
  );

  /**
   * User-defined modes (editable).
   */
  const userModes = computed(() =>
    modes.value.filter((m) => !m.isSystemDefined)
  );

  /**
   * Loads all chat modes from the backend.
   */
  async function loadModes(): Promise<void> {
    isLoading.value = true;
    error.value = null;
    try {
      modes.value = await listChatModes();
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to load chat modes';
      console.error('Failed to load chat modes:', e);
    } finally {
      isLoading.value = false;
    }
  }

  /**
   * Loads all available tools from the backend.
   */
  async function loadTools(): Promise<void> {
    isToolsLoading.value = true;
    try {
      availableTools.value = await apiListTools();
    } catch (e) {
      console.error('Failed to load tools:', e);
    } finally {
      isToolsLoading.value = false;
    }
  }

  /**
   * Selects a mode for new conversations.
   */
  function selectMode(modeId: string): void {
    currentModeId.value = modeId;
  }

  /**
   * Switches the mode for an existing conversation.
   */
  async function switchMode(threadId: string, modeId: string): Promise<void> {
    try {
      await apiSwitchMode(threadId, modeId);
      currentModeId.value = modeId;
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to switch mode';
      console.error('Failed to switch mode:', e);
      throw e;
    }
  }

  /**
   * Creates a new user-defined mode.
   */
  async function createMode(data: ChatModeCreateUpdate): Promise<ChatMode> {
    try {
      const mode = await apiCreateMode(data);
      modes.value.push(mode);
      return mode;
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to create mode';
      console.error('Failed to create mode:', e);
      throw e;
    }
  }

  /**
   * Updates an existing user-defined mode.
   */
  async function updateMode(modeId: string, data: ChatModeCreateUpdate): Promise<ChatMode> {
    try {
      const updatedMode = await apiUpdateMode(modeId, data);
      const index = modes.value.findIndex((m) => m.id === modeId);
      if (index >= 0) {
        modes.value[index] = updatedMode;
      }
      return updatedMode;
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to update mode';
      console.error('Failed to update mode:', e);
      throw e;
    }
  }

  /**
   * Deletes a user-defined mode.
   */
  async function deleteMode(modeId: string): Promise<void> {
    try {
      await apiDeleteMode(modeId);
      modes.value = modes.value.filter((m) => m.id !== modeId);
      // If the deleted mode was selected, switch to default
      if (currentModeId.value === modeId) {
        currentModeId.value = DEFAULT_MODE_ID;
      }
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to delete mode';
      console.error('Failed to delete mode:', e);
      throw e;
    }
  }

  /**
   * Copies an existing mode to create a new user-defined mode.
   */
  async function copyMode(modeId: string, newName: string): Promise<ChatMode> {
    try {
      const mode = await apiCopyMode(modeId, newName);
      modes.value.push(mode);
      return mode;
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to copy mode';
      console.error('Failed to copy mode:', e);
      throw e;
    }
  }

  /**
   * Gets a mode by ID.
   */
  function getModeById(modeId: string): ChatMode | undefined {
    return modes.value.find((m) => m.id === modeId);
  }

  return {
    // State
    modes,
    currentModeId,
    availableTools,
    isLoading,
    isToolsLoading,
    error,

    // Computed
    currentMode,
    systemModes,
    userModes,

    // Actions
    loadModes,
    loadTools,
    selectMode,
    switchMode,
    createMode,
    updateMode,
    deleteMode,
    copyMode,
    getModeById,
  };
}
