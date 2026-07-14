<script setup lang="ts">
import { computed } from 'vue';
import type { ToolPillView } from '@/utils/toolTypes';
import type { ToolCall } from '@/types';
import { parseDiff } from '@/utils/toolParsers';

const props = defineProps<{ view: ToolPillView; toolCall: ToolCall }>();

/**
 * The diff is computed from the Edit ARGS (old_string → new_string), never from the result string
 * (the Edit result is only a "Successfully edited …" confirmation). parseDiff is null-safe.
 */
const model = computed(() => {
  const a = props.view.parsedArgs || {};
  return parseDiff(a.old_string as string, a.new_string as string, a.replace_all as boolean);
});

/** Optional file path header — only shown when the args carry a non-empty string `file_path`. */
const filePath = computed<string | null>(() => {
  const fp = props.view.parsedArgs?.file_path;
  return typeof fp === 'string' && fp.length > 0 ? fp : null;
});
</script>

<template>
  <div class="diff tool-rich">
    <div v-if="filePath" class="diff-path">{{ filePath }}</div>
    <div class="diff-stat">
      +{{ model.added }} −{{ model.removed }}<span v-if="model.replaceAll"> · replace all</span>
    </div>
    <div v-for="(row, i) in model.rows" :key="i" class="diff-row" :class="row.kind">
      <span class="sign">{{ row.kind === 'add' ? '+' : '−' }}</span
      ><span class="line">{{ row.text }}</span>
    </div>
  </div>
</template>

<style scoped>
.diff {
  font-family: ui-monospace, SFMono-Regular, 'SF Mono', Menlo, Consolas, monospace;
  font-size: 12px;
  line-height: 1.5;
  border-radius: 4px;
  overflow-x: auto;
}

.diff-path {
  padding: 2px 8px;
  color: #64748b;
  font-weight: 600;
  word-break: break-all;
}

.diff-stat {
  padding: 2px 8px;
  color: #475569;
  font-weight: 600;
}

.diff-row {
  display: flex;
  align-items: baseline;
  gap: 6px;
  padding: 0 8px;
  white-space: pre;
}

.diff-row .sign {
  flex: none;
  width: 1ch;
  text-align: center;
  font-weight: 700;
  user-select: none;
}

.diff-row .line {
  white-space: pre-wrap;
  word-break: break-word;
}

.diff-row.del {
  background: #fef2f2;
  color: #b91c1c;
}

.diff-row.add {
  background: #f0fdf4;
  color: #15803d;
}
</style>
