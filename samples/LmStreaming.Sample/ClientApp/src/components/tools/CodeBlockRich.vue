<script setup lang="ts">
/**
 * Presentational rich renderer for file `read` / `write` tool calls (#199).
 *
 * Shares the frozen rich-component prop contract (`{ view, toolCall }`) — NO inject, NO API calls.
 * All display data is derived purely from {@link parseCodeBlock}; content is rendered as ESCAPED
 * text ({{ }} interpolation / <pre>), never `v-html`.
 */
import { computed } from 'vue';
import type { ToolPillView } from '@/utils/toolTypes';
import type { ToolCall } from '@/types';
import { parseCodeBlock } from '@/utils/toolParsers';

const props = defineProps<{ view: ToolPillView; toolCall: ToolCall }>();

const model = computed(() => parseCodeBlock(props.view.parsedArgs, props.view.resultText));

/** `file_path` arg when present and a string, else null (small header). */
const filePath = computed<string | null>(() => {
  const fp = props.view.parsedArgs?.file_path;
  return typeof fp === 'string' ? fp : null;
});
</script>

<template>
  <div class="code tool-rich">
    <div v-if="filePath" class="code-path">{{ filePath }}</div>

    <template v-if="model.mode === 'numbered'">
      <div v-for="(line, i) in model.lines" :key="i" class="code-line">
        <span class="ln">{{ line.lineNo }}</span>
        <span class="src">{{ line.text }}</span>
      </div>
    </template>

    <pre v-else class="code-plain">{{ model.content }}</pre>
  </div>
</template>

<style scoped>
.code {
  min-width: 0;
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
  font-size: 12px;
  line-height: 1.5;
}

.code-path {
  padding: 2px 6px;
  margin-bottom: 4px;
  color: #57606a;
  font-size: 11px;
  word-break: break-all;
}

.code-line {
  display: flex;
  gap: 8px;
  white-space: pre;
}

.code-line .ln {
  flex: 0 0 auto;
  min-width: 2.5em;
  text-align: right;
  color: #8c959f;
  user-select: none;
}

.code-line .src {
  flex: 1 1 auto;
  white-space: pre-wrap;
  word-break: break-word;
}

.code-plain {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
