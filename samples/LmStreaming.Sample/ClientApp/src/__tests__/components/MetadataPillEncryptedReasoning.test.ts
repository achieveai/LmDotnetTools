import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import MetadataPill from '@/components/MetadataPill.vue';
import { MessageType, type ReasoningMessage } from '@/types';

function createReasoningMessage(
  reasoning: string,
  visibility: 'Plain' | 'Summary' | 'Encrypted' | 0 | 1 | 2
): ReasoningMessage {
  return {
    $type: MessageType.Reasoning,
    reasoning,
    visibility,
    role: 'assistant',
  };
}

describe('MetadataPill encrypted reasoning', () => {
  it('renders encrypted reasoning even when payload is empty', () => {
    const wrapper = mount(MetadataPill, {
      props: {
        items: [createReasoningMessage('', 'Encrypted')],
      },
    });

    expect(wrapper.text()).toContain('Thinking:');
    expect(wrapper.text()).toContain('Encrypted reasoning');
  });

  it('shows encrypted placeholder content when expanded', async () => {
    const wrapper = mount(MetadataPill, {
      props: {
        items: [createReasoningMessage('', 'Encrypted')],
      },
    });

    await wrapper.find('.pill-item').trigger('click');
    expect(wrapper.find('.reasoning-text').text()).toContain('[Encrypted reasoning hidden]');
  });

  it('treats numeric enum visibility=2 as encrypted', () => {
    const wrapper = mount(MetadataPill, {
      props: {
        items: [createReasoningMessage('base64-signature', 2)],
      },
    });

    expect(wrapper.text()).toContain('Encrypted reasoning');
    expect(wrapper.text()).not.toContain('base64-signature');
  });
});
