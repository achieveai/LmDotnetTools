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
      // Backend sends with Unicode escapes
      const backendWeatherResult: ToolCallResultMessage = {
        $type: MessageType.ToolCallResult,
        tool_call_id: 'call_456',
        result: "\u0022{\\r\\n  \\u0022location\\u0022: \\u0022New York\\u0022,\\r\\n  \\u0022temperature\\u0022: 75,\\r\\n  \\u0022temperatureUnit\\u0022: \\u0022F\\u0022,\\r\\n  \\u0022condition\\u0022: \\u0022Sunny\\u0022,\\r\\n  \\u0022humidity\\u0022: 50\\r\\n}\u0022",
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

    it('should show location immediately when weather tool has no result yet', () => {
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
      
      // Should show location immediately from args
      expect(text).toContain('Paris');
      expect(text).toContain('Loading...');
      
      // Should NOT show temperature yet
      expect(text).not.toContain('°F');
      expect(text).not.toContain('°C');
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

  describe('Pill Expansion', () => {
    it('should show expand button when more than 3 items', () => {
      const items: ToolsCallMessage[] = Array.from({ length: 5 }, (_, i) => ({
        $type: MessageType.ToolsCall,
        role: 'assistant',
        tool_calls: [
          {
            tool_call_id: `call_${i}`,
            function_name: 'test_function',
            function_args: JSON.stringify({ index: i }),
          },
        ],
      }));

      const wrapper = mount(MetadataPill, {
        props: {
          items,
        },
        global: {
          provide: {
            getResultForToolCall: () => null,
          },
        },
      });

      // Should show expand button
      expect(wrapper.find('.pill-header').exists()).toBe(true);
      expect(wrapper.text()).toContain('Show all 5 items');
    });

    it('should not show expand button when 3 or fewer items', () => {
      const items: ToolsCallMessage[] = Array.from({ length: 3 }, (_, i) => ({
        $type: MessageType.ToolsCall,
        role: 'assistant',
        tool_calls: [
          {
            tool_call_id: `call_${i}`,
            function_name: 'test_function',
            function_args: JSON.stringify({ index: i }),
          },
        ],
      }));

      const wrapper = mount(MetadataPill, {
        props: {
          items,
        },
        global: {
          provide: {
            getResultForToolCall: () => null,
          },
        },
      });

      // Should not show expand button
      expect(wrapper.find('.pill-header').exists()).toBe(false);
    });

    it('should expand to show all items when clicked', async () => {
      const items: ToolsCallMessage[] = Array.from({ length: 5 }, (_, i) => ({
        $type: MessageType.ToolsCall,
        role: 'assistant',
        tool_calls: [
          {
            tool_call_id: `call_${i}`,
            function_name: 'test_function',
            function_args: JSON.stringify({ index: i }),
          },
        ],
      }));

      const wrapper = mount(MetadataPill, {
        props: {
          items,
        },
        global: {
          provide: {
            getResultForToolCall: () => null,
          },
        },
      });

      // Initially collapsed
      const pillItems = wrapper.find('.pill-items');
      expect(pillItems.classes()).not.toContain('expanded');

      // Click to expand
      await wrapper.find('.pill-header').trigger('click');

      // Should be expanded
      expect(pillItems.classes()).toContain('expanded');
      expect(wrapper.text()).toContain('Collapse');
    });

    it('should collapse back when clicked again', async () => {
      const items: ToolsCallMessage[] = Array.from({ length: 5 }, (_, i) => ({
        $type: MessageType.ToolsCall,
        role: 'assistant',
        tool_calls: [
          {
            tool_call_id: `call_${i}`,
            function_name: 'test_function',
            function_args: JSON.stringify({ index: i }),
          },
        ],
      }));

      const wrapper = mount(MetadataPill, {
        props: {
          items,
        },
        global: {
          provide: {
            getResultForToolCall: () => null,
          },
        },
      });

      const header = wrapper.find('.pill-header');
      const pillItems = wrapper.find('.pill-items');

      // Expand
      await header.trigger('click');
      expect(pillItems.classes()).toContain('expanded');

      // Collapse
      await header.trigger('click');
      expect(pillItems.classes()).not.toContain('expanded');
      expect(wrapper.text()).toContain('Show all 5 items');
    });
  });

  describe('Tool-Specific Icons and Loading States', () => {
    it('should show weather-specific icon for weather tools', () => {
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

      const wrapper = mount(MetadataPill, {
        props: {
          items: [weatherToolCall],
        },
        global: {
          provide: {
            getResultForToolCall: () => null, // No result yet
          },
        },
      });

      const icon = wrapper.find('.item-icon');
      // Should show weather icon (🌤️) not generic tool icon (🔧)
      expect(icon.text()).not.toBe('🔧');
      expect(icon.text()).toContain('🌤️');
    });

    it('should show pulsing animation while waiting for result', () => {
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

      const wrapper = mount(MetadataPill, {
        props: {
          items: [weatherToolCall],
        },
        global: {
          provide: {
            getResultForToolCall: () => null, // No result yet
          },
        },
      });

      const icon = wrapper.find('.item-icon');
      expect(icon.classes()).toContain('pulsing');
    });

    it('should stop pulsing animation when result arrives', () => {
      const weatherResult: ToolCallResultMessage = {
        $type: MessageType.ToolCallResult,
        tool_call_id: 'call_123',
        result: "\u0022{\\r\\n  \\u0022location\\u0022: \\u0022Seattle\\u0022,\\r\\n  \\u0022temperature\\u0022: 64,\\r\\n  \\u0022temperatureUnit\\u0022: \\u0022F\\u0022,\\r\\n  \\u0022condition\\u0022: \\u0022Rainy\\u0022,\\r\\n  \\u0022humidity\\u0022: 43\\r\\n}\u0022",
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

      const icon = wrapper.find('.item-icon');
      expect(icon.classes()).not.toContain('pulsing');
    });

    it('should display location immediately from args before result', () => {
      const weatherToolCall: ToolsCallMessage = {
        $type: MessageType.ToolsCall,
        role: 'assistant',
        tool_calls: [
          {
            tool_call_id: 'call_123',
            function_name: 'get_weather',
            function_args: JSON.stringify({ location: 'Paris' }),
          },
        ],
      };

      const wrapper = mount(MetadataPill, {
        props: {
          items: [weatherToolCall],
        },
        global: {
          provide: {
            getResultForToolCall: () => null, // No result yet
          },
        },
      });

      const text = wrapper.text();
      expect(text).toContain('Paris');
      expect(text).toContain('Loading...');
    });

    it('should show temperature and condition when result arrives', () => {
      const weatherResult: ToolCallResultMessage = {
        $type: MessageType.ToolCallResult,
        tool_call_id: 'call_123',
        result: "\u0022{\\r\\n  \\u0022location\\u0022: \\u0022Tokyo\\u0022,\\r\\n  \\u0022temperature\\u0022: 22,\\r\\n  \\u0022temperatureUnit\\u0022: \\u0022C\\u0022,\\r\\n  \\u0022condition\\u0022: \\u0022Sunny\\u0022\\r\\n}\u0022",
        role: 'tool',
      };

      const weatherToolCall: ToolsCallMessage = {
        $type: MessageType.ToolsCall,
        role: 'assistant',
        tool_calls: [
          {
            tool_call_id: 'call_123',
            function_name: 'get_weather',
            function_args: JSON.stringify({ location: 'Tokyo' }),
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
      expect(text).toContain('Tokyo');
      expect(text).toContain('22°C');
      expect(text).toContain('Clear');
      expect(text).not.toContain('Loading...');
    });
  });

  describe('Integration with Real Data', () => {
    it('should handle the ResponseSample.md weather scenario', () => {
      // This matches the actual data from ResponseSample.md line 14
      const weatherResult: ToolCallResultMessage = {
        $type: MessageType.ToolCallResult,
        tool_call_id: 'call_nqBX7b43ksVjW68o86ZRRCB6',
        result: "\u0022{\\r\\n  \\u0022location\\u0022: \\u0022Seattle\\u0022,\\r\\n  \\u0022temperature\\u0022: 64,\\r\\n  \\u0022temperatureUnit\\u0022: \\u0022F\\u0022,\\r\\n  \\u0022condition\\u0022: \\u0022Rainy\\u0022,\\r\\n  \\u0022humidity\\u0022: 43,\\r\\n  \\u0022windSpeed\\u0022: 10,\\r\\n  \\u0022windUnit\\u0022: \\u0022mph\\u0022\\r\\n}\u0022",
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

