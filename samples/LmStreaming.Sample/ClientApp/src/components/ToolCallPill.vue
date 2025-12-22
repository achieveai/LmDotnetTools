<script setup lang="ts">
import { computed } from 'vue';
import EventPill from './EventPill.vue';
import { formatToolCallPreview, formatToolCallWithResult } from '@/utils';
import type { ToolCall, ToolCallResultMessage } from '@/types';

const props = defineProps<{
  toolCall: ToolCall;
  result?: ToolCallResultMessage | null;
}>();

const TOOL_ICON = '\u{1F527}'; // Wrench emoji

const isLoading = computed(() => !props.result);

const label = computed(() => {
  const functionName = props.toolCall.function_name || 'unknown';

  if (props.result) {
    return formatToolCallWithResult(
      functionName,
      props.toolCall.function_args,
      props.result.result,
      50
    );
  }

  return formatToolCallPreview(functionName, props.toolCall.function_args);
});

const fullContent = computed(() => {
  const parts: string[] = [];

  // Function call details
  parts.push(`Function: ${props.toolCall.function_name || 'unknown'}`);

  if (props.toolCall.function_args) {
    try {
      const parsed = JSON.parse(props.toolCall.function_args);
      parts.push(`Arguments:\n${JSON.stringify(parsed, null, 2)}`);
    } catch {
      parts.push(`Arguments: ${props.toolCall.function_args}`);
    }
  }

  // Result if available
  if (props.result) {
    parts.push(`\nResult:\n${props.result.result}`);
  }

  return parts.join('\n');
});
</script>

<template>
  <EventPill
    :icon="TOOL_ICON"
    :label="label"
    :full-content="fullContent"
    type="tool-call"
    :is-loading="isLoading"
  />
</template>
