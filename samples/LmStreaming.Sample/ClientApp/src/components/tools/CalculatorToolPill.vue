<script setup lang="ts">
import { computed } from 'vue';
import EventPill from '../EventPill.vue';
import type { ToolCall, ToolCallResultMessage } from '@/types';

const props = defineProps<{
  toolCall: ToolCall;
  result?: ToolCallResultMessage | null;
}>();

const CALCULATOR_ICON = '\u{1F5A9}'; // Calculator emoji (desktop computer with calculator)

const isLoading = computed(() => !props.result);

const expression = computed(() => {
  if (!props.toolCall.function_args) {
    return '';
  }

  try {
    const args = JSON.parse(props.toolCall.function_args);
    // Common arg names for calculator tools
    return args.expression || args.equation || args.input || args.a || '';
  } catch {
    return '';
  }
});

const label = computed(() => {
  if (props.result) {
    // Show "expression = result" format like "2+2 = 4"
    if (expression.value) {
      return `${expression.value} = ${props.result.result}`;
    }
    return props.result.result;
  }

  if (expression.value) {
    return `${expression.value} = ...`;
  }

  return 'Calculating...';
});

const fullContent = computed(() => {
  const parts: string[] = [];

  if (expression.value) {
    parts.push(`Expression: ${expression.value}`);
  }

  if (props.toolCall.function_args) {
    try {
      const parsed = JSON.parse(props.toolCall.function_args);
      parts.push(`Arguments:\n${JSON.stringify(parsed, null, 2)}`);
    } catch {
      parts.push(`Arguments: ${props.toolCall.function_args}`);
    }
  }

  if (props.result) {
    parts.push(`\nResult: ${props.result.result}`);
  }

  return parts.join('\n');
});
</script>

<template>
  <EventPill
    :icon="CALCULATOR_ICON"
    :label="label"
    :full-content="fullContent"
    type="tool-call"
    :is-loading="isLoading"
  />
</template>

<style scoped>
/* Override default tool-call color for calculator */
:deep(.event-pill.tool-call) {
  background: #fff7ed;
  color: #c2410c;
  border: 1px solid #fed7aa;
}
</style>
