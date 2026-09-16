<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue';
import { copyTextToClipboard } from '@/utils/clipboard';
import { logger } from '@/utils';

/**
 * Copies one assistant bubble's text to the clipboard as the model wrote it: raw markdown, not the
 * rendered HTML and not the rewritten workspace links. Icon-only; shown on bubble hover/focus by
 * MessageList. The result is drawn as an icon swap (check / cross), so the button's footprint never
 * changes, and named for assistive tech by a visually hidden label plus the live region below.
 */
const log = logger.forComponent('CopyMessageButton');

const props = defineProps<{ text: string }>();

type CopyState = 'idle' | 'copied' | 'failed';
const state = ref<CopyState>('idle');
const RESET_AFTER_MS = 1500;
let resetTimer: ReturnType<typeof setTimeout> | undefined;

/** The accessible name. Visually hidden: the icon is the whole visible control. */
const LABELS: Record<CopyState, string> = {
  idle: 'Copy message',
  copied: 'Copied',
  failed: 'Copy failed',
};

/**
 * What the live region says. A name change on a button is not announced by itself, so the result is
 * announced separately; the region is always rendered so the change, not its insertion, is read.
 */
const ANNOUNCEMENTS: Record<CopyState, string> = {
  idle: '',
  copied: 'Message copied',
  failed: 'Copy failed',
};

// The live region is a sibling, not a child: a button's children are presentational. MessageList's class
// and styling still belong on the button itself.
defineOptions({ inheritAttrs: false });

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
    v-bind="$attrs"
    type="button"
    class="copy-message-button"
    :class="`copy-message-button--${state}`"
    :data-state="state"
    title="Copy message as markdown"
    data-testid="copy-message-button"
    @click="copy"
  >
    <svg v-if="state === 'idle'" viewBox="0 0 16 16" width="14" height="14" aria-hidden="true">
      <path
        fill="currentColor"
        d="M4 2a2 2 0 0 1 2-2h6a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V2Zm2-1a1 1 0 0 0-1 1v8a1 1 0 0 0 1 1h6a1 1 0 0 0 1-1V2a1 1 0 0 0-1-1H6ZM2 5a1 1 0 0 0-1 1v8a1 1 0 0 0 1 1h6a1 1 0 0 0 1-1v-1h1v1a2 2 0 0 1-2 2H2a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h1v1H2Z"
      />
    </svg>
    <svg v-else-if="state === 'copied'" viewBox="0 0 16 16" width="14" height="14" aria-hidden="true">
      <path
        fill="none"
        stroke="currentColor"
        stroke-width="1.8"
        stroke-linecap="round"
        stroke-linejoin="round"
        d="M3 8.5 6.5 12 13 4.5"
      />
    </svg>
    <svg v-else viewBox="0 0 16 16" width="14" height="14" aria-hidden="true">
      <path
        fill="none"
        stroke="currentColor"
        stroke-width="1.8"
        stroke-linecap="round"
        d="M4 4l8 8M12 4l-8 8"
      />
    </svg>
    <span class="copy-message-label">{{ LABELS[state] }}</span>
  </button>
  <span class="copy-message-status" role="status" aria-live="polite" data-testid="copy-message-status">{{
    ANNOUNCEMENTS[state]
  }}</span>
</template>

<style scoped>
/* Visually hidden, still read: the accessible name and the live region. */
.copy-message-label,
.copy-message-status {
  position: absolute;
  width: 1px;
  height: 1px;
  margin: -1px;
  padding: 0;
  overflow: hidden;
  clip-path: inset(50%);
  white-space: nowrap;
  border: 0;
}

.copy-message-button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  padding: 0;
  border: 1px solid #e0e0e0;
  border-radius: 6px;
  background: #ffffff;
  color: #666;
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
