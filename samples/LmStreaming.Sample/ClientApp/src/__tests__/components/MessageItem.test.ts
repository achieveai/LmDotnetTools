import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import MessageItem from '@/components/MessageItem.vue';
import type { ChatMessage } from '@/composables/useChat';
import {
  type TextMessage,
  type ToolsCallMessage,
  type ReasoningMessage,
  MessageType,
} from '@/types';

describe('MessageItem.vue', () => {
  const createTextMessage = (text: string): TextMessage => ({
    $type: MessageType.Text,
    text,
    role: 'assistant',
  });

  const createToolsCallMessage = (): ToolsCallMessage => ({
    $type: MessageType.ToolsCall,
    tool_calls: [
      {
        function_name: 'get_weather',
        function_args: '{"location": "NYC"}',
        tool_call_id: 'call_123',
      },
    ],
    role: 'assistant',
  });

  const createReasoningMessage = (reasoning: string): ReasoningMessage => ({
    $type: MessageType.Reasoning,
    reasoning,
    visibility: 'Plain',
    role: 'assistant',
  });

  const createChatMessage = (
    content: TextMessage | ToolsCallMessage | ReasoningMessage,
    role: 'user' | 'assistant' = 'assistant',
    isStreaming = false
  ): ChatMessage => ({
    id: 'msg-123',
    role,
    content,
    isStreaming,
  });

  it('should render TextMessage for text content', () => {
    const chatMessage = createChatMessage(createTextMessage('Hello world'));
    const wrapper = mount(MessageItem, {
      props: { message: chatMessage },
    });

    expect(wrapper.find('.text-message').exists()).toBe(true);
    expect(wrapper.find('.text').text()).toBe('Hello world');
  });

  it('should render ToolCallPill for tools_call content', () => {
    const chatMessage = createChatMessage(createToolsCallMessage());
    const wrapper = mount(MessageItem, {
      props: { message: chatMessage },
    });

    // Tool calls are now rendered as pills in a container
    expect(wrapper.find('.tool-calls-container').exists()).toBe(true);
    expect(wrapper.find('.event-pill').exists()).toBe(true);
  });

  it('should render ThinkingPill for reasoning content', () => {
    const chatMessage = createChatMessage(createReasoningMessage('Thinking about the problem...'));
    const wrapper = mount(MessageItem, {
      props: { message: chatMessage },
    });

    // Reasoning is now rendered as a ThinkingPill
    expect(wrapper.find('.event-pill').exists()).toBe(true);
    expect(wrapper.find('.event-pill').classes()).toContain('thinking');
  });

  it('should show user avatar for user role', () => {
    const chatMessage = createChatMessage(createTextMessage('User message'), 'user');
    const wrapper = mount(MessageItem, {
      props: { message: chatMessage },
    });

    expect(wrapper.find('.message-item').classes()).toContain('user');
    // User avatar emoji
    expect(wrapper.find('.avatar').text()).toContain('\u{1F464}');
  });

  it('should show assistant avatar for assistant role', () => {
    const chatMessage = createChatMessage(createTextMessage('Assistant message'), 'assistant');
    const wrapper = mount(MessageItem, {
      props: { message: chatMessage },
    });

    expect(wrapper.find('.message-item').classes()).toContain('assistant');
    // Robot avatar emoji
    expect(wrapper.find('.avatar').text()).toContain('\u{1F916}');
  });

  it('should apply correct CSS class for user role', () => {
    const chatMessage = createChatMessage(createTextMessage('Test'), 'user');
    const wrapper = mount(MessageItem, {
      props: { message: chatMessage },
    });

    expect(wrapper.find('.message-item.user').exists()).toBe(true);
  });

  it('should apply correct CSS class for assistant role', () => {
    const chatMessage = createChatMessage(createTextMessage('Test'), 'assistant');
    const wrapper = mount(MessageItem, {
      props: { message: chatMessage },
    });

    expect(wrapper.find('.message-item.assistant').exists()).toBe(true);
  });

  it('should pass isStreaming prop to TextMessage', () => {
    const chatMessage = createChatMessage(createTextMessage('Streaming...'), 'assistant', true);
    const wrapper = mount(MessageItem, {
      props: { message: chatMessage },
    });

    // When streaming, the cursor should be visible
    expect(wrapper.find('.cursor').exists()).toBe(true);
  });

  it('should use left alignment for assistant messages', () => {
    const chatMessage = createChatMessage(createTextMessage('Test'), 'assistant');
    const wrapper = mount(MessageItem, {
      props: { message: chatMessage },
    });

    // Assistant messages should have the assistant class (left-aligned)
    expect(wrapper.find('.message-item.assistant').exists()).toBe(true);
    expect(wrapper.find('.assistant-avatar').exists()).toBe(true);
  });

  it('should use right alignment for user messages', () => {
    const chatMessage = createChatMessage(createTextMessage('Test'), 'user');
    const wrapper = mount(MessageItem, {
      props: { message: chatMessage },
    });

    // User messages should have the user class (right-aligned)
    expect(wrapper.find('.message-item.user').exists()).toBe(true);
    expect(wrapper.find('.user-avatar').exists()).toBe(true);
  });

  it('should render unknown message types with JSON fallback', () => {
    // Create a message with an unknown type
    const unknownContent = {
      $type: 'unknown_type' as const,
      role: 'assistant' as const,
      someData: 'value',
    };

    const chatMessage: ChatMessage = {
      id: 'msg-123',
      role: 'assistant',
      content: unknownContent as unknown as TextMessage,
      isStreaming: false,
    };

    const wrapper = mount(MessageItem, {
      props: { message: chatMessage },
    });

    expect(wrapper.find('.unknown').exists()).toBe(true);
    expect(wrapper.find('.unknown pre').exists()).toBe(true);
  });
});
