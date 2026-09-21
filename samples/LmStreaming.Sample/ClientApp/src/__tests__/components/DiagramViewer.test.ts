import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { enableAutoUnmount, mount } from "@vue/test-utils";
import DiagramViewer from "@/components/DiagramViewer.vue";
import { renderDiagram } from "@/utils/diagramRenderer";

enableAutoUnmount(afterEach);

vi.mock("@/utils/diagramRenderer", () => ({
  renderDiagram: vi.fn(),
}));

const renderMock = vi.mocked(renderDiagram);
const svg = (label: string) =>
  `<svg xmlns="http://www.w3.org/2000/svg"><text>${label}</text></svg>`;

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

/*
 * Renders go through the shared render queue, which hands the main thread back between two jobs.
 * That is a real macrotask, so draining microtasks alone no longer reaches the rendered state.
 */
async function settle() {
  for (let turn = 0; turn < 4; turn += 1) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
}

function readBlob(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.addEventListener("load", () => resolve(String(reader.result)));
    reader.addEventListener("error", () => reject(reader.error));
    reader.readAsText(blob);
  });
}

describe("DiagramViewer", () => {
  let created: Array<{ url: string; blob: Blob }>;
  let revoked: string[];

  beforeEach(() => {
    renderMock.mockReset();
    created = [];
    revoked = [];
    vi.spyOn(URL, "createObjectURL").mockImplementation((blob) => {
      const url = `blob:diagram-${created.length + 1}`;
      created.push({ url, blob: blob as Blob });
      return url;
    });
    vi.spyOn(URL, "revokeObjectURL").mockImplementation((url) => {
      revoked.push(url);
    });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("renders sanitized SVG as an isolated image and exposes source without rerendering", async () => {
    renderMock.mockResolvedValue(svg("flow"));
    const wrapper = mount(DiagramViewer, {
      props: { source: "graph TD; A-->B", language: "mermaid" },
    });
    await settle();

    expect(renderMock).toHaveBeenCalledOnce();
    expect(renderMock).toHaveBeenCalledWith("graph TD; A-->B", "mermaid");
    expect(wrapper.get('[data-testid="diagram-format"]').text()).toBe("Mermaid");
    expect(wrapper.get('[data-testid="diagram-image"]').attributes("src")).toBe(
      "blob:diagram-1",
    );
    expect(wrapper.get('[data-testid="diagram-download"]').attributes("href")).toBe(
      "blob:diagram-1",
    );

    await wrapper.get('[data-testid="diagram-source-view"]').trigger("click");
    expect(wrapper.get('[data-testid="diagram-source"]').text()).toContain(
      "graph TD; A-->B",
    );
    expect(renderMock).toHaveBeenCalledOnce();
  });

  it("ignores an older async render that resolves after the newest source", async () => {
    const oldRender = deferred<string>();
    const newRender = deferred<string>();
    renderMock.mockReturnValueOnce(oldRender.promise).mockReturnValueOnce(newRender.promise);
    const wrapper = mount(DiagramViewer, {
      props: { source: "old", language: "mermaid" },
    });

    await wrapper.setProps({ source: "new" });
    newRender.resolve(svg("new"));
    await settle();
    expect(wrapper.get('[data-testid="diagram-image"]').attributes("src")).toBe(
      "blob:diagram-1",
    );

    oldRender.resolve(svg("old"));
    await settle();
    expect(wrapper.get('[data-testid="diagram-image"]').attributes("src")).toBe(
      "blob:diagram-1",
    );
    expect(created).toHaveLength(1);
  });

  it("shows an accessible error and preserves source when rendering fails", async () => {
    renderMock.mockRejectedValue(new Error("Unexpected token on line 2"));
    const wrapper = mount(DiagramViewer, {
      props: { source: "@startuml\nAlice ->", language: "plantuml" },
    });
    await settle();

    expect(wrapper.get('[data-testid="diagram-error"]').attributes("role")).toBe(
      "alert",
    );
    expect(wrapper.get('[data-testid="diagram-error"]').text()).toContain(
      "Unexpected token on line 2",
    );
    expect(wrapper.find('[data-testid="diagram-image"]').exists()).toBe(false);

    await wrapper.get('[data-testid="diagram-source-view"]').trigger("click");
    expect(wrapper.get('[data-testid="diagram-source"]').text()).toContain("Alice ->");
    expect(wrapper.get('[data-testid="diagram-format"]').text()).toBe("PlantUML");
  });

  it("retries a failed render and replaces the error with the recovered diagram", async () => {
    renderMock
      .mockRejectedValueOnce(new Error("Renderer initialization timed out"))
      .mockResolvedValueOnce(svg("recovered"));
    const wrapper = mount(DiagramViewer, {
      props: { source: "graph TD; A-->B", language: "mermaid" },
    });
    await settle();

    await wrapper.get('[data-testid="diagram-retry"]').trigger("click");
    await settle();

    expect(renderMock).toHaveBeenCalledTimes(2);
    expect(wrapper.find('[data-testid="diagram-error"]').exists()).toBe(false);
    expect(wrapper.get('[data-testid="diagram-image"]').attributes("src")).toBe(
      "blob:diagram-1",
    );
  });

  it("expands without rerendering and provides bounded zoom plus fit controls", async () => {
    renderMock.mockResolvedValue(svg("sequence"));
    const wrapper = mount(DiagramViewer, {
      attachTo: document.body,
      props: { source: "sequenceDiagram", language: "mermaid" },
    });
    await settle();

    await wrapper.get('[data-testid="diagram-expand"]').trigger("click");
    expect(wrapper.get('[data-testid="diagram-modal"] [role="dialog"]').attributes("role")).toBe(
      "dialog",
    );
    expect(renderMock).toHaveBeenCalledOnce();

    await wrapper.get('[data-testid="diagram-zoom-in"]').trigger("click");
    expect(wrapper.get('[data-testid="diagram-expanded-image"]').attributes("style")).toContain(
      "scale(1.25)",
    );
    await wrapper.get('[data-testid="diagram-fit"]').trigger("click");
    expect(wrapper.get('[data-testid="diagram-expanded-image"]').attributes("style")).toContain(
      "scale(1)",
    );
    expect(renderMock).toHaveBeenCalledOnce();
  });

  it("configures tall diagrams to contain within both axes of the expanded viewport", async () => {
    renderMock.mockResolvedValue(
      '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 1000"></svg>',
    );
    const wrapper = mount(DiagramViewer, {
      attachTo: document.body,
      props: { source: "tall diagram", language: "plantuml" },
    });
    await settle();

    await wrapper.get('[data-testid="diagram-expand"]').trigger("click");
    const style = wrapper
      .get('[data-testid="diagram-expanded-image"]')
      .attributes("style");

    expect(style).toContain("width: 100%");
    expect(style).toContain("height: 100%");
    expect(style).toContain("object-fit: contain");
  });

/**
 * jsdom has no IntersectionObserver, so every test above exercises the no-observer fallback where a
 * diagram counts as approached at once. These tests install one to pin the lazy pipeline itself.
 */
class FakeIntersectionObserver {
  static instances: FakeIntersectionObserver[] = [];
  readonly targets: Element[] = [];
  disconnected = false;

  constructor(
    private readonly callback: IntersectionObserverCallback,
    readonly options?: IntersectionObserverInit,
  ) {
    FakeIntersectionObserver.instances.push(this);
  }

  observe(target: Element) {
    this.targets.push(target);
  }

  unobserve() {}

  disconnect() {
    this.disconnected = true;
  }

  approach() {
    this.callback(
      this.targets.map((target) => ({ target, isIntersecting: true }) as IntersectionObserverEntry),
      this as unknown as IntersectionObserver,
    );
  }
}

describe("lazy rendering", () => {
  beforeEach(() => {
    FakeIntersectionObserver.instances = [];
    vi.stubGlobal("IntersectionObserver", FakeIntersectionObserver);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("shows a labelled placeholder and renders nothing until the diagram approaches the viewport", async () => {
    renderMock.mockResolvedValue(svg("late"));
    const wrapper = mount(DiagramViewer, {
      props: { source: "graph TD; A-->B", language: "mermaid" },
    });
    await settle();

    expect(renderMock).not.toHaveBeenCalled();
    expect(wrapper.get('[data-testid="diagram-placeholder"]').text()).toBe("Mermaid diagram");
    expect(wrapper.find('[data-testid="diagram-image"]').exists()).toBe(false);
    // A viewport of lead time, so the diagram is drawn before the reader reaches it.
    expect(FakeIntersectionObserver.instances[0].options?.rootMargin).toBe("100% 0px");

    FakeIntersectionObserver.instances[0].approach();
    await settle();

    expect(renderMock).toHaveBeenCalledOnce();
    expect(wrapper.find('[data-testid="diagram-placeholder"]').exists()).toBe(false);
    expect(wrapper.get('[data-testid="diagram-image"]').attributes("src")).toBe("blob:diagram-1");
  });

  it("keeps a drawn diagram when it scrolls out of view and back", async () => {
    renderMock.mockResolvedValue(svg("kept"));
    const wrapper = mount(DiagramViewer, {
      props: { source: "graph TD; A-->B", language: "mermaid" },
    });
    const observer = FakeIntersectionObserver.instances[0];
    observer.approach();
    await settle();
    expect(renderMock).toHaveBeenCalledOnce();

    expect(observer.disconnected).toBe(true);
    observer.approach();
    await settle();

    expect(renderMock).toHaveBeenCalledOnce();
    expect(wrapper.get('[data-testid="diagram-image"]').attributes("src")).toBe("blob:diagram-1");
  });

  it("never renders a diagram unmounted while it waited its turn in the queue", async () => {
    const blocking = deferred<string>();
    renderMock.mockReturnValueOnce(blocking.promise).mockResolvedValue(svg("second"));
    const visible = mount(DiagramViewer, { props: { source: "visible", language: "mermaid" } });
    const passed = mount(DiagramViewer, { props: { source: "passed", language: "mermaid" } });

    FakeIntersectionObserver.instances[0].approach();
    FakeIntersectionObserver.instances[1].approach();
    await settle();
    expect(renderMock).toHaveBeenCalledOnce();

    passed.unmount();
    blocking.resolve(svg("first"));
    await settle();

    expect(renderMock).toHaveBeenCalledOnce();
    expect(renderMock).toHaveBeenCalledWith("visible", "mermaid");
    expect(visible.get('[data-testid="diagram-image"]').attributes("src")).toBe("blob:diagram-1");
  });
});

  it("uses the winning sanitized SVG for download and revokes replaced and unmounted URLs", async () => {
    renderMock.mockResolvedValueOnce(svg("first")).mockResolvedValueOnce(svg("second"));
    const wrapper = mount(DiagramViewer, {
      props: { source: "first", language: "mermaid" },
    });
    await settle();

    await wrapper.setProps({ source: "second", language: "plantuml" });
    await settle();
    expect(revoked).toEqual(["blob:diagram-1"]);
    expect(wrapper.get('[data-testid="diagram-download"]').attributes("download")).toBe(
      "diagram-plantuml.svg",
    );
    expect(await readBlob(created[1].blob)).toBe(svg("second"));

    wrapper.unmount();
    expect(revoked).toEqual(["blob:diagram-1", "blob:diagram-2"]);
  });
});
