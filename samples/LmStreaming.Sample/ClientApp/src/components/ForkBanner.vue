<script setup lang="ts">
import { computed } from 'vue';
import type { ConversationSummary } from '@/types/conversations';

/**
 * The strip at the top of a forked conversation: where it came from, a link back, and the warning
 * that the workspace is shared (files are not rewound to the fork point). Renders nothing for a
 * conversation that is not a fork.
 */
const props = defineProps<{
  conversation: ConversationSummary | null | undefined;
  conversations: readonly ConversationSummary[];
}>();
const emit = defineEmits<{ open: [threadId: string] }>();

const origin = computed(() => props.conversation?.forkedFrom ?? null);
const parent = computed(() =>
  origin.value ? props.conversations.find((row) => row.threadId === origin.value!.threadId) ?? null : null
);
</script>

<template>
  <div v-if="origin" class="fork-banner" data-testid="fork-banner" role="note">
    <div>
      <span aria-hidden="true">⑂ </span>
      Forked from
      <template v-if="parent?.title">“{{ parent.title }}”</template>
      <template v-else>another conversation</template>
      <template v-if="origin.seq != null"> at msg {{ origin.seq }}</template>
      <template v-if="!parent?.deleted">
        ·
        <button type="button" class="fork-banner-open" data-testid="fork-banner-open" @click="emit('open', origin.threadId)">
          open
        </button>
      </template>
    </div>
    <div class="fork-banner-warning">⚠ Workspace files are shared, not rewound.</div>
  </div>
</template>

<style scoped>
.fork-banner {
  margin: 8px clamp(12px, 3vw, 28px) 0;
  padding: 6px 10px;
  border: 1px solid #d6d9dd;
  border-radius: 6px;
  background: #f8f9fa;
  color: #41505f;
  font-size: 12px;
  line-height: 1.5;
}

.fork-banner-open {
  padding: 0;
  border: 0;
  background: none;
  color: #0056b3;
  font: inherit;
  text-decoration: underline;
  cursor: pointer;
}

.fork-banner-warning {
  color: #8a5a00;
}
</style>
