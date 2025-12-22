import { describe, it, expect, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import MetadataPill from '@/components/MetadataPill.vue';
import type { ToolsCallMessage, ReasoningMessage, ToolCallResultMessage } from '@/types';
import { MessageType } from '@/types';

describe('MetadataPill', () => {
  describe('Weather Tool Display', () => {
    it('should display weather information in collapsed state', () => {
      const weatherResult: ToolCallResultMessage = {
        $type: MessageType.ToolCallResult,
        tool_call_id: 'call_123',
        result: JSON.stringify({
          location: 'Seattle',
          temperature: 64,
          temperatureUnit: 'F',
          condition: 'Rainy',
          humidity: 43,
          windSpeed: 10,
          windUnit: 'mph',
        }),
        role: 'tool',
      };

      const weatherToolCall: ToolsCallMessage = {
        $type: MessageType.ToolsCall,
        role: 'assistant',
        tool_calls: [
          {
            tool_call_id: 'call_123',
            function_name: 'get_weather',
            function_args: JSON.stringify({ location: 'Seattle' }),
          },
        ],
      };

      const mockGetResult = vi.fn((toolCallId: string | null | undefined) => {
        if (toolCallId === 'call_123') {
          return weatherResult;
        }
        return null;
      });

      const wrapper = mount(MetadataPill, {
        props: {
          items: [weatherToolCall],
        },
        global: {
          provide: {
            getResultForToolCall: mockGetResult,
          },
        },
      });

      const text = wrapper.text();
      
      // Should show weather emoji
      expect(text).toContain('🌧️');
      
      // Should show location
      expect(text).toContain('Seattle');
      
      // Should show formatted temperature
      expect(text).toContain('64°F');
      
      // Should show rain forecast
      expect(text).toContain('Rainy');
    });

    it('should handle double-encoded weather JSON from backend', () => {
      const backendWeatherResult: ToolCallResultMessage = {
        $type: MessageType.ToolCallResult,
        tool_call_id: 'call_456',
        result: `"{\r\n  \"location\": \"New York\",\r\n  \"temperature\": 75,\r\n  \"temperatureUnit\": \"F\",\r\n  \"condition\": \"Sunny\",\r\n  \"humidity\": 50\r\n}"`,
        role: 'tool',
      };

      const weatherToolCall: ToolsCallMessage = {
        $type: MessageType.ToolsCall,
        role: 'assistant',
        tool_calls: [
          {
            tool_call_id: 'call_456',
            function_name: 'fetch_weather',
            function_args: JSON.stringify({ location: 'New York' }),
          },
        ],
      };

      const mockGetResult = vi.fn((toolCallId: string | null | undefined) => {
        if (toolCallId === 'call_456') {
          return backendWeatherResult;
        }
        return null;
      });

      const wrapper = mount(MetadataPill, {
        props: {
          items: [weatherToolCall],
        },
        global: {
          provide: {
            getResultForToolCall: mockGetResult,
          },
        },
      });

      const text = wrapper.text();
      
      expect(text).toContain('☀️');
      expect(text).toContain('New York');
      expect(text).toContain('75°F');
      expect(text).toContain('Clear');
    });

    it('should display different weather conditions correctly', () => {
      const testCases = [
        {
          condition: 'Sunny',
          expectedEmoji: '☀️',
          expectedForecast: '☀️ Clear',
        },
        {
          condition: 'Cloudy',
          expectedEmoji: '☁️',
          expectedForecast: '☁️ Cloudy',
        },
        {
          condition: 'Snow',
          expectedEmoji: '❄️',
          expectedForecast: 'Snow',
        },
        {
          condition: 'Thunderstorm',
          expectedEmoji: '⛈️',
          expectedForecast: '⛈️ Stormy',
        },
      ];

      testCases.forEach((testCase, index) => {
        const result: ToolCallResultMessage = {
          $type: MessageType.ToolCallResult,
          tool_call_id: `call_${index}`,
          result: JSON.stringify({
            location: 'TestCity',
            temperature: 70,
            temperatureUnit: 'F',
            condition: testCase.condition,
          }),
          role: 'tool',
        };

        const toolCall: ToolsCallMessage = {
          $type: MessageType.ToolsCall,
          role: 'assistant',
          tool_calls: [
            {
              tool_call_id: `call_${index}`,
              function_name: 'get_weather',
              function_args: JSON.stringify({ location: 'TestCity' }),
            },
          ],
        };

        const mockGetResult = vi.fn((toolCallId: string | null | undefined) => {
          if (toolCallId === `call_${index}`) {
            return result;
          }
          return null;
        });

        const wrapper = mount(MetadataPill, {
          props: {
            items: [toolCall],
          },
          global: {
            provide: {
              getResultForToolCall: mockGetResult,
            },
          },
        });

        const text = wrapper.text();
        expect(text, `Failed for condition: ${testCase.condition}`).toContain(testCase.expectedEmoji);
        expect(text, `Failed for condition: ${testCase.condition}`).toContain(testCase.expectedForecast);
      });
    });

    it('should handle high humidity conditions', () => {
      const weatherResult: ToolCallResultMessage = {
        $type: MessageType.ToolCallResult,
        tool_call_id: 'call_humid',
        result: JSON.stringify({
          location: 'Miami',
          temperature: 85,
          temperatureUnit: 'F',
          condition: 'Humid',
          humidity: 95,
        }),
        role: 'tool',
      };

      const weatherToolCall: ToolsCallMessage = {
        $type: MessageType.ToolsCall,
        role: 'assistant',
        tool_calls: [
          {
            tool_call_id: 'call_humid',
            function_name: 'get_weather',
            function_args: JSON.stringify({ location: 'Miami' }),
          },
        ],
      };

      const mockGetResult = vi.fn((toolCallId: string | null | undefined) => {
        if (toolCallId === 'call_humid') {
          return weatherResult;
        }
        return null;
      });

      const wrapper = mount(MetadataPill, {
        props: {
          items: [weatherToolCall],
        },
        global: {
          provide: {
            getResultForToolCall: mockGetResult,
          },
        },
      });

      const text = wrapper.text();
      expect(text).toContain('💧 High humidity');
    });

    it('should fall back to regular display for non-weather tools', () => {
      const regularToolCall: ToolsCallMessage = {
        $type: MessageType.ToolsCall,
        role: 'assistant',
        tool_calls: [
          {
            tool_call_id: 'call_calc',
            function_name: 'calculate_sum',
            function_args: JSON.stringify({ a: 5, b: 10 }),
          },
        ],
      };

      const mockGetResult = vi.fn(() => null);

      const wrapper = mount(MetadataPill, {
        props: {
          items: [regularToolCall],
        },
        global: {
          provide: {
            getResultForToolCall: mockGetResult,
          },
        },
      });

      const text = wrapper.text();
      
      // Should use regular tool display
      expect(text).toContain('calculate_sum');
      expect(text).toContain('a: 5');
      
      // Should NOT show weather-specific elements
      expect(text).not.toContain('☀️');
      expect(text).not.toContain('🌧️');
      expect(text).not.toContain('°F');
    });

    it('should show regular display when weather tool has no result yet', () => {
      const weatherToolCall: ToolsCallMessage = {
        $type: MessageType.ToolsCall,
        role: 'assistant',
        tool_calls: [
          {
            tool_call_id: 'call_pending',
            function_name: 'get_weather',
            function_args: JSON.stringify({ location: 'Paris' }),
          },
        ],
      };

      const mockGetResult = vi.fn(() => null); // No result yet

      const wrapper = mount(MetadataPill, {
        props: {
          items: [weatherToolCall],
        },
        global: {
          provide: {
            getResultForToolCall: mockGetResult,
          },
        },
      });

      const text = wrapper.text();
      
      // Should show function name and args
      expect(text).toContain('get_weather');
      expect(text).toContain('location');
      
      // Should NOT show weather data
      expect(text).not.toContain('°F');
    });

    it('should expand to show full JSON when clicked', async () => {
      const weatherResult: ToolCallResultMessage = {
        $type: MessageType.ToolCallResult,
        tool_call_id: 'call_expand',
        result: JSON.stringify({
          location: 'London',
          temperature: 15,
          temperatureUnit: 'C',
          condition: 'Foggy',
          humidity: 80,
          windSpeed: 5,
          windUnit: 'mph',
        }),
        role: 'tool',
      };

      const weatherToolCall: ToolsCallMessage = {
        $type: MessageType.ToolsCall,
        role: 'assistant',
        tool_calls: [
          {
            tool_call_id: 'call_expand',
            function_name: 'get_weather',
            function_args: JSON.stringify({ location: 'London' }),
          },
        ],
      };

      const mockGetResult = vi.fn((toolCallId: string | null | undefined) => {
        if (toolCallId === 'call_expand') {
          return weatherResult;
        }
        return null;
      });

      const wrapper = mount(MetadataPill, {
        props: {
          items: [weatherToolCall],
        },
        global: {
          provide: {
            getResultForToolCall: mockGetResult,
          },
        },
      });

      // Initially collapsed - should show weather summary
      expect(wrapper.text()).toContain('London');
      expect(wrapper.text()).toContain('15°C');

      // Click to expand
      await wrapper.find('.pill-item').trigger('click');

      // Should now show expanded content with full JSON
      const expandedText = wrapper.text();
      expect(expandedText).toContain('Arguments:');
      expect(expandedText).toContain('Result:');
      expect(expandedText).toContain('"humidity": 80');
      expect(expandedText).toContain('"windSpeed": 5');
    });
  });

  describe('Non-Weather Tool Display', () => {
    it('should display reasoning messages', () => {
      const reasoningMessage: ReasoningMessage = {
        $type: MessageType.Reasoning,
        role: 'assistant',
        reasoning: 'I need to fetch the weather for Seattle first',
      };

      const wrapper = mount(MetadataPill, {
        props: {
          items: [reasoningMessage],
        },
        global: {
          provide: {
            getResultForToolCall: () => null,
          },
        },
      });

      const text = wrapper.text();
      expect(text).toContain('💭');
      expect(text).toContain('Thinking:');
      expect(text).toContain('I need to fetch the weather');
    });

    it('should display multiple tool calls', () => {
      const multipleToolCalls: ToolsCallMessage = {
        $type: MessageType.ToolsCall,
        role: 'assistant',
        tool_calls: [
          {
            tool_call_id: 'call_1',
            function_name: 'get_weather',
            function_args: JSON.stringify({ location: 'Seattle' }),
          },
          {
            tool_call_id: 'call_2',
            function_name: 'get_weather',
            function_args: JSON.stringify({ location: 'New York' }),
          },
        ],
      };

      const wrapper = mount(MetadataPill, {
        props: {
          items: [multipleToolCalls],
        },
        global: {
          provide: {
            getResultForToolCall: () => null,
          },
        },
      });

      const text = wrapper.text();
      expect(text).toContain('Tools:');
      expect(text).toContain('2 calls');
    });

    it('should skip encrypted-only reasoning messages', () => {
      const encryptedReasoning: ReasoningMessage = {
        $type: MessageType.Reasoning,
        role: 'assistant',
        reasoning: '',
        visibility: 'Encrypted',
      };

      const wrapper = mount(MetadataPill, {
        props: {
          items: [encryptedReasoning],
        },
        global: {
          provide: {
            getResultForToolCall: () => null,
          },
        },
      });

      // Should not display anything for encrypted-only messages
      const pillItems = wrapper.findAll('.pill-item');
      expect(pillItems.length).toBe(0);
    });
  });

  describe('Integration with Real Data', () => {
    it('should handle the ResponseSample.md weather scenario', () => {
      // This matches the actual data from ResponseSample.md
      const weatherResult: ToolCallResultMessage = {
        $type: MessageType.ToolCallResult,
        tool_call_id: 'call_nqBX7b43ksVjW68o86ZRRCB6',
        result: `"{\r\n  \"location\": \"Seattle\",\r\n  \"temperature\": 64,\r\n  \"temperatureUnit\": \"F\",\r\n  \"condition\": \"Rainy\",\r\n  \"humidity\": 43,\r\n  \"windSpeed\": 10,\r\n  \"windUnit\": \"mph\"\r\n}"`,
        role: 'tool',
      };

      const weatherToolCall: ToolsCallMessage = {
        $type: MessageType.ToolsCall,
        role: 'assistant',
        tool_calls: [
          {
            tool_call_id: 'call_nqBX7b43ksVjW68o86ZRRCB6',
            function_name: 'get_weather',
            function_args: '{\n  "location": "Seattle"\n}',
          },
        ],
      };

      const mockGetResult = vi.fn((toolCallId: string | null | undefined) => {
        if (toolCallId === 'call_nqBX7b43ksVjW68o86ZRRCB6') {
          return weatherResult;
        }
        return null;
      });

      const wrapper = mount(MetadataPill, {
        props: {
          items: [weatherToolCall],
        },
        global: {
          provide: {
            getResultForToolCall: mockGetResult,
          },
        },
      });

      const text = wrapper.text();
      
      // Verify weather display
      expect(text).toContain('🌧️'); // Rainy emoji
      expect(text).toContain('Seattle');
      expect(text).toContain('64°F');
      expect(text).toContain('💧 Rainy');
    });
  });
});

