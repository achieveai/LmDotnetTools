<script setup lang="ts">
import { onBeforeUnmount, onMounted } from 'vue';

/**
 * Transient notice that an agent somewhere ELSE has just parked a question.
 *
 * Deliberately not a dialog and not a navigation: answering resolves a deferred client tool over
 * that conversation's own socket, so opening the form means leaving whatever the user is reading.
 * The toast says which conversation and which agent is waiting and puts review one click away; if it
 * is ignored it expires, and the header count and the "elsewhere" row still hold the question.
 */
const props = withDefaults(
  defineProps<{
    /** `<conversation> · <agent>` — where the question came from. */
    source: string;
    prompt: string;
    /** How long before it retires itself. */
    durationMs?: number;
  }>(),
  { durationMs: 12_000 }
);

const emit = defineEmits<{ review: []; dismiss: [] }>();

let timer: ReturnType<typeof setTimeout> | undefined;
onMounted(() => {
  if (props.durationMs > 0) timer = setTimeout(() => emit('dismiss'), props.durationMs);
});
onBeforeUnmount(() => {
  if (timer !== undefined) clearTimeout(timer);
});
</script>

<template>
  <div class="question-toast" role="status" aria-live="polite" data-testid="question-toast">
    <div class="question-toast-body">
      <span class="question-toast-source" data-testid="question-toast-source">{{ source }}</span>
      <span class="question-toast-prompt">{{ prompt }}</span>
    </div>
    <div class="question-toast-actions">
      <button class="question-toast-review" data-testid="question-toast-review" @click="emit('review')">
        Review
      </button>
      <button
        class="question-toast-dismiss"
        data-testid="question-toast-dismiss"
        aria-label="Dismiss"
        @click="emit('dismiss')"
      >
        <span aria-hidden="true">×</span>
      </button>
    </div>
  </div>
</template>

<style scoped>
.question-toast { position: fixed; right: 18px; bottom: 18px; z-index: 60; display: flex; align-items: flex-start; gap: 14px; max-width: 360px; padding: 14px 14px 14px 16px; border: 1px solid #f0c98a; border-left: 4px solid #e07a00; border-radius: 10px; background: white; box-shadow: 0 10px 28px rgba(28, 36, 48, .18); }
.question-toast-body { display: flex; flex-direction: column; gap: 5px; min-width: 0; }
.question-toast-source { font-size: 12px; color: #8a5300; overflow-wrap: anywhere; }
.question-toast-prompt { font-size: 14px; line-height: 1.45; color: #303b49; overflow-wrap: anywhere; display: -webkit-box; -webkit-line-clamp: 3; line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden; }
.question-toast-actions { display: flex; align-items: center; gap: 6px; }
.question-toast-review { padding: 7px 12px; border: 1px solid #c3d3e9; border-radius: 6px; background: #edf3fc; color: #315c92; font-size: 13px; cursor: pointer; }
.question-toast-review:hover { background: #e2ecfa; }
.question-toast-dismiss { padding: 4px 8px; border: 0; border-radius: 6px; background: transparent; color: #697586; font-size: 16px; line-height: 1; cursor: pointer; }
.question-toast-dismiss:hover { background: #f2f4f7; }
button:focus-visible { outline: 2px solid #2d6cdf; outline-offset: 2px; }
@media (max-width: 768px) { .question-toast { right: 12px; left: 12px; bottom: 12px; max-width: none; } }
</style>
