<script setup lang="ts">
import { onBeforeUnmount, ref } from "vue";

const props = withDefaults(
  defineProps<{
    label: string;
    orientation: "vertical" | "horizontal";
    value: number;
    min: number;
    max: number;
    defaultValue: number;
    direction?: 1 | -1;
    controls?: string;
  }>(),
  { direction: 1 },
);

const emit = defineEmits<{
  "update:value": [value: number];
  dragging: [dragging: boolean];
}>();

const activePointer = ref<number | null>(null);
let pointerStart = 0;
let valueStart = 0;

function clamp(value: number): number {
  return Math.min(props.max, Math.max(props.min, Math.round(value)));
}

function update(value: number): void {
  emit("update:value", clamp(value));
}

function onKeydown(event: KeyboardEvent): void {
  const previous = props.orientation === "vertical" ? "ArrowLeft" : "ArrowUp";
  const next = props.orientation === "vertical" ? "ArrowRight" : "ArrowDown";
  if (![previous, next, "Home", "End"].includes(event.key)) return;
  event.preventDefault();
  if (event.key === "Home") return update(props.min);
  if (event.key === "End") return update(props.max);
  const delta = event.key === next ? 8 : -8;
  update(props.value + delta * props.direction);
}

function onPointerDown(event: PointerEvent): void {
  if (event.button !== 0) return;
  activePointer.value = event.pointerId;
  pointerStart =
    props.orientation === "vertical" ? event.clientX : event.clientY;
  valueStart = props.value;
  (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
  (event.currentTarget as HTMLElement).focus();
  emit("dragging", true);
  event.preventDefault();
}

onBeforeUnmount(() => {
  if (activePointer.value !== null) emit("dragging", false);
});

function onPointerMove(event: PointerEvent): void {
  if (activePointer.value !== event.pointerId) return;
  const current =
    props.orientation === "vertical" ? event.clientX : event.clientY;
  update(valueStart + (current - pointerStart) * props.direction);
}

function finishPointer(event: PointerEvent): void {
  if (activePointer.value !== event.pointerId) return;
  const target = event.currentTarget as HTMLElement;
  if (target.hasPointerCapture?.(event.pointerId))
    target.releasePointerCapture(event.pointerId);
  activePointer.value = null;
  emit("dragging", false);
}
</script>

<template>
  <div
    :class="[
      'panel-splitter',
      `panel-splitter-${orientation}`,
      { dragging: activePointer !== null },
    ]"
    role="separator"
    tabindex="0"
    :aria-label="label"
    :aria-orientation="orientation"
    :aria-valuemin="min"
    :aria-valuemax="max"
    :aria-valuenow="value"
    :aria-valuetext="`${value} pixels`"
    :aria-controls="controls"
    @keydown="onKeydown"
    @dblclick="update(defaultValue)"
    @pointerdown="onPointerDown"
    @pointermove="onPointerMove"
    @pointerup="finishPointer"
    @pointercancel="finishPointer"
    @lostpointercapture="finishPointer"
  >
    <span aria-hidden="true" />
  </div>
</template>

<style scoped>
.panel-splitter {
  flex: 0 0 7px;
  display: grid;
  place-items: center;
  touch-action: none;
  z-index: 22;
}
.panel-splitter-vertical {
  cursor: col-resize;
  width: 7px;
  margin-inline: -3px;
}
.panel-splitter-horizontal {
  cursor: row-resize;
  height: 7px;
  margin-block: -3px;
}
.panel-splitter span {
  display: block;
  border-radius: 99px;
  background: #cbd5e1;
  opacity: 0;
  transition:
    opacity 0.15s,
    background 0.15s;
}
.panel-splitter-vertical span {
  width: 2px;
  height: 28px;
}
.panel-splitter-horizontal span {
  width: 28px;
  height: 2px;
}
.panel-splitter:hover span,
.panel-splitter:focus-visible span,
.panel-splitter.dragging span {
  opacity: 1;
  background: #64748b;
}
.panel-splitter:focus-visible {
  outline: 2px solid #2563eb;
  outline-offset: -1px;
}
</style>
