import readline from "node:readline";
import { Codex } from "@openai/codex-sdk";

let codex = null;
let thread = null;
let initialized = false;
let runInProgress = false;

const rl = readline.createInterface({
  input: process.stdin,
  crlfDelay: Infinity,
});

const log = (level, payload) => {
  const envelope = {
    level,
    provider: "codex",
    timestamp: new Date().toISOString(),
    ...payload,
  };
  process.stderr.write(`${JSON.stringify(envelope)}\n`);
};

const write = (payload) => {
  process.stdout.write(`${JSON.stringify(payload)}\n`);
};

const buildConfigOverrides = (options) => {
  const config = {};
  if (options.approval_policy) {
    config.approval_policy = options.approval_policy;
  }
  if (options.web_search_mode) {
    config.web_search = options.web_search_mode;
  }
  config.sandbox_workspace_write = {
    network_access: !!options.network_access_enabled,
  };

  if (options.mcp_servers) {
    config.mcp_servers = options.mcp_servers;
  }

  return config;
};

const buildThreadOptions = (options) => {
  const threadOptions = {
    model: options.model,
    sandboxMode: options.sandbox_mode,
    workingDirectory: options.working_directory,
    skipGitRepoCheck: !!options.skip_git_repo_check,
    networkAccessEnabled: !!options.network_access_enabled,
    webSearchMode: options.web_search_mode,
    approvalPolicy: options.approval_policy,
    additionalDirectories: options.additional_directories ?? undefined,
  };

  return threadOptions;
};

const ensureInit = (requestId) => {
  if (!initialized || !thread) {
    write({
      type: "fatal",
      request_id: requestId,
      error: "Bridge is not initialized.",
      error_code: "not_initialized",
    });
    return false;
  }

  return true;
};

const handleInit = async (message) => {
  const { request_id: requestId, options } = message;

  try {
    const codexOptions = {
      apiKey: options?.api_key ?? undefined,
      baseUrl: options?.base_url ?? undefined,
      config: buildConfigOverrides(options ?? {}),
    };

    codex = new Codex(codexOptions);

    const threadOptions = buildThreadOptions(options ?? {});

    if (options?.thread_id) {
      thread = codex.resumeThread(options.thread_id, threadOptions);
    } else {
      thread = codex.startThread(threadOptions);
    }

    initialized = true;

    log("info", {
      event_type: "codex.bridge.ready",
      event_status: "completed",
      bridge_request_id: requestId,
    });

    write({ type: "ready", request_id: requestId });
  } catch (error) {
    log("error", {
      event_type: "codex.bridge.start",
      event_status: "failed",
      bridge_request_id: requestId,
      error_code: "init_failed",
      exception_type: error?.name ?? "Error",
      message: `${error}`,
    });

    write({
      type: "fatal",
      request_id: requestId,
      error: `${error}`,
      error_code: "init_failed",
    });
  }
};

const handleRun = async (message) => {
  const requestId = message.request_id;
  const input = message.input;

  if (!ensureInit(requestId)) {
    return;
  }

  if (runInProgress) {
    write({
      type: "run_failed",
      request_id: requestId,
      error: "A run is already in progress.",
      error_code: "run_already_in_progress",
    });
    return;
  }

  runInProgress = true;
  const start = Date.now();

  try {
    log("info", {
      event_type: "codex.bridge.run.started",
      event_status: "started",
      bridge_request_id: requestId,
      codex_thread_id: thread.id,
    });

    const streamed = await thread.runStreamed(input);

    for await (const event of streamed.events) {
      write({
        type: "event",
        request_id: requestId,
        event,
      });
    }

    log("info", {
      event_type: "codex.bridge.run.completed",
      event_status: "completed",
      bridge_request_id: requestId,
      codex_thread_id: thread.id,
      latency_ms: Date.now() - start,
    });

    write({
      type: "run_completed",
      request_id: requestId,
      thread_id: thread.id,
    });
  } catch (error) {
    log("error", {
      event_type: "codex.bridge.run.failed",
      event_status: "failed",
      bridge_request_id: requestId,
      codex_thread_id: thread?.id,
      error_code: "run_failed",
      exception_type: error?.name ?? "Error",
      message: `${error}`,
      latency_ms: Date.now() - start,
    });

    write({
      type: "run_failed",
      request_id: requestId,
      error: `${error}`,
      error_code: "run_failed",
      thread_id: thread?.id,
    });
  } finally {
    runInProgress = false;
  }
};

const handleShutdown = async (message) => {
  const requestId = message.request_id;

  log("info", {
    event_type: "codex.bridge.shutdown",
    event_status: "completed",
    bridge_request_id: requestId,
    codex_thread_id: thread?.id,
  });

  write({ type: "shutdown", request_id: requestId });
  rl.close();
};

rl.on("line", async (line) => {
  if (!line || !line.trim()) {
    return;
  }

  let message;
  try {
    message = JSON.parse(line);
  } catch (error) {
    write({
      type: "fatal",
      error: `Invalid JSON request: ${error}`,
      error_code: "invalid_json",
    });
    return;
  }

  switch (message.type) {
    case "init":
      await handleInit(message);
      break;
    case "run":
      await handleRun(message);
      break;
    case "shutdown":
      await handleShutdown(message);
      break;
    default:
      write({
        type: "fatal",
        request_id: message.request_id,
        error: `Unsupported message type '${message.type}'`,
        error_code: "unsupported_message_type",
      });
      break;
  }
});

rl.on("close", () => {
  process.exit(0);
});
