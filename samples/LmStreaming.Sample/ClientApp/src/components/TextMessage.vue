<script setup lang="ts">
import { computed, inject, shallowRef, watch } from 'vue';
import DiagramViewer from './DiagramViewer.vue';
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
    /**
     * Workspace directory a PLAIN RELATIVE link in this text should be read against. The file preview passes
     * the previewed file's own directory, because a link written inside a document means "relative to that
     * document". A chat message is not a file, so it passes nothing and its links stay root-relative.
     */
    workspaceLinkBaseDir?: string;
  }>(),
  { isComplete: true, workspaceLinks: false }
);

const fileLinks = inject<WorkspaceFileLinksContext | null>(WORKSPACE_FILE_LINKS, null);

const workspaceLinkOptions = computed(() => {
  const threadId = fileLinks?.threadId.value;
  return props.workspaceLinks && threadId
    ? { threadId, baseDir: props.workspaceLinkBaseDir }
    : undefined;
});

const parsedText = computed(() =>
  parseMarkdown(props.message.text, {
    highlight: props.isComplete !== false,
    workspaceLinks: workspaceLinkOptions.value,
  })
);

const markdownElement = shallowRef<HTMLElement | null>(null);
const diagrams = shallowRef<Array<{
  target: HTMLElement;
  source: string;
  language: 'mermaid' | 'plantuml';
}>>([]);
const diagramsReady = computed(() => props.isComplete !== false);
// Replacing the HTML surface also retires its diagram hosts. Vue owns the viewers through
// Teleport; generated SVG never passes through or broadens the Markdown HTML allowlist.
const contentKey = computed(() => `${diagramsReady.value}:${parsedText.value}`);
watch(markdownElement, (element) => {
  diagrams.value = [];
  if (!element || !diagramsReady.value) return;
  const next: typeof diagrams.value = [];
  for (const code of element.querySelectorAll('pre > code')) {
    const fence = [...code.classList].find((name) => name.startsWith('language-'))?.slice(9).toLowerCase();
    if (!fence || !['mermaid', 'plantuml', 'puml', 'uml'].includes(fence)) continue;
    const target = document.createElement('div');
    target.className = 'diagram-host';
    const source = code.textContent ?? '';
    code.parentElement?.replaceWith(target);
    next.push({ target, source, language: fence === 'mermaid' ? 'mermaid' : 'plantuml' });
  }
  diagrams.value = next;
}, { flush: 'post' });

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
    <div :key="contentKey" ref="markdownElement" class="markdown-content" v-html="parsedText" @click="onContentClick"></div>
    <Teleport v-for="(diagram, index) in diagrams" :key="contentKey + index" :to="diagram.target">
      <DiagramViewer :source="diagram.source" :language="diagram.language" />
    </Teleport>
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
