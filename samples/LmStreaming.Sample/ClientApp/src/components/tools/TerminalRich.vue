<script setup lang="ts">
import { computed } from 'vue';
import type { ToolPillView } from '@/utils/toolTypes';
import type { ToolCall } from '@/types';
import { parseTerminal } from '@/utils/toolParsers';

const props = defineProps<{ view: ToolPillView; toolCall: ToolCall }>();

/** Parse a string to a plain (non-array) object, or null when it is not JSON / not an object. */
function tryObj(s: string): Record<string, unknown> | null {
  try {
    const v = JSON.parse(s);
    return v && typeof v === 'object' && !Array.isArray(v) ? v : null;
  } catch {
    return null;
  }
}

/**
 * Normalized terminal model. A structured TaskOutput result (`view.resultText` is that object's
 * JSON string) is detected by `tryObj`; a plain Bash string falls back to the trailing
 * `[Exit code: N]` / derived `view.exitCode`. `parseTerminal` never throws.
 */
const model = computed(() => {
  const structured = tryObj(props.view.resultText);
  return parseTerminal(props.view.resultText, { structured, exitCode: props.view.exitCode });
});

/** Optional `$ command` header pulled from the already-parsed args. */
const command = computed(() => {
  const c = props.view.parsedArgs?.command;
  return typeof c === 'string' ? c : '';
});
</script>

<template>
  <div class="term tool-rich">
    <div v-if="command" class="term-cmd">$ {{ command }}</div>
    <pre class="term-out">{{ model.stdout }}</pre>
    <pre v-if="model.stderr" class="term-err">{{ model.stderr }}</pre>
    <div
      v-if="model.exitCode !== null"
      class="term-exit"
      :class="{ failed: model.failed }"
    >
      <template v-if="model.failed">✗ exited with code {{ model.exitCode }}</template>
      <template v-else>✓ exit {{ model.exitCode }}</template>
    </div>
  </div>
</template>

<style scoped>
.term {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.8125rem;
  line-height: 1.5;
}

.term-cmd {
  color: #93c5fd;
  margin-bottom: 0.25rem;
  white-space: pre-wrap;
  word-break: break-word;
}

.term-out,
.term-err {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
}

.term-err {
  color: #f87171;
  margin-top: 0.25rem;
}

.term-exit {
  margin-top: 0.375rem;
  color: #4ade80;
  font-weight: 600;
}

.term-exit.failed {
  color: #f87171;
}
</style>
