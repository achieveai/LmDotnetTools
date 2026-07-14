<script setup lang="ts">
/**
 * Presentational rich renderer for Grep-family results (#199).
 *
 * Renders the file-grouped match list produced by {@link parseMatches}: a summary header, then a
 * per-file heading followed by numbered match/context rows. Matched lines carry the `hit` class
 * (the primary highlight, since the grep payload has no column spans).
 *
 * When the search pattern is a plain LITERAL (no regex metacharacters), occurrences of that literal
 * inside each line are additionally wrapped in `<mark>`. This is done WITHOUT v-html and WITHOUT
 * running the pattern through a regex engine — `text.split(pattern)` is interleaved into typed
 * segments, and every segment renders through `{{ }}` interpolation so text is auto-escaped.
 */
import { computed } from 'vue';
import type { ToolPillView } from '@/utils/toolTypes';
import type { ToolCall } from '@/types';
import { parseMatches } from '@/utils/toolParsers';

const props = defineProps<{ view: ToolPillView; toolCall: ToolCall }>();

const model = computed(() => parseMatches(props.view.resultText));

const pattern = String(props.view.parsedArgs?.pattern ?? '');
const isLiteral = pattern.length > 0 && !/[.*+?^${}()|[\]\\]/.test(pattern);

/**
 * Split a line into escaped segments. When the pattern is a literal, occurrences are marked;
 * otherwise the whole line is a single un-marked segment (line-level `hit` highlight only).
 */
function segments(text: string): { t: string; mark: boolean }[] {
  if (!isLiteral) {
    return [{ t: text, mark: false }];
  }
  const parts = text.split(pattern);
  const out: { t: string; mark: boolean }[] = [];
  parts.forEach((part, i) => {
    if (i > 0) {
      out.push({ t: pattern, mark: true });
    }
    out.push({ t: part, mark: false });
  });
  return out;
}
</script>

<template>
  <div class="matches tool-rich">
    <div class="matches-summary">{{ model.summary || model.totalMatches + ' matches' }}</div>
    <div v-for="(group, gi) in model.groups" :key="gi" class="match-group">
      <div class="match-file">{{ group.file }}</div>
      <div v-for="(line, li) in group.lines" :key="li" class="m" :class="{ hit: line.isMatch }">
        <span class="n">{{ line.lineNo }}</span
        ><span class="ln"><template v-for="(seg, si) in segments(line.text)" :key="si"><mark
              v-if="seg.mark"
              >{{ seg.t }}</mark
            ><span v-else>{{ seg.t }}</span></template></span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.matches {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.8125rem;
}

.matches-summary {
  color: #6b7280;
  margin-bottom: 0.25rem;
}

.match-file {
  font-weight: 600;
  color: #374151;
  margin: 0.375rem 0 0.125rem;
  word-break: break-all;
}

.m {
  display: flex;
  gap: 0.5rem;
  white-space: pre-wrap;
}

.m.hit {
  background: #fef9c3;
}

.n {
  color: #9ca3af;
  text-align: right;
  min-width: 3ch;
  user-select: none;
}

.ln {
  flex: 1;
  min-width: 0;
}

.ln mark {
  background: #fde047;
  color: inherit;
  border-radius: 2px;
}
</style>
