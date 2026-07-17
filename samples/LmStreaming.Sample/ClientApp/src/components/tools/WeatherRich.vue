<script setup lang="ts">
import { computed } from 'vue';
import type { ToolPillView } from '@/utils/toolTypes';
import type { ToolCall } from '@/types';
import { parseWeatherData, getWeatherEmoji, formatTemperature, getRainForecast } from '@/utils';

const props = defineProps<{ view: ToolPillView; toolCall: ToolCall }>();

/** Parsed weather body from the (possibly double-encoded) unwrapped result text. */
const data = computed(() => parseWeatherData(props.view.resultText));

/** Requested location for the pre-result loading state (from parsed args). */
const loadingLocation = computed(() => {
  const loc = props.view.parsedArgs?.location;
  return typeof loc === 'string' ? loc : '';
});
</script>

<template>
  <div class="weather-rich tool-rich">
    <div v-if="data" class="weather-card">
      <div class="weather-head">
        <span class="weather-emoji">{{ getWeatherEmoji(data.condition) }}</span>
        <span class="weather-loc">{{ data.location }}</span>
      </div>
      <div class="weather-temp">{{ formatTemperature(data.temperature, data.temperatureUnit) }}</div>
      <div class="weather-cond">{{ getRainForecast(data.condition, data.humidity) }}</div>
      <div class="weather-stats">
        <div class="weather-humidity" v-if="data.humidity != null">Humidity: {{ data.humidity }}%</div>
        <div class="weather-wind" v-if="data.windSpeed != null">Wind: {{ data.windSpeed }} {{ data.windUnit }}</div>
      </div>
    </div>
    <div v-else-if="loadingLocation" class="weather-loading">{{ loadingLocation }} · Loading…</div>
  </div>
</template>

<style scoped>
.weather-rich {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.weather-card {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 8px 10px;
  border-radius: 8px;
  background: #e0f2fe;
  color: #0369a1;
  border: 1px solid #7dd3fc;
}

.weather-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-weight: 600;
}

.weather-emoji {
  font-size: 1.1rem;
  line-height: 1;
}

.weather-temp {
  font-size: 1.35rem;
  font-weight: 700;
}

.weather-cond {
  font-size: 0.9rem;
}

.weather-stats {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
  font-size: 0.8rem;
  opacity: 0.85;
}

.weather-loading {
  font-size: 0.85rem;
  opacity: 0.7;
  font-style: italic;
}
</style>
