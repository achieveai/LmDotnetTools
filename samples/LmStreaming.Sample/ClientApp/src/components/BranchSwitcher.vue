<script setup lang="ts">
import { computed } from 'vue';
import type { BranchPoint } from '@/api/conversationsApi';

/**
 * "◀ i / N ▶" at a message where the history splits. Each option is a whole conversation; the arrows
 * ask the parent to open the neighbouring one. No wrap-around: the ends disable their arrow.
 */
const props = defineProps<{ point: BranchPoint }>();
const emit = defineEmits<{ open: [threadId: string] }>();

const index = computed(() => Math.max(0, props.point.options.findIndex((option) => option.current)));
const previous = computed(() => props.point.options[index.value - 1] ?? null);
const next = computed(() => props.point.options[index.value + 1] ?? null);
</script>

<template>
  <div class="branch-switcher" data-testid="branch-switcher" role="group" aria-label="Conversation branches">
    <button
      type="button"
      class="branch-arrow"
      data-testid="branch-switcher-prev"
      :disabled="!previous"
      :title="previous ? `Open “${previous.title}”` : undefined"
      aria-label="Previous branch"
      @click="previous && emit('open', previous.threadId)"
    >
      ◀
    </button>
    <span class="branch-label" data-testid="branch-switcher-label">{{ index + 1 }} / {{ point.options.length }}</span>
    <button
      type="button"
      class="branch-arrow"
      data-testid="branch-switcher-next"
      :disabled="!next"
      :title="next ? `Open “${next.title}”` : undefined"
      aria-label="Next branch"
      @click="next && emit('open', next.threadId)"
    >
      ▶
    </button>
    <span class="branch-mark" aria-hidden="true">⑂</span>
  </div>
</template>

<style scoped>
.branch-switcher {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  align-self: flex-end;
  color: #59636e;
  font-size: 12px;
}

.branch-arrow {
  width: 22px;
  height: 22px;
  padding: 0;
  border: 1px solid #d6d9dd;
  border-radius: 5px;
  background: #fff;
  color: inherit;
  font-size: 10px;
  cursor: pointer;
}

.branch-arrow:disabled {
  cursor: default;
  opacity: 0.4;
}

.branch-arrow:focus-visible {
  outline: 2px solid #007bff;
  outline-offset: 1px;
}
</style>
