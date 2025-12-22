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
  try {
    let data = resultJson;
    
    // Handle double-encoded JSON (common from backend)
    // The backend may send JSON as a quoted string
    if (resultJson.startsWith('"') && resultJson.endsWith('"')) {
      // First parse to unwrap the outer quotes and handle escape sequences
      data = JSON.parse(resultJson);
    }
    
    // Now parse the actual JSON data
    const parsed = JSON.parse(data);
    
    // Validate required fields
    if (!parsed.location || typeof parsed.temperature !== 'number' || !parsed.condition) {
      return null;
    }
    
    return {
      location: parsed.location,
      temperature: parsed.temperature,
      temperatureUnit: parsed.temperatureUnit || 'F',
      condition: parsed.condition,
      humidity: parsed.humidity,
      windSpeed: parsed.windSpeed,
      windUnit: parsed.windUnit,
    };
  } catch (error) {
    // Log error for debugging (optional)
    // console.error('Failed to parse weather data:', error);
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

