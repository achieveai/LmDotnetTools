import { describe, it, expect } from 'vitest';
import {
  parseWeatherData,
  isWeatherTool,
  getWeatherEmoji,
  formatTemperature,
  getRainForecast,
  type WeatherData,
} from '@/utils/weatherParser';

describe('weatherParser', () => {
  describe('parseWeatherData', () => {
    it('should parse valid weather JSON', () => {
      const json = JSON.stringify({
        location: 'Seattle',
        temperature: 64,
        temperatureUnit: 'F',
        condition: 'Rainy',
        humidity: 43,
        windSpeed: 10,
        windUnit: 'mph',
      });

      const result = parseWeatherData(json);
      
      expect(result).not.toBeNull();
      expect(result?.location).toBe('Seattle');
      expect(result?.temperature).toBe(64);
      expect(result?.temperatureUnit).toBe('F');
      expect(result?.condition).toBe('Rainy');
      expect(result?.humidity).toBe(43);
      expect(result?.windSpeed).toBe(10);
      expect(result?.windUnit).toBe('mph');
    });

    it('should parse double-encoded JSON (from backend)', () => {
      const innerJson = JSON.stringify({
        location: 'New York',
        temperature: 75,
        temperatureUnit: 'F',
        condition: 'Sunny',
      });
      const doubleEncoded = JSON.stringify(innerJson);

      const result = parseWeatherData(doubleEncoded);
      
      expect(result).not.toBeNull();
      expect(result?.location).toBe('New York');
      expect(result?.temperature).toBe(75);
      expect(result?.condition).toBe('Sunny');
    });

    it('should handle minimal valid data', () => {
      const json = JSON.stringify({
        location: 'London',
        temperature: 15,
        condition: 'Cloudy',
      });

      const result = parseWeatherData(json);
      
      expect(result).not.toBeNull();
      expect(result?.location).toBe('London');
      expect(result?.temperature).toBe(15);
      expect(result?.temperatureUnit).toBe('F'); // Default
      expect(result?.condition).toBe('Cloudy');
      expect(result?.humidity).toBeUndefined();
    });

    it('should return null for missing required fields', () => {
      const cases = [
        JSON.stringify({ temperature: 70, condition: 'Sunny' }), // Missing location
        JSON.stringify({ location: 'Paris', condition: 'Clear' }), // Missing temperature
        JSON.stringify({ location: 'Berlin', temperature: 20 }), // Missing condition
        JSON.stringify({ location: 'Tokyo' }), // Missing temp and condition
      ];

      cases.forEach(json => {
        expect(parseWeatherData(json)).toBeNull();
      });
    });

    it('should return null for invalid JSON', () => {
      expect(parseWeatherData('not json')).toBeNull();
      expect(parseWeatherData('{invalid}')).toBeNull();
      expect(parseWeatherData('')).toBeNull();
    });

    it('should return null for non-weather data', () => {
      const json = JSON.stringify({
        name: 'John',
        age: 30,
        city: 'Seattle',
      });

      expect(parseWeatherData(json)).toBeNull();
    });
  });

  describe('isWeatherTool', () => {
    it('should detect weather-related function names', () => {
      expect(isWeatherTool('get_weather')).toBe(true);
      expect(isWeatherTool('fetch_weather')).toBe(true);
      expect(isWeatherTool('weather_forecast')).toBe(true);
      expect(isWeatherTool('get_temperature')).toBe(true);
      expect(isWeatherTool('check_climate')).toBe(true);
      expect(isWeatherTool('GetWeather')).toBe(true); // Case insensitive
      expect(isWeatherTool('WEATHER_API')).toBe(true);
    });

    it('should not detect non-weather functions', () => {
      expect(isWeatherTool('calculate_sum')).toBe(false);
      expect(isWeatherTool('get_user')).toBe(false);
      expect(isWeatherTool('fetch_data')).toBe(false);
      expect(isWeatherTool('search_results')).toBe(false);
    });

    it('should handle null/undefined', () => {
      expect(isWeatherTool(null)).toBe(false);
      expect(isWeatherTool(undefined)).toBe(false);
      expect(isWeatherTool('')).toBe(false);
    });
  });

  describe('getWeatherEmoji', () => {
    it('should return sunny emoji for sunny conditions', () => {
      expect(getWeatherEmoji('Sunny')).toBe('☀️');
      expect(getWeatherEmoji('Clear')).toBe('☀️');
      expect(getWeatherEmoji('clear sky')).toBe('☀️');
    });

    it('should return cloud emoji for cloudy conditions', () => {
      expect(getWeatherEmoji('Cloudy')).toBe('☁️');
      expect(getWeatherEmoji('Overcast')).toBe('☁️');
      expect(getWeatherEmoji('clouds')).toBe('☁️');
    });

    it('should return rain emoji for rainy conditions', () => {
      expect(getWeatherEmoji('Rainy')).toBe('🌧️');
      expect(getWeatherEmoji('Rain')).toBe('🌧️');
      expect(getWeatherEmoji('Showers')).toBe('🌧️');
      expect(getWeatherEmoji('light rain')).toBe('🌧️');
    });

    it('should return storm emoji for stormy conditions', () => {
      expect(getWeatherEmoji('Stormy')).toBe('⛈️');
      expect(getWeatherEmoji('Thunderstorm')).toBe('⛈️');
      expect(getWeatherEmoji('thunder')).toBe('⛈️');
    });

    it('should return snow emoji for snowy conditions', () => {
      expect(getWeatherEmoji('Snowy')).toBe('❄️');
      expect(getWeatherEmoji('Snow')).toBe('❄️');
      expect(getWeatherEmoji('snowfall')).toBe('❄️');
    });

    it('should return fog emoji for foggy conditions', () => {
      expect(getWeatherEmoji('Foggy')).toBe('🌫️');
      expect(getWeatherEmoji('Fog')).toBe('🌫️');
      expect(getWeatherEmoji('Mist')).toBe('🌫️');
      expect(getWeatherEmoji('misty')).toBe('🌫️');
    });

    it('should return partly cloudy emoji', () => {
      expect(getWeatherEmoji('Partly Cloudy')).toBe('⛅');
      expect(getWeatherEmoji('partly sunny')).toBe('⛅');
    });

    it('should return wind emoji for windy conditions', () => {
      expect(getWeatherEmoji('Windy')).toBe('💨');
      expect(getWeatherEmoji('wind')).toBe('💨');
    });

    it('should return default thermometer for unknown conditions', () => {
      expect(getWeatherEmoji('Unknown')).toBe('🌡️');
      expect(getWeatherEmoji('Variable')).toBe('🌡️');
      expect(getWeatherEmoji('')).toBe('🌡️');
    });
  });

  describe('formatTemperature', () => {
    it('should format temperature with unit', () => {
      expect(formatTemperature(64, 'F')).toBe('64°F');
      expect(formatTemperature(18, 'C')).toBe('18°C');
      expect(formatTemperature(293, 'K')).toBe('293°K');
    });

    it('should round decimal temperatures', () => {
      expect(formatTemperature(64.7, 'F')).toBe('65°F');
      expect(formatTemperature(18.2, 'C')).toBe('18°C');
      expect(formatTemperature(64.4, 'F')).toBe('64°F');
    });

    it('should handle negative temperatures', () => {
      expect(formatTemperature(-5, 'C')).toBe('-5°C');
      expect(formatTemperature(-10, 'F')).toBe('-10°F');
    });

    it('should handle zero temperature', () => {
      expect(formatTemperature(0, 'C')).toBe('0°C');
    });
  });

  describe('getRainForecast', () => {
    it('should return rainy forecast for rain conditions', () => {
      expect(getRainForecast('Rainy')).toBe('💧 Rainy');
      expect(getRainForecast('Rain')).toBe('💧 Rainy');
      expect(getRainForecast('Showers')).toBe('💧 Rainy');
      expect(getRainForecast('light rain')).toBe('💧 Rainy');
    });

    it('should return stormy forecast for storm conditions', () => {
      expect(getRainForecast('Stormy')).toBe('⛈️ Stormy');
      expect(getRainForecast('Thunderstorm')).toBe('⛈️ Stormy');
      expect(getRainForecast('thunder')).toBe('⛈️ Stormy');
    });

    it('should return high humidity for high humidity', () => {
      expect(getRainForecast('Humid', 85)).toBe('💧 High humidity');
      expect(getRainForecast('Muggy', 90)).toBe('💧 High humidity');
      expect(getRainForecast('Any condition', 81)).toBe('💧 High humidity');
    });

    it('should not return high humidity for moderate humidity', () => {
      const result = getRainForecast('Cloudy', 70);
      expect(result).not.toBe('💧 High humidity');
      expect(result).toBe('☁️ Cloudy');
    });

    it('should return cloudy forecast for cloud conditions', () => {
      expect(getRainForecast('Cloudy')).toBe('☁️ Cloudy');
      expect(getRainForecast('Overcast')).toBe('☁️ Cloudy');
      expect(getRainForecast('clouds')).toBe('☁️ Cloudy');
    });

    it('should return clear forecast for clear conditions', () => {
      expect(getRainForecast('Clear')).toBe('☀️ Clear');
      expect(getRainForecast('Sunny')).toBe('☀️ Clear');
      expect(getRainForecast('clear sky')).toBe('☀️ Clear');
    });

    it('should return condition as-is for unknown conditions', () => {
      expect(getRainForecast('Windy')).toBe('Windy');
      expect(getRainForecast('Variable')).toBe('Variable');
      expect(getRainForecast('Unknown')).toBe('Unknown');
    });
  });

  describe('Real-world weather data', () => {
    it('should parse actual backend response from ResponseSample.md', () => {
      // This is the actual weather result from the ResponseSample.md file
      const backendResult = `"{\r\n  \"location\": \"Seattle\",\r\n  \"temperature\": 64,\r\n  \"temperatureUnit\": \"F\",\r\n  \"condition\": \"Rainy\",\r\n  \"humidity\": 43,\r\n  \"windSpeed\": 10,\r\n  \"windUnit\": \"mph\"\r\n}"`;

      const result = parseWeatherData(backendResult);
      
      expect(result).not.toBeNull();
      expect(result?.location).toBe('Seattle');
      expect(result?.temperature).toBe(64);
      expect(result?.temperatureUnit).toBe('F');
      expect(result?.condition).toBe('Rainy');
      expect(result?.humidity).toBe(43);
      expect(result?.windSpeed).toBe(10);
      expect(result?.windUnit).toBe('mph');
      
      // Verify display formatting
      expect(formatTemperature(result!.temperature, result!.temperatureUnit)).toBe('64°F');
      expect(getWeatherEmoji(result!.condition)).toBe('🌧️');
      expect(getRainForecast(result!.condition, result!.humidity)).toBe('💧 Rainy');
    });

    it('should handle various real-world weather scenarios', () => {
      const scenarios = [
        {
          name: 'Hot and sunny',
          data: { location: 'Phoenix', temperature: 105, temperatureUnit: 'F', condition: 'Sunny' },
          expectedEmoji: '☀️',
          expectedForecast: '☀️ Clear',
        },
        {
          name: 'Cold and snowy',
          data: { location: 'Moscow', temperature: -10, temperatureUnit: 'C', condition: 'Snow' },
          expectedEmoji: '❄️',
          expectedForecast: 'Snow',
        },
        {
          name: 'Stormy with high humidity',
          data: { location: 'Miami', temperature: 85, temperatureUnit: 'F', condition: 'Thunderstorm', humidity: 95 },
          expectedEmoji: '⛈️',
          expectedForecast: '⛈️ Stormy',
        },
        {
          name: 'Foggy morning',
          data: { location: 'San Francisco', temperature: 55, temperatureUnit: 'F', condition: 'Foggy' },
          expectedEmoji: '🌫️',
          expectedForecast: 'Foggy',
        },
      ];

      scenarios.forEach(scenario => {
        const json = JSON.stringify(scenario.data);
        const result = parseWeatherData(json);
        
        expect(result, `Failed to parse: ${scenario.name}`).not.toBeNull();
        expect(getWeatherEmoji(result!.condition), `Wrong emoji for: ${scenario.name}`).toBe(scenario.expectedEmoji);
        expect(getRainForecast(result!.condition, result!.humidity), `Wrong forecast for: ${scenario.name}`).toBe(scenario.expectedForecast);
      });
    });
  });
});

