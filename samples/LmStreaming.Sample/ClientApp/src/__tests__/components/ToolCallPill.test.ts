import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import ToolCallPill from '@/components/ToolCallPill.vue';
import type { ToolCall, ToolCallResultMessage } from '@/types';
import { MessageType } from '@/types';

describe('ToolCallPill.vue', () => {
  const createToolCall = (overrides: Partial<ToolCall> = {}): ToolCall => ({
    function_name: 'test_function',
    function_args: JSON.stringify({ arg1: 'value1', arg2: 'value2' }),
    tool_call_id: 'tc_123',
    ...overrides,
  });

  const createToolResult = (overrides: Partial<ToolCallResultMessage> = {}): ToolCallResultMessage => ({
    $type: MessageType.ToolCallResult,
    role: 'tool',
    tool_call_id: 'tc_123',
    result: 'Test result',
    ...overrides,
  });

  it('should render tool call with function name', () => {
    const toolCall = createToolCall();
    const wrapper = mount(ToolCallPill, {
      props: { toolCall },
    });

    expect(wrapper.text()).toContain('test_function');
  });

  it('should show loading state when no result', () => {
    const toolCall = createToolCall();
    const wrapper = mount(ToolCallPill, {
      props: { toolCall },
    });

    // The EventPill should have isLoading=true
    expect(wrapper.find('.pill-spinner').exists()).toBe(true);
  });

  it('should show result when available', () => {
    const toolCall = createToolCall();
    const result = createToolResult({ result: 'Success!' });
    const wrapper = mount(ToolCallPill, {
      props: { toolCall, result },
    });

    expect(wrapper.text()).toContain('Success!');
    expect(wrapper.find('.pill-spinner').exists()).toBe(false);
  });

  it('should format tool call preview with truncated args', () => {
    const toolCall = createToolCall({
      function_name: 'calculate',
      function_args: JSON.stringify({ expression: '2+2' }),
    });
    const wrapper = mount(ToolCallPill, {
      props: { toolCall },
    });

    expect(wrapper.text()).toContain('calculate');
    expect(wrapper.text()).toContain('expression');
  });

  it('should handle missing function_args', () => {
    const toolCall = createToolCall({
      function_args: undefined,
    });
    const wrapper = mount(ToolCallPill, {
      props: { toolCall },
    });

    expect(wrapper.text()).toContain('test_function()');
  });

  it('should handle empty function_args', () => {
    const toolCall = createToolCall({
      function_args: '{}',
    });
    const wrapper = mount(ToolCallPill, {
      props: { toolCall },
    });

    expect(wrapper.text()).toContain('test_function()');
  });

  it('should handle invalid JSON in function_args', () => {
    const toolCall = createToolCall({
      function_args: 'not valid json',
    });
    const wrapper = mount(ToolCallPill, {
      props: { toolCall },
    });

    // Should not throw, should show function name with fallback
    expect(wrapper.text()).toContain('test_function');
  });

  it('should be expandable to show full content', async () => {
    const toolCall = createToolCall();
    const result = createToolResult();
    const wrapper = mount(ToolCallPill, {
      props: { toolCall, result },
    });

    await wrapper.find('.event-pill').trigger('click');

    expect(wrapper.find('.pill-content').exists()).toBe(true);
    expect(wrapper.find('.pill-content').text()).toContain('Function:');
    expect(wrapper.find('.pill-content').text()).toContain('Arguments:');
    expect(wrapper.find('.pill-content').text()).toContain('Result:');
  });

  it('should handle unknown function name', () => {
    const toolCall = createToolCall({
      function_name: undefined,
    });
    const wrapper = mount(ToolCallPill, {
      props: { toolCall },
    });

    expect(wrapper.text()).toContain('unknown');
  });
});
