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
      <p id="chat-input-hint" class="input-hint" data-testid="chat-input-hint">
        Enter to {{ streaming ? 'queue' : 'send' }} · Shift+Enter for a new line
      </p>
    </div>
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
</template>

<style scoped>
.chat-input {
  display: flex;
  align-items: flex-end;
  gap: 10px;
  padding: 12px 16px;
  border-top: 1px solid #e0e0e0;
  background: #fff;
}

.input-field {
  flex: 1;
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
  padding: 12px;
  border: 1px solid #e0e0e0;
  border-radius: 8px;
  font-family: inherit;
  font-size: 14px;
  resize: none;
}

textarea:focus-visible {
  outline: 2px solid #2d6cdf;
  outline-offset: 2px;
  border-color: #007bff;
}

textarea:disabled {
  background: #f5f5f5;
}

.input-hint {
  margin: 5px 2px 0;
  color: #687481;
  font-size: 12px;
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
    gap: 8px;
    padding: 10px 12px;
  }

  button {
    padding-inline: 16px;
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
