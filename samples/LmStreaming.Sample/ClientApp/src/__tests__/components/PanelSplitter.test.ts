import { afterEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import PanelSplitter from '@/components/PanelSplitter.vue';

describe('PanelSplitter', () => {
  afterEach(() => document.body.replaceChildren());

  it('supports arrows, bounds, and double-click reset', async () => {
    const wrapper = mount(PanelSplitter, {
      props: { label: 'Resize projects', orientation: 'vertical', value: 280, min: 220, max: 420, defaultValue: 280 },
    });
    const separator = wrapper.get('[role="separator"]');
    expect(separator.attributes('aria-valuenow')).toBe('280');
    await separator.trigger('keydown', { key: 'ArrowRight' });
    await separator.trigger('keydown', { key: 'Home' });
    await separator.trigger('keydown', { key: 'End' });
    await separator.trigger('dblclick');
    expect(wrapper.emitted('update:value')).toEqual([[288], [220], [420], [280]]);
  });

  it('reports pointer deltas and always releases drag state', async () => {
    const wrapper = mount(PanelSplitter, {
      attachTo: document.body,
      props: { label: 'Resize workspace', orientation: 'horizontal', value: 300, min: 180, max: 500, defaultValue: 300 },
    });
    const element = wrapper.get<HTMLElement>('[role="separator"]');
    element.element.setPointerCapture = vi.fn();
    element.element.releasePointerCapture = vi.fn();
    const pointer = (type: string, y: number) => {
      const event = new MouseEvent(type, { bubbles: true, clientY: y, button: 0 });
      Object.defineProperty(event, 'pointerId', { value: 4 });
      element.element.dispatchEvent(event);
    };
    pointer('pointerdown', 200);
    pointer('pointermove', 230);
    pointer('pointerup', 230);
    expect(wrapper.emitted('update:value')).toEqual([[330]]);
    expect(wrapper.emitted('dragging')).toEqual([[true], [false]]);
  });
});
