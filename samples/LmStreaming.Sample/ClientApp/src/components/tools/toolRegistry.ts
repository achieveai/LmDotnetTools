import type { Component } from 'vue';
import ToolCallPill from '../ToolCallPill.vue';

// Import specialized tool components
import CalculatorToolPill from './CalculatorToolPill.vue';
import WeatherToolPill from './WeatherToolPill.vue';

/**
 * Registry mapping function names to specialized tool components.
 * If a function name is not found, the default ToolCallPill is used.
 */
const toolComponents: Record<string, Component> = {
  calculator: CalculatorToolPill,
  calculate: CalculatorToolPill,
  get_weather: WeatherToolPill,
  weather: WeatherToolPill,
};

/**
 * Gets the appropriate tool component for a given function name.
 * Returns the default ToolCallPill if no specialized component exists.
 */
export function getToolComponent(functionName: string | null | undefined): Component {
  if (!functionName) {
    return ToolCallPill;
  }

  const normalizedName = functionName.toLowerCase();
  return toolComponents[normalizedName] || ToolCallPill;
}

/**
 * Registers a custom tool component for a function name.
 */
export function registerToolComponent(functionName: string, component: Component): void {
  toolComponents[functionName.toLowerCase()] = component;
}

/**
 * Checks if a specialized component exists for a function name.
 */
export function hasSpecializedComponent(functionName: string | null | undefined): boolean {
  if (!functionName) {
    return false;
  }
  return functionName.toLowerCase() in toolComponents;
}
