<script setup lang="ts">
import { ref } from 'vue';

const props = defineProps<{
  disabled?: boolean;
}>();

const emit = defineEmits<{
  send: [message: string];
}>();

const inputText = ref('');

function handleSubmit() {
  const text = inputText.value.trim();
  if (text && !props.disabled) {
    emit('send', text);
    inputText.value = '';
  }
}

function handleKeydown(event: KeyboardEvent) {
  if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault();
    handleSubmit();
  }
}
</script>

<template>
  <div class="chat-input">
    <textarea
      v-model="inputText"
      :disabled="disabled"
      placeholder="Type a message..."
      rows="2"
      @keydown="handleKeydown"
    />
    <button
      :disabled="disabled || !inputText.trim()"
      @click="handleSubmit"
    >
      Send
    </button>
  </div>
</template>

<style scoped>
.chat-input {
  display: flex;
  gap: 8px;
  padding: 16px;
  border-top: 1px solid #e0e0e0;
  background: #fff;
}

textarea {
  flex: 1;
  padding: 12px;
  border: 1px solid #e0e0e0;
  border-radius: 8px;
  font-family: inherit;
  font-size: 14px;
  resize: none;
}

textarea:focus {
  outline: none;
  border-color: #007bff;
}

textarea:disabled {
  background: #f5f5f5;
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

button:hover:not(:disabled) {
  background: #0056b3;
}

button:disabled {
  background: #ccc;
  cursor: not-allowed;
}
</style>
