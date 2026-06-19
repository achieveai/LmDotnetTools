<script setup lang="ts">
import { onMounted } from 'vue';
import { useMarketplaces } from '@/composables/useMarketplaces';

const { marketplaces, isLoading, isGatewayOffline, isEmpty, error, load } = useMarketplaces();

onMounted(() => load());
</script>

<template>
  <section class="marketplace-browser" data-testid="marketplace-browser">
    <header class="mb-header">
      <h2 class="mb-title">Marketplaces</h2>
      <button
        class="mb-refresh"
        data-testid="marketplace-browser-refresh"
        :disabled="isLoading"
        @click="load()"
      >
        {{ isLoading ? 'Loading…' : 'Refresh' }}
      </button>
    </header>

    <p v-if="isLoading" class="mb-state" data-testid="marketplace-browser-loading">
      Loading marketplaces…
    </p>

    <div
      v-else-if="isGatewayOffline"
      class="mb-state mb-offline"
      data-testid="marketplace-browser-offline"
    >
      The sandbox gateway is offline, so the marketplace catalog is unavailable.
      <button class="mb-retry" @click="load()">Retry</button>
    </div>

    <div v-else-if="error" class="mb-state mb-error" data-testid="marketplace-browser-error">
      {{ error }}
      <button class="mb-retry" @click="load()">Retry</button>
    </div>

    <p v-else-if="isEmpty" class="mb-state" data-testid="marketplace-browser-empty">
      No marketplaces are configured.
    </p>

    <ul v-else class="mb-list">
      <li
        v-for="mk in marketplaces"
        :key="mk.alias"
        class="mb-marketplace"
        :data-testid="`marketplace-item-${mk.alias}`"
      >
        <div class="mb-marketplace-head">
          <span class="mb-alias">{{ mk.alias }}</span>
          <span class="mb-count">{{ mk.plugins.length }} plugin(s)</span>
        </div>

        <p
          v-if="mk.error"
          class="mb-marketplace-error"
          :data-testid="`marketplace-error-${mk.alias}`"
        >
          Failed to load: {{ mk.error }}
        </p>

        <ul class="mb-plugins">
          <li
            v-for="plugin in mk.plugins"
            :key="plugin.name"
            class="mb-plugin"
            :data-testid="`marketplace-plugin-${plugin.name}`"
          >
            <div class="mb-plugin-head">
              <span class="mb-plugin-name">{{ plugin.name }}</span>
              <span v-if="plugin.version" class="mb-plugin-version">v{{ plugin.version }}</span>
              <span class="mb-plugin-counts">
                {{ plugin.skills.length }} skill(s) · {{ plugin.agents.length }} agent(s)
              </span>
            </div>
            <p v-if="plugin.description" class="mb-plugin-desc">{{ plugin.description }}</p>

            <div v-if="plugin.skills.length > 0" class="mb-items">
              <span class="mb-items-label">Skills</span>
              <span
                v-for="skill in plugin.skills"
                :key="skill.path"
                class="mb-chip mb-chip-skill"
                :data-testid="`marketplace-skill-${skill.name}`"
                :title="skill.description"
              >
                {{ skill.name }}
              </span>
            </div>

            <div v-if="plugin.agents.length > 0" class="mb-items">
              <span class="mb-items-label">Agents</span>
              <span
                v-for="agent in plugin.agents"
                :key="agent.path"
                class="mb-chip mb-chip-agent"
                :data-testid="`marketplace-agent-${agent.name}`"
                :title="agent.description"
              >
                {{ agent.name }}
              </span>
            </div>
          </li>
        </ul>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.marketplace-browser {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  padding: 1rem;
}

.mb-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.mb-title {
  margin: 0;
  font-size: 1.1rem;
}

.mb-refresh,
.mb-retry {
  border: 1px solid var(--border-color, #d0d0d0);
  border-radius: 6px;
  background: var(--surface, #fff);
  padding: 0.25rem 0.6rem;
  cursor: pointer;
}

.mb-refresh:disabled {
  opacity: 0.6;
  cursor: default;
}

.mb-state {
  color: var(--muted, #666);
  padding: 0.5rem 0;
}

.mb-offline {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.mb-error {
  color: var(--error, #b00020);
}

.mb-list,
.mb-plugins {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.mb-marketplace {
  border: 1px solid var(--border-color, #e3e3e3);
  border-radius: 8px;
  padding: 0.75rem;
}

.mb-marketplace-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  margin-bottom: 0.5rem;
}

.mb-alias {
  font-weight: 600;
}

.mb-count,
.mb-plugin-counts,
.mb-plugin-version {
  color: var(--muted, #888);
  font-size: 0.8rem;
}

.mb-marketplace-error {
  color: var(--error, #b00020);
  font-size: 0.85rem;
  margin: 0 0 0.5rem;
}

.mb-plugins {
  gap: 0.5rem;
}

.mb-plugin {
  border-top: 1px solid var(--border-color, #f0f0f0);
  padding-top: 0.5rem;
}

.mb-plugin-head {
  display: flex;
  align-items: baseline;
  gap: 0.5rem;
  flex-wrap: wrap;
}

.mb-plugin-name {
  font-weight: 500;
}

.mb-plugin-desc {
  margin: 0.25rem 0;
  font-size: 0.85rem;
  color: var(--muted, #555);
}

.mb-items {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.35rem;
  margin-top: 0.35rem;
}

.mb-items-label {
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.03em;
  color: var(--muted, #999);
}

.mb-chip {
  font-size: 0.78rem;
  padding: 0.1rem 0.45rem;
  border-radius: 999px;
  background: var(--chip-bg, #eef);
}

.mb-chip-agent {
  background: var(--chip-agent-bg, #efe);
}
</style>
