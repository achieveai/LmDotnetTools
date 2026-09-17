<script setup lang="ts">
import { ref } from 'vue';

const props = defineProps<{
  disabled?: boolean;
  streaming?: boolean;
}>();

const emit = defineEmits<{
  send: [message: string];
  cancel: [];
}>();

const inputText = ref('');

function handleSubmit() {
  const text = inputText.value.trim();
  if (text && !props.disabled) {
    emit('send', text);
    inputText.value = '';
  }
}

function handleCancel() {
  emit('cancel');
}

function handleKeydown(event: KeyboardEvent) {
  if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault();
    handleSubmit();
  }
}
</script>

<template>
  <div class="chat-input" data-testid="chat-input">
    <div v-if="$slots['project-control']" class="project-control" data-testid="chat-input-project-control">
      <slot name="project-control" />
    </div>
    <div class="chat-input-surface" data-testid="chat-input-surface">
      <div class="input-field">
        <label class="sr-only" for="chat-message-input">Message</label>
        <textarea
          id="chat-message-input"
          v-model="inputText"
          :disabled="disabled"
          aria-describedby="chat-input-hint"
          placeholder="Type a message..."
          rows="2"
          data-testid="chat-input-textarea"
          @keydown="handleKeydown"
        />
      </div>
      <p id="chat-input-hint" class="input-hint" data-testid="chat-input-hint">
        Enter to {{ streaming ? 'queue' : 'send' }} · Shift+Enter for a new line
      </p>
      <div class="composer-footer" data-testid="chat-input-footer">
        <div v-if="$slots['mode-control']" class="composer-mode" data-testid="chat-input-mode-control">
          <slot name="mode-control" />
        </div>
        <div class="composer-actions" data-testid="chat-input-actions">
          <slot name="context-control" />
          <button
            v-if="streaming && inputText.trim()"
            class="queue-button"
            data-testid="queue-button"
            @click="handleSubmit"
          >
            Queue
          </button>
          <button
            v-else-if="streaming"
            class="stop-button"
            data-testid="stop-button"
            @click="handleCancel"
          >
            Stop
          </button>
          <button
            v-else
            class="send-button"
            :disabled="disabled || !inputText.trim()"
            data-testid="send-button"
            aria-label="Send message"
            title="Send message"
            @click="handleSubmit"
          >
            <svg viewBox="0 0 16 16" aria-hidden="true" focusable="false">
              <path d="M8 13V3m0 0L4.5 6.5M8 3l3.5 3.5" />
            </svg>
          </button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.chat-input {
  display: flex;
  flex-direction: column;
  gap: 4px;
  box-sizing: border-box;
  margin: 12px 16px;
}

.project-control {
  width: 100%;
}

.chat-input-surface {
  display: flex;
  flex-direction: column;
  gap: 4px;
  box-sizing: border-box;
  padding: 12px 12px 10px;
  border: 1px solid #d9dde3;
  border-radius: 22px;
  background: #fff;
  transition: border-color 0.15s, box-shadow 0.15s;
}

.chat-input-surface:focus-within {
  border-color: #9da7b3;
  box-shadow: 0 0 0 2px rgb(45 108 223 / 16%);
}

.input-field {
  width: 100%;
  min-width: 0;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}

textarea {
  display: block;
  width: 100%;
  box-sizing: border-box;
  min-height: 48px;
  padding: 2px 4px 10px;
  border: 0;
  border-radius: 0;
  background: transparent;
  font-family: inherit;
  font-size: 14px;
  line-height: 1.5;
  resize: none;
}

textarea:focus-visible {
  outline: none;
}

textarea:disabled {
  color: #777;
  background: transparent;
}

.composer-footer {
  display: flex;
  flex-wrap: nowrap;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  width: 100%;
  min-width: 0;
}

.composer-actions {
  display: flex;
  flex: 0 1 auto;
  min-width: 0;
  align-items: center;
  justify-content: flex-end;
  gap: 6px;
  margin-left: auto;
}

.composer-mode {
  display: flex;
  flex: 1 1 0;
  min-width: 0;
  align-items: center;
  position: relative;
  z-index: 5;
}

.composer-mode :deep(.selector-btn) {
  width: 100%;
  max-width: 220px;
  min-width: 0;
  padding: 6px 8px;
  border-color: transparent;
  background: transparent;
}

.composer-mode :deep(.selector-btn:hover:not(:disabled)) {
  background: #f1f2f4;
}

.composer-actions :deep(.provider-selector) {
  min-width: 0;
}

.composer-actions :deep(.selector-btn) {
  max-width: min(52vw, 220px);
  padding: 6px 8px;
  border-color: transparent;
  background: transparent;
}

.composer-actions :deep(.selector-btn:hover:not(:disabled)) {
  background: #f1f2f4;
}

.composer-actions :deep(.provider-label) {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}

.input-hint {
  margin: -2px 4px 0;
  color: #818892;
  font-size: 11px;
  line-height: 1.35;
}

button {
  padding: 12px 24px;
  background: #007bff;
  color: white;
  border: none;
  border-radius: 8px;
  font-size: 14px;
  font-weight: 500;
  cursor: pointer;
  transition: background 0.2s;
}

button:focus-visible {
  outline: 2px solid #2d6cdf;
  outline-offset: 2px;
}

@media (max-width: 520px) {
  .chat-input {
    margin: 8px 10px;
  }

  .chat-input-surface {
    padding: 10px 10px 8px;
  }

  button {
    padding-inline: 16px;
  }

  .composer-actions :deep(.selector-btn) {
    max-width: 42vw;
  }

  .composer-mode :deep(.selector-btn) {
    max-width: 100%;
  }
}

button:hover:not(:disabled) {
  background: #0056b3;
}

button:disabled {
  background: #ccc;
  cursor: not-allowed;
}

.stop-button {
  background: #dc3545;
}

.stop-button:hover:not(:disabled) {
  background: #b02a37;
}

.queue-button {
  background: #0d6efd;
}

.queue-button:hover:not(:disabled) {
  background: #0b5ed7;
}

.send-button {
  display: inline-flex;
  width: 36px;
  height: 36px;
  flex: 0 0 36px;
  align-items: center;
  justify-content: center;
  padding: 0;
  border-radius: 50%;
  background: #111;
}

.send-button svg {
  width: 18px;
  height: 18px;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.8;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.send-button:hover:not(:disabled) {
  background: #000;
}

.send-button:disabled {
  background: #e2e4e7;
  color: #8a9097;
}
</style>
