<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from "vue";
import {
  getBackupCosts,
  getSandboxInfo,
  listBackupSnapshots,
  purgeBackupWorkspace,
  restoreBackupSnapshot,
  type BackupCosts,
  type BackupSnapshot,
  type SandboxPanelInfo,
} from "@/api/sandboxApi";
import OperatorSecretField from "@/components/OperatorSecretField.vue";

/**
 * The inspector's Sandbox section: the image/digest/home the gateway acknowledged for the conversation's
 * live sandbox (with the image's conformance warnings), and the workspace's backups — snapshots, restore,
 * costs, and a typed-confirm purge.
 */
const props = defineProps<{ threadId: string | null }>();

const info = ref<SandboxPanelInfo | null>(null);
const snapshots = ref<BackupSnapshot[] | null>(null);
const costs = ref<BackupCosts | null>(null);
const error = ref<string | null>(null);
const notice = ref<string | null>(null);
const busy = ref(false);
const purgeConfirm = ref("");

/** Bumped on every thread change; an operation from an older epoch publishes nothing. */
let epoch = 0;
/** The in-flight read, cancelled by a thread change. */
let readAbort: AbortController | null = null;
/** Cancels everything on unmount. A thread change does not cancel a restore or purge mid-way. */
const unmounted = new AbortController();

interface Operation {
  threadId: string;
  signal: AbortSignal;
  /** False once the panel has switched to another thread: drop the results. */
  current: () => boolean;
}

/** Runs one operation for the selected thread, holding that thread across its awaits. */
async function run(kind: "read" | "write", action: (op: Operation) => Promise<void>): Promise<void> {
  const threadId = props.threadId;
  if (busy.value || !threadId) return;
  const started = epoch;
  const current = () => started === epoch;
  let signal = unmounted.signal;
  if (kind === "read") {
    readAbort = new AbortController();
    signal = readAbort.signal;
  }
  busy.value = true;
  error.value = null;
  notice.value = null;
  try {
    await action({ threadId, signal, current });
  } catch (e) {
    if (current() && !(e instanceof DOMException && e.name === "AbortError"))
      error.value = e instanceof Error ? e.message : String(e);
  } finally {
    if (current()) busy.value = false;
  }
}

function loadInfo(): Promise<void> {
  return run("read", async ({ threadId, signal, current }) => {
    const value = await getSandboxInfo(threadId, signal);
    if (current()) info.value = value;
  });
}

function loadSnapshots(): Promise<void> {
  return run("read", async ({ threadId, signal, current }) => {
    const value = await listBackupSnapshots(threadId, signal);
    if (current()) snapshots.value = value;
  });
}

function loadCosts(): Promise<void> {
  return run("read", async ({ threadId, signal, current }) => {
    const value = await getBackupCosts(threadId, signal);
    if (current()) costs.value = value;
  });
}

function restore(snapshot: BackupSnapshot): Promise<void> {
  const workspace = info.value?.gatewayWorkspace ?? "this workspace";
  const ok = window.confirm(
    `Restore snapshot ${snapshot.id.slice(0, 8)} (${formatTime(snapshot.time)} UTC) into '${workspace}'?\n\n`
      + "This stops the live sandbox of every conversation on this workspace. Changed files are saved "
      + "aside as an orphan snapshot first.",
  );
  if (!ok) return Promise.resolve();
  return run("write", async ({ threadId, signal, current }) => {
    const result = await restoreBackupSnapshot(threadId, snapshot.id, signal);
    if (!current()) return;
    notice.value = result.restoredSnapshotId
      ? `Restored ${result.restoredSnapshotId.slice(0, 8)}`
        + (result.orphanSnapshotId ? `; previous files saved as ${result.orphanSnapshotId.slice(0, 8)}.` : ".")
      : "The workspace already matched that snapshot; nothing changed.";
    const refreshedInfo = await getSandboxInfo(threadId, signal);
    const refreshedSnapshots = await listBackupSnapshots(threadId, signal);
    if (!current()) return;
    info.value = refreshedInfo;
    snapshots.value = refreshedSnapshots;
  });
}

function purge(): Promise<void> {
  const workspace = info.value?.gatewayWorkspace;
  if (!workspace || purgeConfirm.value !== workspace) return Promise.resolve();
  const ok = window.confirm(
    `PERMANENTLY delete '${workspace}': its sandboxes, every snapshot and all files? This cannot be undone.`,
  );
  if (!ok) return Promise.resolve();
  return run("write", async ({ threadId, signal, current }) => {
    const result = await purgeBackupWorkspace(threadId, workspace, signal);
    if (!current()) return;
    notice.value = `Purged '${result.workspace}': ${plural(result.snapshotsDeleted, "snapshot")} and `
      + `${plural(result.sandboxesDeleted.length, "sandbox", "sandboxes")} deleted.`;
    purgeConfirm.value = "";
    snapshots.value = null;
    const refreshedInfo = await getSandboxInfo(threadId, signal);
    if (current()) info.value = refreshedInfo;
  });
}

/** The gateway's time looks like `2026-10-09T22:00:18.437828115+00:00[UTC]`; show `2026-10-09 22:00:18`. */
function formatTime(time: string): string {
  return time.slice(0, 19).replace("T", " ");
}

function plural(count: number, singular: string, pluralForm = `${singular}s`): string {
  return `${count} ${count === 1 ? singular : pluralForm}`;
}

function formatBytes(bytes: number | null): string {
  if (bytes === null) return "?";
  return bytes < 1024 ? `${bytes} B` : `${(bytes / 1024).toFixed(1)} KiB`;
}

watch(
  () => props.threadId,
  () => {
    // Before the busy gate: the old thread's pending operation must neither publish here nor skip this load.
    epoch++;
    readAbort?.abort();
    busy.value = false;
    error.value = null;
    notice.value = null;
    info.value = null;
    snapshots.value = null;
    costs.value = null;
    purgeConfirm.value = "";
    void loadInfo();
  },
  { immediate: true },
);
onBeforeUnmount(() => {
  readAbort?.abort();
  unmounted.abort();
});
</script>

<template>
  <div class="sandbox-panel" data-testid="sandbox-panel">
    <p v-if="!threadId" class="muted">Start a conversation to see its sandbox.</p>
    <template v-else>
      <p v-if="error" class="error" role="alert" data-testid="sandbox-panel-error">{{ error }}</p>
      <p v-if="notice" class="notice" role="status" data-testid="sandbox-panel-notice">{{ notice }}</p>

      <dl v-if="info" class="facts" data-testid="sandbox-info">
        <dt>Workspace</dt>
        <dd data-testid="sandbox-info-workspace">{{ info.gatewayWorkspace }}</dd>
        <template v-if="info.session">
          <dt>Image</dt>
          <dd data-testid="sandbox-info-image">{{ info.session.image ?? "default" }}</dd>
          <dt>Digest</dt>
          <dd class="mono" data-testid="sandbox-info-digest">{{ info.session.imageDigest ?? "unresolved" }}</dd>
          <dt>Home</dt>
          <dd data-testid="sandbox-info-home">{{ info.session.home ?? "workspace root" }}</dd>
        </template>
      </dl>
      <p v-if="info && !info.session" class="muted" data-testid="sandbox-info-no-session">
        No live sandbox. The next message starts one.
      </p>
      <ul
        v-if="info?.session?.imageWarnings.length"
        class="warnings"
        data-testid="sandbox-image-warnings"
      >
        <li v-for="warning in info.session.imageWarnings" :key="warning">{{ warning }}</li>
      </ul>

      <div class="actions">
        <button :disabled="busy" data-testid="sandbox-refresh" @click="loadInfo">Refresh</button>
        <button :disabled="busy" data-testid="sandbox-snapshots-load" @click="loadSnapshots">Snapshots</button>
        <button :disabled="busy" data-testid="sandbox-costs-load" @click="loadCosts">Costs</button>
      </div>

      <OperatorSecretField v-if="snapshots || info" purpose="restore, purge" />

      <template v-if="snapshots">
        <p v-if="!snapshots.length" class="muted" data-testid="sandbox-snapshots-empty">No snapshots yet.</p>
        <ul v-else class="snapshots" data-testid="sandbox-snapshots">
          <li v-for="snapshot in snapshots" :key="snapshot.id" :data-testid="`sandbox-snapshot-${snapshot.id}`">
            <span class="mono">{{ snapshot.id.slice(0, 8) }}</span>
            <span class="muted">{{ formatTime(snapshot.time) }}</span>
            <span class="muted">
              {{ snapshot.files === null ? "? files" : plural(snapshot.files, "file") }}, {{ formatBytes(snapshot.bytes) }}
            </span>
            <span v-if="snapshot.orphan" class="tag">orphan</span>
            <span v-if="snapshot.partial" class="tag">partial</span>
            <button
              :disabled="busy"
              :data-testid="`sandbox-snapshot-restore-${snapshot.id}`"
              @click="restore(snapshot)"
            >Restore</button>
          </li>
        </ul>
      </template>

      <table v-if="costs" class="costs" data-testid="sandbox-costs">
        <caption>
          Backup cost estimate: ${{ costs.totalEstimatedCostUsd.toFixed(6) }}
        </caption>
        <thead>
          <tr><th>Workspace</th><th>Runs</th><th>Up</th><th>USD</th></tr>
        </thead>
        <tbody>
          <tr v-for="row in costs.groups" :key="row.group ?? '(app)'">
            <td>{{ row.group ?? "(app)" }}</td>
            <td>{{ row.runs }}</td>
            <td>{{ formatBytes(row.bytesUp) }}</td>
            <td>{{ row.estimatedCostUsd.toFixed(6) }}</td>
          </tr>
        </tbody>
      </table>

      <details v-if="info" class="danger">
        <summary>Purge workspace</summary>
        <p>Permanently deletes the workspace's sandboxes, snapshots and files.</p>
        <label>
          Type <span class="mono">{{ info.gatewayWorkspace }}</span> to confirm
          <input v-model="purgeConfirm" data-testid="sandbox-purge-confirm" type="text" />
        </label>
        <button
          :disabled="busy || purgeConfirm !== info.gatewayWorkspace"
          data-testid="sandbox-purge"
          @click="purge"
        >Purge</button>
      </details>
    </template>
  </div>
</template>

<style scoped>
.sandbox-panel { font-size: 13px; color: #334155; }
.facts { display: grid; grid-template-columns: max-content 1fr; gap: 4px 10px; margin: 0 0 8px; }
.facts dt { color: #64748b; }
.facts dd { margin: 0; min-width: 0; overflow-wrap: anywhere; }
.mono { font-family: ui-monospace, monospace; font-size: 12px; }
.muted { color: #64748b; }
.error { color: #b42318; margin: 0 0 8px; }
.notice { color: #166534; margin: 0 0 8px; }
.warnings { margin: 0 0 8px; padding-left: 18px; color: #92400e; }
.actions { display: flex; gap: 6px; flex-wrap: wrap; margin: 8px 0; }
button { border: 1px solid #b9cbe2; border-radius: 5px; background: #fff; color: #275188; padding: 4px 9px; cursor: pointer; }
button:disabled { opacity: 0.5; cursor: default; }
.snapshots { list-style: none; margin: 0; padding: 0; }
.snapshots li { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; padding: 5px 0; border-top: 1px solid #e2e6eb; }
.snapshots li button { margin-left: auto; }
.tag { border: 1px solid #cbd1d8; border-radius: 4px; padding: 0 4px; font-size: 11px; }
.costs { width: 100%; border-collapse: collapse; margin: 8px 0; }
.costs caption { text-align: left; color: #64748b; padding-bottom: 4px; }
.costs th, .costs td { text-align: left; padding: 3px 4px; border-top: 1px solid #e2e6eb; overflow-wrap: anywhere; }
.danger { margin-top: 10px; border: 1px solid #f2c4c0; border-radius: 6px; padding: 6px 8px; }
.danger summary { color: #b42318; cursor: pointer; }
.danger label { display: flex; flex-direction: column; gap: 4px; margin: 6px 0; }
.danger input { border: 1px solid #cbd1d8; border-radius: 4px; padding: 4px 6px; }
.danger button { border-color: #f2c4c0; color: #b42318; }
</style>
