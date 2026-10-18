<script setup lang="ts">
import { ref } from 'vue';
import { getOperatorSecret, setOperatorSecret } from '@/api/operatorSecret';

/** Where the operator types the secret the gated sandbox routes need; shared by every panel in this tab. */
defineProps<{ purpose: string }>();

const secret = ref(getOperatorSecret());

function update(value: string): void {
  secret.value = value;
  setOperatorSecret(value);
}
</script>

<template>
  <label class="operator-secret">
    <span>Operator secret <span class="hint">({{ purpose }})</span></span>
    <input
      :value="secret"
      type="password"
      autocomplete="off"
      data-testid="operator-secret"
      @input="update(($event.target as HTMLInputElement).value)"
    />
  </label>
</template>

<style scoped>
.operator-secret {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
  font-size: 0.8rem;
}

.operator-secret input {
  padding: 0.25rem 0.4rem;
  border: 1px solid var(--border-color, #d0d0d0);
  border-radius: 6px;
}

.hint {
  color: var(--muted, #888);
}
</style>
