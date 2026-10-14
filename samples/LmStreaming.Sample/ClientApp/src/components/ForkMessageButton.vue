<script setup lang="ts">
/**
 * A fork action on one message. Icon plus short label; MessageList decides where it shows and what the
 * click means ("Fork from here" forks after the run, "Edit in fork" forks before the user message).
 */
const props = defineProps<{ kind: 'fork-from-here' | 'edit-in-fork' }>();
defineEmits<{ click: [] }>();

const LABELS = {
  'fork-from-here': { icon: '⑂', text: 'Fork from here', title: 'Start a new conversation that ends here' },
  'edit-in-fork': { icon: '✎', text: 'Edit in fork', title: 'Start a new conversation from just before this message, with it ready to edit' },
} as const;
const label = LABELS[props.kind];
</script>

<template>
  <button
    type="button"
    class="fork-message-button"
    :title="label.title"
    :data-testid="`${kind}-button`"
    @click="$emit('click')"
  >
    <span aria-hidden="true">{{ label.icon }}</span>
    <span class="fork-message-label">{{ label.text }}</span>
  </button>
</template>

<style scoped>
.fork-message-button {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 26px;
  padding: 0 8px;
  border: 1px solid #e0e0e0;
  border-radius: 6px;
  background: #ffffff;
  color: #666;
  font-size: 12px;
  cursor: pointer;
  white-space: nowrap;
}

.fork-message-button:hover {
  background: #f3f4f6;
  color: #111;
}

.fork-message-button:focus-visible {
  outline: 2px solid #007bff;
  outline-offset: 1px;
}
</style>
