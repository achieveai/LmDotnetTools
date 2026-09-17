import { afterEach, describe, it, expect, vi } from "vitest";
import { enableAutoUnmount, mount } from "@vue/test-utils";
import PendingQuestionDock from "@/components/PendingQuestionDock.vue";
import { GET_RESULT_FOR_TOOL_CALL } from "@/composables/useToolResult";
import { SUBMIT_CLIENT_TOOL_RESULT } from "@/composables/useClientToolSubmit";
import type { ClientToolSubmitFn } from "@/composables/useClientToolSubmit";
import { MessageType } from "@/types";
import type { DisplayItem, ToolCall, ToolCallResultMessage } from "@/types";

enableAutoUnmount(afterEach);

describe("PendingQuestionDock", () => {
  const ARGS = JSON.stringify({
    context: "Need your input",
    questions: [
      { prompt: "Pick a colour", options: [{ label: "Blue", value: "blue" }] },
    ],
  });

  function call(id: string): ToolCall {
    return {
      tool_call_id: id,
      function_name: "AskUserQuestion",
      function_args: ARGS,
    };
  }

  function pill(id: string, ...toolCalls: ToolCall[]): DisplayItem {
    return {
      type: "pill",
      id,
      items: [
        {
          $type: MessageType.ToolsCall,
          role: "assistant",
          tool_calls: toolCalls,
        } as never,
      ],
    } as DisplayItem;
  }

  function deferred(id: string): ToolCallResultMessage {
    return {
      $type: MessageType.ToolCallResult,
      tool_call_id: id,
      result: "",
      is_error: false,
      is_deferred: true,
      role: "tool",
    };
  }

  function mountDock(
    options: {
      displayItems?: DisplayItem[];
      results?: Record<string, ToolCallResultMessage>;
      active?: boolean;
      sourceLabel?: string;
      scopeKey?: string;
      requestedQuestionId?: string | null;
      submit?: ClientToolSubmitFn;
    } = {},
  ) {
    const results = options.results ?? {};
    return mount(PendingQuestionDock, {
      attachTo: document.body,
      props: {
        displayItems: options.displayItems ?? [],
        active: options.active ?? true,
        sourceLabel: options.sourceLabel ?? "Main conversation",
        scopeKey: options.scopeKey ?? "thread-1:main",
        requestedQuestionId: options.requestedQuestionId ?? null,
      },
      global: {
        provide: {
          [GET_RESULT_FOR_TOOL_CALL]: (id: string | null | undefined) =>
            (id ? results[id] : null) ?? null,
          [SUBMIT_CLIENT_TOOL_RESULT]:
            options.submit ??
            vi.fn(async () => ({ status: "acked", duplicate: false })),
        },
      },
    });
  }

  it("renders nothing when no question is pending", () => {
    const wrapper = mountDock();
    expect(wrapper.find('[data-testid="question-dock"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="question-review-modal"]').exists()).toBe(
      false,
    );
  });

  it("shows a compact notice and auto-opens the oldest question only for an active view", () => {
    const wrapper = mountDock({
      displayItems: [pill("p1", call("q1"))],
      results: { q1: deferred("q1") },
      sourceLabel: "Main conversation",
    });

    expect(wrapper.get('[data-testid="question-dock"]').text()).toContain(
      "Needs your answer",
    );
    expect(wrapper.get('[data-testid="question-dock"]').text()).toContain(
      "Main conversation",
    );
    expect(
      wrapper.get('[data-testid="question-review-modal"]').text(),
    ).toContain("Pick a colour");
    expect(wrapper.emitted("opened")).toEqual([["q1"]]);

    const inactive = mountDock({
      displayItems: [pill("p1", call("q1"))],
      results: { q1: deferred("q1") },
      active: false,
    });
    expect(
      inactive.find('[data-testid="question-review-modal"]').exists(),
    ).toBe(false);
    expect(inactive.find('[data-testid="question-review"]').exists()).toBe(
      true,
    );
  });

  it("Close and Later hide without cancelling, submitting, or reopening on a reactive refresh", async () => {
    const submit = vi.fn<ClientToolSubmitFn>(async () => ({
      status: "acked",
      duplicate: false,
    }));
    const items = [pill("p1", call("q1"))];
    const wrapper = mountDock({
      displayItems: items,
      results: { q1: deferred("q1") },
      submit,
    });

    await wrapper
      .get('[data-testid="question-review-modal-close"]')
      .trigger("click");
    expect(wrapper.find('[data-testid="question-review-modal"]').exists()).toBe(
      false,
    );
    expect(wrapper.find('[data-testid="question-dock"]').exists()).toBe(true);
    expect(submit).not.toHaveBeenCalled();

    await wrapper.get('[data-testid="question-review"]').trigger("click");
    await wrapper.get('[data-testid="question-later"]').trigger("click");
    expect(submit).not.toHaveBeenCalled();
    expect(wrapper.emitted("open-change")).toEqual([
      [true],
      [false],
      [true],
      [false],
    ]);

    await wrapper.setProps({ displayItems: [...items] });
    expect(wrapper.find('[data-testid="question-review-modal"]').exists()).toBe(
      false,
    );
  });

  it("queues one live form at a time with stable owner-scoped draft keys", async () => {
    const wrapper = mountDock({
      displayItems: [pill("p1", call("q1")), pill("p2", call("q2"))],
      results: { q1: deferred("q1"), q2: deferred("q2") },
      scopeKey: "thread-1:agent-a",
      sourceLabel: "Research agent",
    });

    expect(wrapper.get('[data-testid="question-count"]').text()).toContain("2");
    expect(wrapper.findAll('[data-testid="question-rich"]')).toHaveLength(1);
    expect(
      wrapper.get('[data-testid="question-queue-position"]').text(),
    ).toContain("1 of 2");
    expect(
      wrapper.getComponent({ name: "QuestionRich" }).props("draftKey"),
    ).toBe("thread-1:agent-a:q1");

    await wrapper.get('[data-testid="question-next-pending"]').trigger("click");
    expect(
      wrapper.get('[data-testid="question-queue-position"]').text(),
    ).toContain("2 of 2");
    expect(
      wrapper.getComponent({ name: "QuestionRich" }).props("draftKey"),
    ).toBe("thread-1:agent-a:q2");
    expect(
      wrapper.get('[data-testid="question-review-modal"]').text(),
    ).toContain("Research agent");
  });

  it("opens an exact requested question and exposes the same API to the parent inbox", async () => {
    const results = {
      q1: deferred("q1"),
      q2: deferred("q2"),
      q3: deferred("q3"),
    };
    const wrapper = mountDock({
      displayItems: [pill("p1", call("q1")), pill("p2", call("q2"))],
      results,
      active: false,
    });

    await wrapper.setProps({ active: true, requestedQuestionId: "q2" });
    expect(
      wrapper.getComponent({ name: "QuestionRich" }).props("toolCall"),
    ).toMatchObject({ tool_call_id: "q2" });

    await wrapper.get('[data-testid="question-later"]').trigger("click");
    await wrapper.setProps({
      displayItems: [
        pill("p1", call("q1")),
        pill("p2", call("q2")),
        pill("p3", call("q3")),
      ],
    });
    expect(
      wrapper.getComponent({ name: "QuestionRich" }).props("toolCall"),
    ).toMatchObject({ tool_call_id: "q1" });

    await wrapper.get('[data-testid="question-later"]').trigger("click");
    (
      wrapper.vm as unknown as { openQuestion: (id: string) => void }
    ).openQuestion("q1");
    await wrapper.vm.$nextTick();
    expect(
      wrapper.getComponent({ name: "QuestionRich" }).props("toolCall"),
    ).toMatchObject({ tool_call_id: "q1" });
  });

  it("disables queue switching while the visible question reports a submission in flight", async () => {
    const wrapper = mountDock({
      displayItems: [pill("p1", call("q1")), pill("p2", call("q2"))],
      results: { q1: deferred("q1"), q2: deferred("q2") },
    });

    wrapper
      .getComponent({ name: "QuestionRich" })
      .vm.$emit("busy-change", true);
    await wrapper.vm.$nextTick();
    expect(
      (
        wrapper.get('[data-testid="question-next-pending"]')
          .element as HTMLButtonElement
      ).disabled,
    ).toBe(true);
    expect(
      (
        wrapper.get('[data-testid="question-later"]')
          .element as HTMLButtonElement
      ).disabled,
    ).toBe(true);
    await wrapper
      .get('[data-testid="question-review-modal-close"]')
      .trigger("click");
    expect(wrapper.find('[data-testid="question-review-modal"]').exists()).toBe(
      true,
    );
    expect(wrapper.emitted("busy-change")?.at(-1)).toEqual([true]);
  });

  it("defers automatic opening while another dialog is active but keeps manual Review available", async () => {
    const otherDialog = document.createElement("div");
    otherDialog.setAttribute("role", "dialog");
    document.body.appendChild(otherDialog);

    const wrapper = mountDock({
      displayItems: [pill("p1", call("q1"))],
      results: { q1: deferred("q1") },
    });
    expect(wrapper.find('[data-testid="question-review-modal"]').exists()).toBe(
      false,
    );

    await wrapper.get('[data-testid="question-review"]').trigger("click");
    expect(wrapper.find('[data-testid="question-review-modal"]').exists()).toBe(
      true,
    );
    otherDialog.remove();
  });
});
