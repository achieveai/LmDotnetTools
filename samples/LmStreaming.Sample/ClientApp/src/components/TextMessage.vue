<script setup lang="ts">
import { computed, inject } from 'vue';
import type { TextMessage } from '@/types';
import { parseMarkdown } from '@/utils/markdown';
import {
  WORKSPACE_FILE_LINKS,
  WORKSPACE_LINK_CLASS,
  parseWorkspaceLinkHref,
  type WorkspaceFileLinksContext,
} from '@/utils/workspaceLinks';

const props = withDefaults(
  defineProps<{
    message: TextMessage;
    isStreaming?: boolean;
    /**
     * `false` while this message is still being streamed into. Syntax highlighting is skipped
     * for the growing text (it re-parses on every delta) and applied once the run completes.
     * Distinct from `isStreaming`, which only controls the blinking cursor.
     */
    isComplete?: boolean;
    /**
     * Render file links as workspace links that open the preview modal. MessageList turns it on for
     * assistant bubbles only; it has an effect only under a ChatLayout that has a conversation id.
     */
    workspaceLinks?: boolean;
  }>(),
  { isComplete: true, workspaceLinks: false }
);

const fileLinks = inject<WorkspaceFileLinksContext | null>(WORKSPACE_FILE_LINKS, null);

const workspaceLinkOptions = computed(() => {
  const threadId = fileLinks?.threadId.value;
  return props.workspaceLinks && threadId ? { threadId } : undefined;
});

const parsedText = computed(() =>
  parseMarkdown(props.message.text, {
    highlight: props.isComplete !== false,
    workspaceLinks: workspaceLinkOptions.value,
  })
);

/** One delegated listener for every link in the v-html body, including clicks on nested elements. */
function onContentClick(event: MouseEvent): void {
  if (!fileLinks || !(event.target instanceof Element)) return;
  const anchor = event.target.closest(`a.${WORKSPACE_LINK_CLASS}`);
  const link = anchor ? parseWorkspaceLinkHref(anchor.getAttribute('href') ?? '') : null;
  if (!link) return;
  event.preventDefault();
  fileLinks.open(link);
}
</script>

<template>
  <div class="text-message" :class="{ thinking: message.isThinking }">
    <div class="markdown-content" v-html="parsedText" @click="onContentClick"></div>
    <span v-if="isStreaming" class="cursor">|</span>
  </div>
</template>

<style scoped>
.text-message {
  line-height: 1.5;
  position: relative;
}

/* Markdown element styling lives in assets/markdown.css (shared with PendingMessage). */

.text-message.thinking {
  font-style: italic;
  color: #666;
}

.cursor {
  display: inline-block;
  animation: blink 1s infinite;
  color: #007bff;
  margin-left: 2px;
  vertical-align: text-bottom;
}

@keyframes blink {
  0%, 50% {
    opacity: 1;
  }
  51%, 100% {
    opacity: 0;
  }
}
</style>
