<script setup lang="ts">
import { computed } from 'vue';
import EventPill from './EventPill.vue';
import { truncateText } from '@/utils';
import type { ReasoningMessage } from '@/types';

const props = defineProps<{
  message: ReasoningMessage;
}>();

const THINKING_ICON = '\u{1F9E0}'; // Brain emoji

const label = computed(() => {
  const preview = truncateText(props.message.reasoning, 40);
  return `Thinking: ${preview}`;
});

const fullContent = computed(() => props.message.reasoning);
</script>

<template>
  <EventPill
    :icon="THINKING_ICON"
    :label="label"
    :full-content="fullContent"
    type="thinking"
  />
</template>
