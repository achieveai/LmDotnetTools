import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
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

function mountViewer(source: string) {
  const wrapper = mount(SmilesViewer, { props: { source } });
  wrappers.push(wrapper);
  return wrapper;
}

describe('SmilesViewer', () => {
  it('draws one labelled figure per line of the fence', async () => {
    renderSmiles.mockResolvedValue('<svg xmlns="http://www.w3.org/2000/svg"></svg>');
    const wrapper = mountViewer('CCO ethanol\nc1ccccc1 benzene');
    await flushPromises();
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
    await flushPromises();
    expect(wrapper.find('[data-testid="smiles-error"]').text()).toContain('invalid ring closure');
    expect(wrapper.findAll('[data-testid="smiles-figure"]')).toHaveLength(1);
  });

  it('toggles between the structures and the fence source', async () => {
    renderSmiles.mockResolvedValue('<svg xmlns="http://www.w3.org/2000/svg"></svg>');
    const wrapper = mountViewer('CCO');
    await flushPromises();
    await wrapper.get('[data-testid="smiles-source-view"]').trigger('click');
    expect(wrapper.get('[data-testid="smiles-source"]').text()).toContain('CCO');
    expect(wrapper.find('[data-testid="smiles-figure"]').exists()).toBe(false);
  });
});
