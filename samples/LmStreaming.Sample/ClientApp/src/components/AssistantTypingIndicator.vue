<script setup lang="ts">
</script>

<template>
  <div class="assistant-message-wrapper">
    <div class="assistant-message-container">
      <div class="assistant-avatar group-avatar">&#x1F916;</div>
      <div class="assistant-content">
        <div class="typing-bubble">
          <div class="typing-indicator">
            <span class="typing-dot"></span>
            <span class="typing-dot"></span>
            <span class="typing-dot"></span>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.assistant-message-wrapper {
  display: flex;
  margin-right: auto;
  width: 100%;
}

.assistant-message-container {
  display: flex;
  gap: 12px;
  align-items: flex-start;
  width: 100%;
  min-width: 0;
}

.assistant-avatar {
  width: 40px;
  height: 40px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 20px;
  flex-shrink: 0;
  background: #6c757d;
  align-self: flex-start;
  position: sticky;
  top: 8px;
}

.assistant-content {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.typing-bubble {
  background: #ffffff;
  border: 1px solid #e0e0e0;
  border-radius: 16px 16px 16px 4px;
  padding: 14px 20px;
  display: inline-flex;
  align-items: center;
  width: fit-content;
}

.typing-indicator {
  display: flex;
  align-items: center;
  gap: 5px;
}

.typing-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: #90949c;
  animation: typing-bounce 1.4s infinite ease-in-out both;
}

.typing-dot:nth-child(1) {
  animation-delay: -0.32s;
}

.typing-dot:nth-child(2) {
  animation-delay: -0.16s;
}

@keyframes typing-bounce {
  0%, 80%, 100% {
    transform: scale(0.6);
    opacity: 0.4;
  }
  40% {
    transform: scale(1);
    opacity: 1;
  }
}
</style>
