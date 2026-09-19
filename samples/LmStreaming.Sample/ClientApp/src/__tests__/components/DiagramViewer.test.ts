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

async function settle() {
  await Promise.resolve();
  await Promise.resolve();
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
