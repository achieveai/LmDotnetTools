<script setup lang="ts">
import { ref, computed } from 'vue';

export type PillType = 'thinking' | 'tool-call' | 'tool-result';

const props = defineProps<{
  icon: string;
  label: string;
  fullContent?: string;
  type: PillType;
  isLoading?: boolean;
}>();

const isExpanded = ref(false);

const hasExpandableContent = computed(() => {
  return props.fullContent && props.fullContent !== props.label;
});

function toggleExpand() {
  if (hasExpandableContent.value) {
    isExpanded.value = !isExpanded.value;
  }
}
</script>

<template>
  <div
    class="event-pill"
    :class="[type, { expanded: isExpanded, expandable: hasExpandableContent, loading: isLoading }]"
    @click="toggleExpand"
  >
    <div class="pill-header">
      <span class="pill-icon">{{ icon }}</span>
      <span class="pill-label">{{ label }}</span>
      <span v-if="isLoading" class="pill-spinner"></span>
      <span v-else-if="hasExpandableContent" class="pill-expand-icon">
        {{ isExpanded ? '▼' : '▶' }}
      </span>
    </div>
    <div v-if="isExpanded && fullContent" class="pill-content">
      <pre>{{ fullContent }}</pre>
    </div>
  </div>
</template>

<style scoped>
.event-pill {
  display: inline-flex;
  flex-direction: column;
  gap: 4px;
  padding: 8px 14px;
  border-radius: 20px;
  font-size: 13px;
  transition: all 0.2s ease;
  max-width: 100%;
}

.event-pill.expandable {
  cursor: pointer;
}

.event-pill.expandable:hover {
  filter: brightness(0.95);
}

.pill-header {
  display: flex;
  align-items: center;
  gap: 6px;
}

.pill-icon {
  flex-shrink: 0;
}

.pill-label {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.pill-expand-icon {
  font-size: 10px;
  opacity: 0.6;
  margin-left: auto;
  flex-shrink: 0;
}

.pill-spinner {
  width: 12px;
  height: 12px;
  border: 2px solid currentColor;
  border-top-color: transparent;
  border-radius: 50%;
  animation: spin 1s linear infinite;
  margin-left: auto;
  flex-shrink: 0;
}

@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}

.pill-content {
  padding-top: 8px;
  border-top: 1px solid rgba(0, 0, 0, 0.1);
}

.pill-content pre {
  margin: 0;
  font-size: 12px;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 200px;
  overflow-y: auto;
}

.event-pill.expanded {
  border-radius: 12px;
}

/* Type-specific colors */
.event-pill.thinking {
  background: #e8f4f8;
  color: #2c5282;
  border: 1px solid #bee3f8;
}

.event-pill.tool-call {
  background: #f0fff4;
  color: #276749;
  border: 1px solid #c6f6d5;
}

.event-pill.tool-result {
  background: #faf5ff;
  color: #553c9a;
  border: 1px solid #e9d8fd;
}

/* Loading state */
.event-pill.loading {
  opacity: 0.8;
}
</style>
