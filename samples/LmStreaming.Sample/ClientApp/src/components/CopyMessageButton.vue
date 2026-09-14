<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue';
import { copyTextToClipboard } from '@/utils/clipboard';
import { logger } from '@/utils';

/**
 * Copies one assistant bubble's text to the clipboard as the model wrote it: raw markdown, not the
 * rendered HTML and not the rewritten workspace links. Shown on bubble hover/focus by MessageList.
 */
const log = logger.forComponent('CopyMessageButton');

const props = defineProps<{ text: string }>();

type CopyState = 'idle' | 'copied' | 'failed';
const state = ref<CopyState>('idle');
const RESET_AFTER_MS = 1500;
let resetTimer: ReturnType<typeof setTimeout> | undefined;

const LABELS: Record<CopyState, string> = {
  idle: 'Copy',
  copied: 'Copied',
  failed: 'Copy failed',
};

async function copy(): Promise<void> {
  try {
    await copyTextToClipboard(props.text);
    state.value = 'copied';
  } catch (e) {
    state.value = 'failed';
    log.debug('Copy message failed', { error: e });
  }
  clearTimeout(resetTimer);
  resetTimer = setTimeout(() => {
    state.value = 'idle';
  }, RESET_AFTER_MS);
}

onBeforeUnmount(() => clearTimeout(resetTimer));
</script>

<template>
  <button
    type="button"
    class="copy-message-button"
    :class="`copy-message-button--${state}`"
    aria-label="Copy message"
    title="Copy message as markdown"
    data-testid="copy-message-button"
    @click="copy"
  >
    <svg v-if="state === 'idle'" viewBox="0 0 16 16" width="12" height="12" aria-hidden="true">
      <path
        fill="currentColor"
        d="M4 2a2 2 0 0 1 2-2h6a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V2Zm2-1a1 1 0 0 0-1 1v8a1 1 0 0 0 1 1h6a1 1 0 0 0 1-1V2a1 1 0 0 0-1-1H6ZM2 5a1 1 0 0 0-1 1v8a1 1 0 0 0 1 1h6a1 1 0 0 0 1-1v-1h1v1a2 2 0 0 1-2 2H2a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h1v1H2Z"
      />
    </svg>
    <span>{{ LABELS[state] }}</span>
  </button>
</template>

<style scoped>
.copy-message-button {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  border: 1px solid #e0e0e0;
  border-radius: 6px;
  background: #ffffff;
  color: #555;
  font-size: 11px;
  line-height: 1.6;
  cursor: pointer;
}

.copy-message-button:hover {
  background: #f3f4f6;
  color: #111;
}

.copy-message-button:focus-visible {
  outline: 2px solid #007bff;
  outline-offset: 1px;
}

.copy-message-button--copied {
  color: #1a7f37;
  border-color: #1a7f37;
}

.copy-message-button--failed {
  color: #b42318;
  border-color: #b42318;
}
</style>
