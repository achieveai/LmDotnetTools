import { afterEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import SmilesViewer from '@/components/SmilesViewer.vue';
import { SmilesRenderError } from '@/utils/smilesRenderer';

const renderSmiles = vi.hoisted(() => vi.fn());
vi.mock('@/utils/smilesRenderer', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/utils/smilesRenderer')>()),
  renderSmiles,
}));

const wrappers: ReturnType<typeof mount>[] = [];
afterEach(() => {
  wrappers.splice(0).forEach((wrapper) => wrapper.unmount());
  renderSmiles.mockReset();
});

/*
 * Structures go through the shared render queue, which yields to the event loop between two
 * drawings, so settling means draining timers and not only microtasks.
 */
async function settle() {
  for (let turn = 0; turn < 6; turn += 1) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
}

function mountViewer(source: string) {
  const wrapper = mount(SmilesViewer, { props: { source } });
  wrappers.push(wrapper);
  return wrapper;
}

describe('SmilesViewer', () => {
  it('draws one labelled figure per line of the fence', async () => {
    renderSmiles.mockResolvedValue('<svg xmlns="http://www.w3.org/2000/svg"></svg>');
    const wrapper = mountViewer('CCO ethanol\nc1ccccc1 benzene');
    await settle();
    expect(wrapper.findAll('[data-testid="smiles-figure"]')).toHaveLength(2);
    expect(wrapper.text()).toContain('ethanol');
    expect(wrapper.text()).toContain('benzene');
    expect(renderSmiles).toHaveBeenCalledTimes(2);
  });

  it('shows the parser message for an invalid structure and keeps the valid one', async () => {
    renderSmiles.mockImplementation((smiles: string) =>
      smiles === 'CCO'
        ? Promise.resolve('<svg xmlns="http://www.w3.org/2000/svg"></svg>')
        : Promise.reject(new SmilesRenderError('invalid ring closure'))
    );
    const wrapper = mountViewer('CCO\nC1CC');
    await settle();
    expect(wrapper.find('[data-testid="smiles-error"]').text()).toContain('invalid ring closure');
    expect(wrapper.findAll('[data-testid="smiles-figure"]')).toHaveLength(1);
  });

  it('toggles between the structures and the fence source', async () => {
    renderSmiles.mockResolvedValue('<svg xmlns="http://www.w3.org/2000/svg"></svg>');
    const wrapper = mountViewer('CCO');
    await settle();
    await wrapper.get('[data-testid="smiles-source-view"]').trigger('click');
    expect(wrapper.get('[data-testid="smiles-source"]').text()).toContain('CCO');
    expect(wrapper.find('[data-testid="smiles-figure"]').exists()).toBe(false);
  });

  it('draws nothing until the fence approaches the viewport', async () => {
    // jsdom has no IntersectionObserver, so the tests above exercise the render-at-once fallback.
    const observers: Array<{ approach: () => void }> = [];
    vi.stubGlobal(
      'IntersectionObserver',
      class {
        private readonly targets: Element[] = [];
        constructor(private readonly callback: IntersectionObserverCallback) {
          observers.push({
            approach: () =>
              this.callback(
                this.targets.map((target) => ({ target, isIntersecting: true }) as IntersectionObserverEntry),
                this as unknown as IntersectionObserver
              ),
          });
        }
        observe(target: Element) { this.targets.push(target); }
        unobserve() {}
        disconnect() {}
      }
    );
    try {
      renderSmiles.mockResolvedValue('<svg xmlns="http://www.w3.org/2000/svg"></svg>');
      const wrapper = mountViewer('CCO ethanol');
      await settle();
      expect(renderSmiles).not.toHaveBeenCalled();
      expect(wrapper.get('[data-testid="smiles-placeholder"]').text()).toBe('Chemical structures');

      observers[0].approach();
      await settle();
      expect(renderSmiles).toHaveBeenCalledOnce();
      expect(wrapper.findAll('[data-testid="smiles-figure"]')).toHaveLength(1);
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
