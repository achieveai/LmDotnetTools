<script setup lang="ts">
import { computed, ref } from 'vue';
import BaseModal from './BaseModal.vue';

export interface QuestionInboxItem {
  key: string;
  conversationTitle: string;
  agentName: string;
  prompt: string;
}
const props = withDefaults(
  defineProps<{
    entries: QuestionInboxItem[];
    refreshing: boolean;
    error: string | null;
    disabled?: boolean;
    /**
     * How many of `entries` are waiting in a conversation OTHER than the one on screen. Drives the
     * pulsing accent below: the count alone reads the same whether the question is in front of the
     * user or three conversations away, and the second case is the one that gets missed.
     */
    elsewhere?: number;
  }>(),
  { elsewhere: 0 }
);
const label = computed(() =>
  props.elsewhere
    ? `Needs your answer: ${props.entries.length} pending requests, ${props.elsewhere} in another conversation`
    : `Needs your answer: ${props.entries.length} pending requests`
);
const emit = defineEmits<{ select: [key: string]; refresh: [] }>();
const open = ref(false);
function show(): void { open.value = true; emit('refresh'); }
function select(key: string): void { open.value = false; emit('select', key); }
</script>

<template>
  <button class="question-inbox-trigger" data-testid="question-inbox-trigger"
    :class="{ 'has-questions': entries.length, 'pending-elsewhere': elsewhere > 0 }" :disabled="disabled"
    :aria-label="label"
    :title="elsewhere ? 'Needs your answer in another conversation' : 'Needs your answer'" @click="show">
    <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M5 3h10a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H9l-4 3v-3a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z"/><path d="M8 7a2 2 0 1 1 3 1.7c-.7.4-1 1-1 1.3M10 12h.01"/></svg>
    <span v-if="entries.length" class="question-inbox-count">{{ entries.length }}</span>
    <span v-if="elsewhere" class="question-inbox-pulse" data-testid="question-inbox-elsewhere" aria-hidden="true"></span>
  </button>
  <BaseModal v-if="open" title="Needs your answer" data-test-id="question-inbox-modal" @close="open = false">
    <div class="question-inbox-body">
      <div class="question-inbox-intro"><p>Questions from your conversations and agents.</p>
        <button :disabled="refreshing" @click="emit('refresh')">{{ refreshing ? 'Checking…' : 'Refresh' }}</button>
      </div>
      <p v-if="error" class="question-inbox-error" role="status">{{ error }}</p>
      <p v-if="!entries.length" class="question-inbox-empty">{{ refreshing ? 'Checking for questions…' : 'No questions waiting for you.' }}</p>
      <ul v-else class="question-inbox-list">
        <li v-for="entry in entries" :key="entry.key">
          <button class="question-inbox-item" :disabled="disabled" @click="select(entry.key)">
            <span class="question-inbox-source">{{ entry.conversationTitle }} <span aria-hidden="true">·</span> {{ entry.agentName }}</span>
            <span class="question-inbox-prompt">{{ entry.prompt }}</span>
            <span class="question-inbox-action">Review question <span aria-hidden="true">→</span></span>
          </button>
        </li>
      </ul>
    </div>
  </BaseModal>
</template>

<style scoped>
.question-inbox-trigger { display: flex; align-items: center; justify-content: center; gap: 5px; min-width: 34px; height: 34px; padding: 6px; border: 1px solid #d6dbe1; border-radius: 6px; color: #5f6874; background: white; cursor: pointer; }
.question-inbox-trigger svg { width: 18px; height: 18px; fill: none; stroke: currentColor; stroke-width: 1.4; stroke-linecap: round; stroke-linejoin: round; }
.question-inbox-trigger.has-questions { background: #edf3fc; color: #315c92; border-color: #c3d3e9; }
.question-inbox-trigger.pending-elsewhere { position: relative; background: #fff4e5; color: #8a5300; border-color: #f0c98a; }
.question-inbox-pulse { position: absolute; top: -3px; right: -3px; width: 9px; height: 9px; border-radius: 50%; background: #e07a00; box-shadow: 0 0 0 0 rgba(224, 122, 0, .55); animation: question-inbox-pulse 1.8s ease-out infinite; }
@keyframes question-inbox-pulse { 70% { box-shadow: 0 0 0 7px rgba(224, 122, 0, 0); } 100% { box-shadow: 0 0 0 0 rgba(224, 122, 0, 0); } }
@media (prefers-reduced-motion: reduce) { .question-inbox-pulse { animation: none; } }
.question-inbox-count { font-size: 12px; font-weight: 600; }
button:focus-visible { outline: 2px solid #2d6cdf; outline-offset: 2px; }
button:disabled { opacity: .55; cursor: default; }
.question-inbox-body { padding: 18px 20px 22px; color: #5f6874; }
.question-inbox-intro { display: flex; align-items: center; justify-content: space-between; gap: 12px; font-size: 13px; }
.question-inbox-intro p { margin: 0; }
.question-inbox-intro button { border: 0; background: transparent; color: #315c92; padding: 8px; cursor: pointer; }
.question-inbox-list { list-style: none; padding: 0; margin: 16px 0 0; display: grid; gap: 10px; }
.question-inbox-item { display: flex; flex-direction: column; gap: 8px; width: 100%; text-align: left; padding: 14px 16px; border: 1px solid #e0e5eb; background: white; border-radius: 10px; cursor: pointer; }
.question-inbox-item:hover { background: #f7f9fc; border-color: #bac9dd; }
.question-inbox-source { font-size: 12px; color: #697586; overflow-wrap: anywhere; }
.question-inbox-prompt { font-size: 15px; line-height: 1.5; color: #303b49; overflow-wrap: anywhere; }
.question-inbox-action { font-size: 12px; color: #315c92; }
.question-inbox-empty { text-align: center; padding: 28px 0; font-size: 14px; }
.question-inbox-error { padding: 10px 12px; background: #fff8ed; color: #795719; border-radius: 6px; font-size: 13px; }
</style>
