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

  describe('persisting to localStorage (F-003, #784)', () => {
    afterEach(() => {
      localStorage.clear();
      vi.restoreAllMocks();
    });

    it('does not write on every intermediate pointermove, only once the gesture ends', async () => {
      const setItemSpy = vi.spyOn(Storage.prototype, 'setItem');
      const wrapper = mount(PanelSplitter, {
        attachTo: document.body,
        props: {
          label: 'Resize workspace',
          orientation: 'horizontal',
          value: 300,
          min: 180,
          max: 500,
          defaultValue: 300,
          persistKey: 'test.splitter.height',
        },
      });
      const element = wrapper.get<HTMLElement>('[role="separator"]');
      element.element.setPointerCapture = vi.fn();
      element.element.releasePointerCapture = vi.fn();
      const pointer = (type: string, y: number) => {
        const event = new MouseEvent(type, { bubbles: true, clientY: y, button: 0 });
        Object.defineProperty(event, 'pointerId', { value: 7 });
        element.element.dispatchEvent(event);
      };

      pointer('pointerdown', 200);
      pointer('pointermove', 210);
      pointer('pointermove', 220);
      pointer('pointermove', 230);
      // Three intermediate ticks moved the value (and emitted update:value each time)…
      expect(wrapper.emitted('update:value')).toEqual([[310], [320], [330]]);
      // …but none of them touched storage while the gesture is still in progress.
      expect(setItemSpy).not.toHaveBeenCalled();

      pointer('pointerup', 230);
      // The gesture ending flushes exactly one write, with the final value.
      expect(setItemSpy).toHaveBeenCalledTimes(1);
      expect(setItemSpy).toHaveBeenCalledWith('test.splitter.height', '330');
      expect(localStorage.getItem('test.splitter.height')).toBe('330');
    });

    it('persists immediately for discrete changes (arrow key, double-click reset)', async () => {
      const wrapper = mount(PanelSplitter, {
        props: {
          label: 'Resize projects',
          orientation: 'vertical',
          value: 280,
          min: 220,
          max: 420,
          defaultValue: 280,
          persistKey: 'test.splitter.width',
        },
      });
      const separator = wrapper.get('[role="separator"]');
      await separator.trigger('keydown', { key: 'ArrowRight' });
      expect(localStorage.getItem('test.splitter.width')).toBe('288');
      await separator.trigger('dblclick');
      expect(localStorage.getItem('test.splitter.width')).toBe('280');
    });

    it('does not touch storage at all without a persistKey', async () => {
      const setItemSpy = vi.spyOn(Storage.prototype, 'setItem');
      const wrapper = mount(PanelSplitter, {
        props: { label: 'Resize projects', orientation: 'vertical', value: 280, min: 220, max: 420, defaultValue: 280 },
      });
      await wrapper.get('[role="separator"]').trigger('keydown', { key: 'ArrowRight' });
      expect(setItemSpy).not.toHaveBeenCalled();
    });
  });
});
