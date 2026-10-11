<script setup lang="ts">
import { onMounted, ref } from 'vue';
import {
  listAppMarketplaces,
  registerAppMarketplace,
  removeAppMarketplace,
  syncAppMarketplace,
  type AppMarketplace,
} from '@/api/appMarketplacesApi';
import OperatorSecretField from '@/components/OperatorSecretField.vue';

/** `changed` fires after a register or remove, so the catalog below can reload with the alias's plugins. */
const emit = defineEmits<{ changed: [] }>();

const rows = ref<AppMarketplace[]>([]);
const alias = ref('');
const url = ref('');
const gitRef = ref('');
/** The action in flight (`register`, `sync:<alias>`, ...); every button is disabled while one runs. */
const busy = ref<string | null>(null);
const error = ref<string | null>(null);

async function run(action: string, work: () => Promise<void>): Promise<void> {
  busy.value = action;
  error.value = null;
  try {
    await work();
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    busy.value = null;
  }
}

function load(): Promise<void> {
  return run('list', async () => {
    rows.value = await listAppMarketplaces();
  });
}

function register(): Promise<void> {
  return run('register', async () => {
    await registerAppMarketplace(alias.value.trim(), url.value.trim(), gitRef.value);
    alias.value = '';
    url.value = '';
    gitRef.value = '';
    rows.value = await listAppMarketplaces();
    emit('changed');
  });
}

function sync(name: string): Promise<void> {
  return run(`sync:${name}`, async () => {
    const row = await syncAppMarketplace(name);
    rows.value = rows.value.map((r) => (r.alias === name ? row : r));
  });
}

function remove(name: string): Promise<void> {
  return run(`remove:${name}`, async () => {
    await removeAppMarketplace(name);
    rows.value = rows.value.filter((r) => r.alias !== name);
    emit('changed');
  });
}

onMounted(() => load());
</script>

<template>
  <section class="app-marketplaces" data-testid="app-marketplaces">
    <h3 class="am-title">Git marketplaces (this app)</h3>

    <OperatorSecretField purpose="register, sync, remove" />

    <form class="am-form" @submit.prevent="register">
      <input v-model="alias" aria-label="Alias" placeholder="alias" required data-testid="app-marketplace-alias" />
      <input
        v-model="url"
        aria-label="Repository URL"
        placeholder="https://github.com/owner/repo"
        required
        data-testid="app-marketplace-url"
      />
      <input v-model="gitRef" aria-label="Git ref" placeholder="ref (latest)" data-testid="app-marketplace-ref" />
      <button type="submit" :disabled="busy !== null" data-testid="app-marketplace-register">
        {{ busy === 'register' ? 'Registering…' : 'Register' }}
      </button>
    </form>

    <p v-if="error" class="am-error" role="alert" data-testid="app-marketplace-error">{{ error }}</p>

    <p v-if="rows.length === 0 && busy !== 'list'" class="am-empty" data-testid="app-marketplace-empty">
      No git marketplaces registered.
    </p>
    <ul v-else class="am-list">
      <li v-for="row in rows" :key="row.alias" :data-testid="`app-marketplace-row-${row.alias}`">
        <div class="am-row-head">
          <span class="am-alias">{{ row.alias }}</span>
          <span class="am-meta">{{ row.ref }} @ <code :title="row.resolvedSha">{{ row.resolvedSha.slice(0, 12) }}</code></span>
          <button
            :disabled="busy !== null"
            :data-testid="`app-marketplace-sync-${row.alias}`"
            @click="sync(row.alias)"
          >
            {{ busy === `sync:${row.alias}` ? 'Syncing…' : 'Sync' }}
          </button>
          <button
            :disabled="busy !== null"
            :data-testid="`app-marketplace-remove-${row.alias}`"
            @click="remove(row.alias)"
          >
            Remove
          </button>
        </div>
        <div class="am-url">{{ row.url }}</div>
        <div v-if="row.lastError" class="am-error" :data-testid="`app-marketplace-last-error-${row.alias}`">
          Last sync failed: {{ row.lastError }}
        </div>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.app-marketplaces {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding: 1rem 1rem 0;
}

.am-title {
  margin: 0;
  font-size: 1rem;
}

.am-form {
  display: flex;
  flex-wrap: wrap;
  gap: 0.35rem;
}

.am-form input {
  flex: 1 1 8rem;
  min-width: 0;
  padding: 0.25rem 0.4rem;
  border: 1px solid var(--border-color, #d0d0d0);
  border-radius: 6px;
}

.am-form button,
.am-row-head button {
  border: 1px solid var(--border-color, #d0d0d0);
  border-radius: 6px;
  background: var(--surface, #fff);
  padding: 0.25rem 0.6rem;
  cursor: pointer;
}

button:disabled {
  opacity: 0.6;
  cursor: default;
}

.am-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.am-list li {
  border: 1px solid var(--border-color, #e3e3e3);
  border-radius: 8px;
  padding: 0.5rem 0.75rem;
}

.am-row-head {
  display: flex;
  align-items: baseline;
  gap: 0.5rem;
  flex-wrap: wrap;
}

.am-alias {
  font-weight: 600;
}

.am-meta,
.am-url,
.am-empty {
  /* #666 on the modal's white is 5.7:1, above WCAG's 4.5:1 for small text. */
  color: var(--muted, #666);
  font-size: 0.8rem;
}

.am-meta {
  flex: 1;
}

.am-url {
  word-break: break-all;
}

.am-error {
  color: var(--error, #b00020);
  font-size: 0.85rem;
  margin: 0;
}
</style>
