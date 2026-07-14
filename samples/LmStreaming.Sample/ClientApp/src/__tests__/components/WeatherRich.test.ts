import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import WeatherRich from '@/components/tools/WeatherRich.vue';
import { deriveToolPillState } from '@/utils/toolPillState';
import type { ToolPillView } from '@/utils/toolTypes';
import type { ToolCall } from '@/types';
import weatherFx from '../fixtures/persisted/weather.doubleenc.json';

function mountRich(view: ToolPillView, toolCall: ToolCall) {
  return mount(WeatherRich, { props: { view, toolCall } });
}

/** Synthetic success view whose resultText is the given weather object as JSON. */
function weatherView(obj: Record<string, unknown>): ToolPillView {
  return deriveToolPillState({ result: JSON.stringify(obj), hasResult: true });
}

const anyToolCall: ToolCall = { tool_call_id: 't', function_name: 'get_weather', function_args: '{}' };

describe('WeatherRich — real double-encoded fixture (New York, 74F, Sunny)', () => {
  const view = deriveToolPillState({
    functionArgs: weatherFx.functionArgs,
    result: weatherFx.result,
    hasResult: true,
    isErrorFlag: weatherFx.isError,
  });
  const toolCall: ToolCall = {
    tool_call_id: 't',
    function_name: weatherFx.toolName,
    function_args: weatherFx.functionArgs,
  };

  it('renders the .weather-rich card from the unwrapped double-encoded payload', () => {
    const w = mountRich(view, toolCall);
    expect(w.find('.weather-rich').exists()).toBe(true);
    expect(w.find('.weather-card').exists()).toBe(true);
  });

  it('shows the fixture location New York', () => {
    const w = mountRich(view, toolCall);
    expect(w.get('.weather-loc').text()).toBe('New York');
  });

  it('shows 74°F via formatTemperature', () => {
    const w = mountRich(view, toolCall);
    expect(w.get('.weather-temp').text()).toBe('74°F');
  });

  it('shows the Sunny condition emoji ☀️', () => {
    const w = mountRich(view, toolCall);
    expect(w.get('.weather-emoji').text()).toBe('☀️');
  });
});

describe('WeatherRich — stats', () => {
  it('renders humidity when present', () => {
    const view = weatherView({
      location: 'Miami',
      temperature: 85,
      temperatureUnit: 'F',
      condition: 'Humid',
      humidity: 95,
    });
    const w = mountRich(view, anyToolCall);
    const humidity = w.get('.weather-humidity');
    expect(humidity.text()).toContain('95');
  });

  it('omits humidity/wind rows when those fields are absent', () => {
    const view = weatherView({
      location: 'Reno',
      temperature: 70,
      temperatureUnit: 'F',
      condition: 'Clear',
    });
    const w = mountRich(view, anyToolCall);
    expect(w.find('.weather-humidity').exists()).toBe(false);
    expect(w.find('.weather-wind').exists()).toBe(false);
  });
});

describe('WeatherRich — loading (no result yet)', () => {
  it('shows "<location> · Loading…" from parsedArgs when there is no result', () => {
    const view = deriveToolPillState({
      functionArgs: '{"location":"Paris"}',
      result: null,
      hasResult: false,
    });
    const w = mountRich(view, anyToolCall);
    expect(w.find('.weather-card').exists()).toBe(false);
    const loading = w.get('.weather-loading');
    expect(loading.text()).toContain('Paris');
    expect(loading.text()).toContain('Loading');
  });

  it('renders nothing meaningful when neither result nor location is present', () => {
    const view = deriveToolPillState({ functionArgs: '{}', result: null, hasResult: false });
    const w = mountRich(view, anyToolCall);
    expect(w.find('.weather-card').exists()).toBe(false);
    expect(w.find('.weather-loading').exists()).toBe(false);
    // root still exists (presentational, empty)
    expect(w.find('.weather-rich').exists()).toBe(true);
  });
});

describe('WeatherRich — condition emoji mapping', () => {
  it('maps different conditions to different emoji (Rainy → 🌧️, Sunny → ☀️)', () => {
    const rainy = mountRich(
      weatherView({ location: 'Seattle', temperature: 55, temperatureUnit: 'F', condition: 'Rainy' }),
      anyToolCall
    );
    const sunny = mountRich(
      weatherView({ location: 'Phoenix', temperature: 100, temperatureUnit: 'F', condition: 'Sunny' }),
      anyToolCall
    );
    expect(rainy.get('.weather-emoji').text()).toBe('🌧️');
    expect(sunny.get('.weather-emoji').text()).toBe('☀️');
    expect(rainy.get('.weather-emoji').text()).not.toBe(sunny.get('.weather-emoji').text());
  });
});
