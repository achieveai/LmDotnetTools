/**
 * Weather data structure parsed from tool results
 */
export interface WeatherData {
  location: string;
  temperature: number;
  temperatureUnit: string;
  condition: string;
  humidity?: number;
  windSpeed?: number;
  windUnit?: string;
}

/**
 * Parse weather data from a tool result JSON string
 */
export function parseWeatherData(resultJson: string): WeatherData | null {
  function parseJsonIfPossible(value: unknown): unknown {
    if (typeof value !== 'string') return value;
    const trimmed = value.trim();
    if (!trimmed) return null;
    try {
      return JSON.parse(trimmed);
    } catch {
      return value;
    }
  }

  function findWeatherObject(value: unknown, depth = 0): Record<string, unknown> | null {
    if (depth > 6 || value == null) return null;

    const parsedValue = parseJsonIfPossible(value);

    if (Array.isArray(parsedValue)) {
      for (const entry of parsedValue) {
        const found = findWeatherObject(entry, depth + 1);
        if (found) return found;
      }
      return null;
    }

    if (typeof parsedValue === 'string') {
      // Continue unwrapping double-encoded JSON strings.
      if (parsedValue === value) {
        return null;
      }
      return findWeatherObject(parsedValue, depth + 1);
    }

    if (typeof parsedValue !== 'object') {
      return null;
    }

    const obj = parsedValue as Record<string, unknown>;
    if (
      typeof obj.location === 'string'
      && typeof obj.temperature === 'number'
      && typeof obj.condition === 'string'
    ) {
      return obj;
    }

    // MCP-style shape: {"content":[{"type":"text","text":"\"{...}\""}], ...}
    if (Array.isArray(obj.content)) {
      const fromContent = findWeatherObject(obj.content, depth + 1);
      if (fromContent) return fromContent;
    }

    if (obj.structured_content != null) {
      const fromStructured = findWeatherObject(obj.structured_content, depth + 1);
      if (fromStructured) return fromStructured;
    }

    if (obj.text != null) {
      const fromText = findWeatherObject(obj.text, depth + 1);
      if (fromText) return fromText;
    }

    return null;
  }

  try {
    const parsed = findWeatherObject(resultJson);
    if (!parsed) {
      return null;
    }

    return {
      location: String(parsed.location),
      temperature: parsed.temperature as number,
      temperatureUnit: typeof parsed.temperatureUnit === 'string' ? parsed.temperatureUnit : 'F',
      condition: String(parsed.condition),
      humidity: typeof parsed.humidity === 'number' ? parsed.humidity : undefined,
      windSpeed: typeof parsed.windSpeed === 'number' ? parsed.windSpeed : undefined,
      windUnit: typeof parsed.windUnit === 'string' ? parsed.windUnit : undefined,
    };
  } catch {
    return null;
  }
}

/**
 * Check if a tool call is a weather-related function
 */
export function isWeatherTool(functionName: string | null | undefined): boolean {
  if (!functionName) return false;
  const weatherKeywords = ['weather', 'forecast', 'temperature', 'climate'];
  const lowerName = functionName.toLowerCase();
  return weatherKeywords.some(keyword => lowerName.includes(keyword));
}

/**
 * Get weather emoji based on condition
 */
export function getWeatherEmoji(condition: string): string {
  const lowerCondition = condition.toLowerCase();
  
  // Check partly conditions before general cloud check
  if (lowerCondition.includes('partly')) return '⛅';
  if (lowerCondition.includes('sun') || lowerCondition.includes('clear')) return '☀️';
  if (lowerCondition.includes('rain') || lowerCondition.includes('shower')) return '🌧️';
  if (lowerCondition.includes('storm') || lowerCondition.includes('thunder')) return '⛈️';
  if (lowerCondition.includes('snow')) return '❄️';
  if (lowerCondition.includes('fog') || lowerCondition.includes('mist')) return '🌫️';
  if (lowerCondition.includes('wind')) return '💨';
  if (lowerCondition.includes('cloud') || lowerCondition.includes('overcast')) return '☁️';
  
  return '🌡️'; // Default thermometer
}

/**
 * Format temperature display
 */
export function formatTemperature(temp: number, unit: string): string {
  return `${Math.round(temp)}°${unit}`;
}

/**
 * Get rain forecast emoji/text
 */
export function getRainForecast(condition: string, humidity?: number): string {
  const lowerCondition = condition.toLowerCase();
  
  if (lowerCondition.includes('rain') || lowerCondition.includes('shower')) {
    return '💧 Rainy';
  }
  if (lowerCondition.includes('storm') || lowerCondition.includes('thunder')) {
    return '⛈️ Stormy';
  }
  if (humidity && humidity > 80) {
    return '💧 High humidity';
  }
  if (lowerCondition.includes('cloud') || lowerCondition.includes('overcast')) {
    return '☁️ Cloudy';
  }
  if (lowerCondition.includes('clear') || lowerCondition.includes('sun')) {
    return '☀️ Clear';
  }
  
  return condition;
}
