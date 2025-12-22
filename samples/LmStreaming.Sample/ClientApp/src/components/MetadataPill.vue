<script setup lang="ts">
import { ref, inject, watch, nextTick } from 'vue';
import type { ReasoningMessage, ToolsCallMessage, ToolCallResultMessage, ToolCall } from '@/types';
import { isReasoningMessage, isToolsCallMessage } from '@/types';
import { truncateText, parseWeatherData, isWeatherTool, getWeatherEmoji, formatTemperature, getRainForecast, type WeatherData } from '@/utils';

const props = defineProps<{
  items: Array<ReasoningMessage | ToolsCallMessage>;
}>();

// Inject getResultForToolCall from parent
const getResultForToolCall = inject<(toolCallId: string | null | undefined) => ToolCallResultMessage | null>(
  'getResultForToolCall',
  () => null
);

// Track which items are expanded
const expandedItems = ref<Set<number>>(new Set());

// Track if the entire pill is expanded
const isPillExpanded = ref(false);

// Reference to the scrollable container
const pillItemsContainer = ref<HTMLElement | null>(null);

// Auto-scroll to bottom when new items are added (only when collapsed)
watch(() => props.items.length, async () => {
  if (!isPillExpanded.value) {
    await nextTick();
    if (pillItemsContainer.value) {
      pillItemsContainer.value.scrollTop = pillItemsContainer.value.scrollHeight;
    }
  }
});

/**
 * Toggle expansion of the entire pill
 */
function togglePillExpansion(event: Event) {
  event.stopPropagation();
  isPillExpanded.value = !isPillExpanded.value;
}

/**
 * Toggle expansion of an item
 */
function toggleExpand(index: number) {
  if (expandedItems.value.has(index)) {
    expandedItems.value.delete(index);
  } else {
    expandedItems.value.add(index);
  }
}

/**
 * Get summary text for a reasoning message
 */
function getReasoningSummary(item: ReasoningMessage): string {
  return truncateText(item.reasoning, 60);
}

/**
 * Get summary for a tool call
 */
function getToolCallSummary(toolCall: ToolCall): string {
  const name = toolCall.function_name || 'unknown';
  let argsSummary = '';
  
  if (toolCall.function_args) {
    try {
      const args = JSON.parse(toolCall.function_args);
      const keys = Object.keys(args);
      if (keys.length > 0) {
        const firstKey = keys[0];
        const firstValue = args[firstKey];
        argsSummary = `${firstKey}: ${JSON.stringify(firstValue)}`;
        if (keys.length > 1) {
          argsSummary += `, +${keys.length - 1} more`;
        }
      }
    } catch {
      argsSummary = truncateText(toolCall.function_args, 40);
    }
  }
  
  return argsSummary ? `${name}(${argsSummary})` : name;
}

/**
 * Get icon for message type or tool-specific icon
 */
function getIcon(item: ReasoningMessage | ToolsCallMessage): string {
  if (isReasoningMessage(item)) {
    return '💭'; // Thinking emoji
  }
  
  // For tool calls, try to get tool-specific icon
  if (isToolsCallMessage(item) && item.tool_calls.length === 1) {
    const toolCall = item.tool_calls[0];
    if (isWeatherTool(toolCall.function_name)) {
      // For weather tools, try to get weather-specific emoji
      const weatherData = getWeatherData(toolCall);
      if (weatherData) {
        return getWeatherEmoji(weatherData.condition);
      }
      // Default weather icon if no result yet
      return '🌤️';
    }
  }
  
  return '🔧'; // Generic tool emoji
}

/**
 * Check if a tool call has a result
 */
function hasResult(toolCall: ToolCall): boolean {
  return getResult(toolCall) !== null;
}

/**
 * Get result for a tool call
 */
function getResult(toolCall: ToolCall): ToolCallResultMessage | null {
  return getResultForToolCall(toolCall.tool_call_id);
}

/**
 * Format tool result for display
 */
function formatResult(result: ToolCallResultMessage): string {
  try {
    const parsed = JSON.parse(result.result);
    return JSON.stringify(parsed, null, 2);
  } catch {
    return result.result;
  }
}

/**
 * Get weather data for a tool call if it's a weather tool
 */
function getWeatherData(toolCall: ToolCall): WeatherData | null {
  if (!isWeatherTool(toolCall.function_name)) {
    return null;
  }
  
  const result = getResult(toolCall);
  if (!result) {
    return null;
  }
  
  return parseWeatherData(result.result);
}

/**
 * Get location from weather tool call arguments (for immediate display)
 */
function getWeatherLocation(toolCall: ToolCall): string | null {
  if (!isWeatherTool(toolCall.function_name)) {
    return null;
  }
  
  try {
    const args = JSON.parse(toolCall.function_args || '{}');
    return args.location || null;
  } catch {
    return null;
  }
}
</script>

<template>
  <div class="metadata-pill">
    <!-- Pill header with expand/collapse button -->
    <div v-if="props.items.length > 3" class="pill-header" @click="togglePillExpansion">
      <span class="pill-expand-icon">{{ isPillExpanded ? '▼' : '▶' }}</span>
      <span class="pill-header-text">
        {{ isPillExpanded ? 'Collapse' : `Show all ${props.items.length} items` }}
      </span>
    </div>
    
    <div 
      class="pill-items" 
      :class="{ 'expanded': isPillExpanded }"
      ref="pillItemsContainer"
    >
      <template v-for="(item, index) in props.items" :key="index">
        <!-- Skip encrypted-only reasoning -->
        <template v-if="!(isReasoningMessage(item) && item.visibility === 'Encrypted' && !item.reasoning)">
          <div 
            class="pill-item" 
            :class="{ expanded: expandedItems.has(index) }"
            @click="toggleExpand(index)"
          >
            <div class="item-header">
              <span 
                class="item-icon" 
                :class="{ 'pulsing': isToolsCallMessage(item) && item.tool_calls.length === 1 && !hasResult(item.tool_calls[0]) }"
              >
                {{ getIcon(item) }}
              </span>
              
              <!-- Reasoning item -->
              <template v-if="isReasoningMessage(item)">
                <span class="item-label">Thinking:</span>
                <span class="item-summary">{{ getReasoningSummary(item) }}</span>
              </template>
              
              <!-- Tool calls item -->
              <template v-else-if="isToolsCallMessage(item)">
                <template v-if="item.tool_calls.length === 1">
                  <!-- Special weather display -->
                  <template v-if="getWeatherLocation(item.tool_calls[0])">
                    <span class="item-label weather-label">
                      {{ getWeatherLocation(item.tool_calls[0]) }}
                    </span>
                    <span class="weather-summary">
                      <template v-if="getWeatherData(item.tool_calls[0])">
                        <span class="weather-temp">{{ formatTemperature(getWeatherData(item.tool_calls[0])!.temperature, getWeatherData(item.tool_calls[0])!.temperatureUnit) }}</span>
                        <span class="weather-condition">{{ getRainForecast(getWeatherData(item.tool_calls[0])!.condition, getWeatherData(item.tool_calls[0])!.humidity) }}</span>
                      </template>
                      <span v-else class="weather-loading">Loading...</span>
                    </span>
                  </template>
                  <!-- Regular tool display -->
                  <template v-else>
                    <span class="item-label">{{ item.tool_calls[0].function_name || 'Tool' }}:</span>
                    <span class="item-summary">{{ getToolCallSummary(item.tool_calls[0]) }}</span>
                  </template>
                </template>
                <template v-else>
                  <span class="item-label">Tools:</span>
                  <span class="item-summary">{{ item.tool_calls.length }} calls</span>
                </template>
              </template>
              
              <span class="expand-icon">{{ expandedItems.has(index) ? '▼' : '▶' }}</span>
            </div>
            
            <!-- Expanded content -->
            <div v-if="expandedItems.has(index)" class="item-content">
              <!-- Reasoning content -->
              <template v-if="isReasoningMessage(item)">
                <pre class="reasoning-text">{{ item.reasoning }}</pre>
              </template>
              
              <!-- Tool calls content -->
              <template v-else-if="isToolsCallMessage(item)">
                <div v-for="(toolCall, tcIndex) in item.tool_calls" :key="tcIndex" class="tool-call">
                  <div class="tool-call-header">
                    <strong>{{ toolCall.function_name || 'unknown' }}</strong>
                  </div>
                  <div class="tool-call-args">
                    <strong>Arguments:</strong>
                    <pre>{{ toolCall.function_args }}</pre>
                  </div>
                  <div v-if="getResult(toolCall)" class="tool-call-result">
                    <strong>Result:</strong>
                    <pre>{{ formatResult(getResult(toolCall)!) }}</pre>
                  </div>
                </div>
              </template>
            </div>
          </div>
        </template>
      </template>
    </div>
  </div>
</template>

<style scoped>
.metadata-pill {
  background: #f0f0f0;
  border-radius: 12px;
  padding: 8px;
  border: 1px solid #e0e0e0;
  margin-bottom: 8px;
}

.pill-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 8px;
  margin-bottom: 8px;
  background: #e8e8e8;
  border-radius: 8px;
  cursor: pointer;
  user-select: none;
  transition: background 0.2s ease;
}

.pill-header:hover {
  background: #d8d8d8;
}

.pill-expand-icon {
  font-size: 10px;
  color: #666;
}

.pill-header-text {
  font-size: 12px;
  font-weight: 600;
  color: #555;
}

.pill-items {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-height: 150px; /* Height for ~3 collapsed items (42px each + gaps) */
  overflow-y: auto;
  overflow-x: hidden;
  transition: max-height 0.3s ease;
}

.pill-items.expanded {
  max-height: none;
  overflow-y: visible;
}

/* Scrollbar styling for pill items */
.pill-items::-webkit-scrollbar {
  width: 6px;
}

.pill-items::-webkit-scrollbar-track {
  background: #f0f0f0;
  border-radius: 3px;
}

.pill-items::-webkit-scrollbar-thumb {
  background: #c0c0c0;
  border-radius: 3px;
}

.pill-items::-webkit-scrollbar-thumb:hover {
  background: #a0a0a0;
}

.pill-item {
  background: #fff;
  border-radius: 8px;
  padding: 8px 12px;
  cursor: pointer;
  transition: all 0.2s ease;
  border: 1px solid transparent;
}

.pill-item:hover {
  background: #f8f8f8;
  border-color: #d0d0d0;
}

.pill-item.expanded {
  background: #fff;
}

.item-header {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 14px;
  user-select: none;
}

.item-icon {
  font-size: 16px;
  flex-shrink: 0;
}

.item-icon.pulsing {
  animation: pulse 2s ease-in-out infinite;
}

@keyframes pulse {
  0%, 100% {
    opacity: 1;
    transform: scale(1);
  }
  50% {
    opacity: 0.6;
    transform: scale(0.95);
  }
}

.item-label {
  font-weight: 600;
  color: #333;
  flex-shrink: 0;
}

.item-summary {
  color: #666;
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.weather-label {
  font-weight: 600;
  color: #1976d2;
  font-size: 15px;
}

.weather-summary {
  display: flex;
  align-items: center;
  gap: 12px;
  flex: 1;
}

.weather-temp {
  font-weight: 600;
  color: #333;
  font-size: 16px;
}

.weather-condition {
  color: #666;
  font-size: 14px;
}

.weather-loading {
  color: #999;
  font-size: 14px;
  font-style: italic;
  animation: fadeInOut 1.5s ease-in-out infinite;
}

@keyframes fadeInOut {
  0%, 100% {
    opacity: 0.5;
  }
  50% {
    opacity: 1;
  }
}

.expand-icon {
  color: #999;
  font-size: 10px;
  flex-shrink: 0;
  margin-left: auto;
}

.item-content {
  margin-top: 12px;
  padding-top: 12px;
  border-top: 1px solid #e0e0e0;
}

.reasoning-text {
  margin: 0;
  padding: 12px;
  background: #f8f9fa;
  border-radius: 6px;
  font-size: 13px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-wrap: break-word;
  overflow-x: auto;
}

.tool-call {
  margin-bottom: 12px;
}

.tool-call:last-child {
  margin-bottom: 0;
}

.tool-call-header {
  margin-bottom: 8px;
  color: #1976d2;
}

.tool-call-args,
.tool-call-result {
  margin-bottom: 8px;
}

.tool-call-args strong,
.tool-call-result strong {
  display: block;
  margin-bottom: 4px;
  font-size: 12px;
  color: #666;
}

.tool-call-args pre,
.tool-call-result pre {
  margin: 0;
  padding: 8px;
  background: #f8f9fa;
  border-radius: 4px;
  font-size: 12px;
  line-height: 1.4;
  overflow-x: auto;
}
</style>

