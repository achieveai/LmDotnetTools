<script setup lang="ts">
import { computed } from 'vue';
import EventPill from '../EventPill.vue';
import { truncateText } from '@/utils';
import type { ToolCall, ToolCallResultMessage } from '@/types';

const props = defineProps<{
  toolCall: ToolCall;
  result?: ToolCallResultMessage | null;
}>();

const isLoading = computed(() => !props.result);

const location = computed(() => {
  if (!props.toolCall.function_args) {
    return '';
  }

  try {
    const args = JSON.parse(props.toolCall.function_args);
    // Common arg names for weather tools
    return args.location || args.city || args.place || args.query || '';
  } catch {
    return '';
  }
});

const weatherIcon = computed(() => {
  if (!props.result) {
    return '\u{1F324}'; // Sun behind cloud (loading)
  }

  const resultLower = props.result.result.toLowerCase();

  if (resultLower.includes('sunny') || resultLower.includes('clear')) {
    return '\u{2600}'; // Sun
  }
  if (resultLower.includes('cloud')) {
    return '\u{2601}'; // Cloud
  }
  if (resultLower.includes('rain')) {
    return '\u{1F327}'; // Rain cloud
  }
  if (resultLower.includes('snow')) {
    return '\u{1F328}'; // Snow cloud
  }
  if (resultLower.includes('storm') || resultLower.includes('thunder')) {
    return '\u{26C8}'; // Thunder cloud
  }
  if (resultLower.includes('fog') || resultLower.includes('mist')) {
    return '\u{1F32B}'; // Fog
  }

  return '\u{1F324}'; // Default: sun behind cloud
});

const label = computed(() => {
  if (props.result) {
    // Show "Location: Weather" format like "San Francisco: Sunny"
    if (location.value) {
      return `${location.value}: ${truncateText(props.result.result, 30)}`;
    }
    return truncateText(props.result.result, 40);
  }

  if (location.value) {
    return `${location.value}: Loading...`;
  }

  return 'Fetching weather...';
});

const fullContent = computed(() => {
  const parts: string[] = [];

  if (location.value) {
    parts.push(`Location: ${location.value}`);
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
    parts.push(`\nWeather:\n${props.result.result}`);
  }

  return parts.join('\n');
});
</script>

<template>
  <EventPill
    :icon="weatherIcon"
    :label="label"
    :full-content="fullContent"
    type="tool-call"
    :is-loading="isLoading"
  />
</template>

<style scoped>
/* Override default tool-call color for weather - sky blue */
:deep(.event-pill.tool-call) {
  background: #e0f2fe;
  color: #0369a1;
  border: 1px solid #7dd3fc;
}
</style>
