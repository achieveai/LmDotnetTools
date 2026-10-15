<script setup lang="ts">
import { computed, inject, shallowRef, watch } from 'vue';
import DiagramViewer from './DiagramViewer.vue';
import SmilesViewer from './SmilesViewer.vue';
import type { TextMessage } from '@/types';
import { parseMarkdown } from '@/utils/markdown';
import { parseUserAnswerMessage } from '@/utils/pendingQuestions';
import {
  WORKSPACE_FILE_LINKS,
  WORKSPACE_LINK_CLASS,
  parseWorkspaceLinkHref,
  type WorkspaceFileLinksContext,
} from '@/utils/workspaceLinks';
import { MINI_WEB_APP_LINKS, parseMiniWebAppHref, type MiniWebAppLinksContext } from '@/utils/miniWebAppLinks';

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
const miniWebAppLinks = inject<MiniWebAppLinksContext | null>(MINI_WEB_APP_LINKS, null);

const workspaceLinkOptions = computed(() => {
  const threadId = fileLinks?.threadId.value;
  return props.workspaceLinks && threadId
    ? { threadId, baseDir: props.workspaceLinkBaseDir }
    : undefined;
});

/**
 * A late answer the server redirected into the conversation (bug #5): the question was settled
 * early so the run could continue, and the human's answer arrives as this user-role message
 * carrying both the request and the answer. Rendered as a compact card, never as raw markup.
 */
const userAnswer = computed(() => parseUserAnswerMessage(props.message.text));

const answerLines = computed<string[]>(() => {
  if (!userAnswer.value) return [];
  try {
    const parsed = JSON.parse(userAnswer.value.answer) as { answers?: unknown };
    if (!Array.isArray(parsed.answers)) return [];
    return parsed.answers.map((entry) => {
      const answer = (entry ?? {}) as Record<string, unknown>;
      const parts: string[] = [];
      if (answer.skipped === true) parts.push('Skipped');
      if (Array.isArray(answer.selectedValues) && answer.selectedValues.length) {
        parts.push(answer.selectedValues.map(String).join(', '));
      }
      if (typeof answer.otherText === 'string' && answer.otherText) parts.push(answer.otherText);
      if (typeof answer.comment === 'string' && answer.comment) parts.push(`— ${answer.comment}`);
      const label = typeof answer.questionId === 'string' && answer.questionId ? `${answer.questionId}: ` : '';
      return `${label}${parts.join(' ') || '(no answer)'}`;
    });
  } catch {
    return [];
  }
});

const parsedText = computed(() =>
  userAnswer.value
    ? ''
    : parseMarkdown(props.message.text, {
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
/*
 * ```smiles fences become chemical-structure figures. They are hoisted exactly like diagrams -- and
 * for the same reason -- rather than rendered inside `parseMarkdown`: the drawing is generated SVG,
 * so it belongs to `sanitizeDiagramSvg`'s policy and to a component, not to the Markdown allowlist.
 * (Math is different: KaTeX output is small and bounded, so `parseMarkdown` renders it in place
 * under its own sanitizer -- see utils/mathMarkdown.ts -- and needs nothing here.)
 */
const structures = shallowRef<Array<{ target: HTMLElement; source: string }>>([]);
const embedsReady = computed(() => props.isComplete !== false);
// Replacing the HTML surface also retires its embed hosts. Vue owns the viewers through
// Teleport; generated SVG never passes through or broadens the Markdown HTML allowlist.
const contentKey = computed(() => `${embedsReady.value}:${parsedText.value}`);
watch(markdownElement, (element) => {
  diagrams.value = [];
  structures.value = [];
  if (!element || !embedsReady.value) return;
  const nextDiagrams: typeof diagrams.value = [];
  const nextStructures: typeof structures.value = [];
  for (const code of element.querySelectorAll('pre > code')) {
    const fence = [...code.classList].find((name) => name.startsWith('language-'))?.slice(9).toLowerCase();
    if (!fence || !['mermaid', 'plantuml', 'puml', 'uml', 'smiles'].includes(fence)) continue;
    const target = document.createElement('div');
    target.className = fence === 'smiles' ? 'smiles-host' : 'diagram-host';
    const source = code.textContent ?? '';
    code.parentElement?.replaceWith(target);
    if (fence === 'smiles') nextStructures.push({ target, source });
    else nextDiagrams.push({ target, source, language: fence === 'mermaid' ? 'mermaid' : 'plantuml' });
  }
  diagrams.value = nextDiagrams;
  structures.value = nextStructures;
}, { flush: 'post' });

/** One delegated listener for every link in the v-html body, including clicks on nested elements. */
function onContentClick(event: MouseEvent): void {
  if (!props.workspaceLinks || !(event.target instanceof Element)) return;
  const appAnchor = event.target.closest('a');
  const appLink = appAnchor ? parseMiniWebAppHref(appAnchor.getAttribute('href') ?? '') : null;
  const threadId = miniWebAppLinks?.threadId.value;
  if (appLink && threadId) {
    event.preventDefault();
    miniWebAppLinks?.open({ threadId, ...appLink });
    return;
  }
  if (!fileLinks) return;
  const anchor = event.target.closest(`a.${WORKSPACE_LINK_CLASS}`);
  const link = anchor ? parseWorkspaceLinkHref(anchor.getAttribute('href') ?? '') : null;
  if (!link) return;
  event.preventDefault();
  fileLinks.open(link);
}
</script>

<template>
  <div v-if="userAnswer" class="text-message user-answer" data-testid="user-answer-card">
    <div class="user-answer__title">Answer delivered</div>
    <div class="user-answer__section">
      <div class="user-answer__label">Question</div>
      <pre class="user-answer__body" data-testid="user-answer-request">{{ userAnswer.request }}</pre>
    </div>
    <div class="user-answer__section">
      <div class="user-answer__label">Answer</div>
      <ul v-if="answerLines.length" class="user-answer__answers" data-testid="user-answer-answers">
        <li v-for="(line, index) in answerLines" :key="index">{{ line }}</li>
      </ul>
      <pre v-else class="user-answer__body" data-testid="user-answer-answers">{{ userAnswer.answer }}</pre>
    </div>
  </div>
  <div v-else class="text-message" :class="{ thinking: message.isThinking }">
    <div :key="contentKey" ref="markdownElement" class="markdown-content" v-html="parsedText" @click="onContentClick"></div>
    <Teleport v-for="(diagram, index) in diagrams" :key="contentKey + index" :to="diagram.target">
      <DiagramViewer :source="diagram.source" :language="diagram.language" />
    </Teleport>
    <Teleport v-for="(structure, index) in structures" :key="`smiles${contentKey}${index}`" :to="structure.target">
      <SmilesViewer :source="structure.source" />
    </Teleport>
    <span v-if="isStreaming" class="cursor">|</span>
  </div>
</template>

<style scoped>
.text-message {
  line-height: 1.5;
  position: relative;
}

.user-answer {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 10px;
  border: 1px solid #d9dee5;
  border-radius: 8px;
  background: #f7f8fa;
  color: #4f5966;
  font-size: 13px;
}

.user-answer__title {
  font-weight: 600;
}

.user-answer__label {
  color: #697482;
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.user-answer__body {
  margin: 2px 0 0;
  white-space: pre-wrap;
  word-break: break-word;
  font: inherit;
}

.user-answer__answers {
  margin: 2px 0 0;
  padding-left: 18px;
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
