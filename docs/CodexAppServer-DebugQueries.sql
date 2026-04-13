-- Codex App Server debug query pack for DuckDB.
-- Usage example:
-- duckdb -c "INSTALL json; LOAD json; <paste query>" 

-- 1) Runs started without terminal completion/failure (by request id)
WITH logs AS (
  SELECT *
  FROM read_json_auto('samples/LmStreaming.Sample/bin/Debug/net9.0/logs/lmstreaming-*.jsonl')
  WHERE provider_mode = 'codex'
)
SELECT
  bridge_request_id,
  SUM(CASE WHEN event_type = 'codex.bridge.run.started' THEN 1 ELSE 0 END) AS started_count,
  SUM(CASE WHEN event_type = 'codex.bridge.run.completed' THEN 1 ELSE 0 END) AS completed_count,
  SUM(CASE WHEN event_type = 'codex.bridge.run.failed' THEN 1 ELSE 0 END) AS failed_count,
  MIN("@t") AS first_seen,
  MAX("@t") AS last_seen
FROM logs
WHERE bridge_request_id IS NOT NULL
GROUP BY bridge_request_id
HAVING started_count > 0 AND completed_count = 0 AND failed_count = 0
ORDER BY first_seen;

-- 2) Notification drop analysis for turn mismatches
SELECT
  "@t",
  event_type,
  event_status,
  method,
  reason,
  active_turn_id,
  event_turn_id
FROM read_json_auto('samples/LmStreaming.Sample/bin/Debug/net9.0/logs/lmstreaming-*.jsonl')
WHERE provider_mode = 'codex'
  AND event_type = 'codex.app_server.notification'
  AND event_status = 'dropped'
ORDER BY "@t";

-- 3) Tool lifecycle completeness (MCP + dynamic)
SELECT
  event_type,
  COUNT(*) AS count
FROM read_json_auto('samples/LmStreaming.Sample/bin/Debug/net9.0/logs/lmstreaming-*.jsonl')
WHERE provider_mode = 'codex'
  AND event_type IN (
    'codex.tool.started',
    'codex.tool.completed',
    'codex.tool.failed',
    'codex.dynamic_tool.requested',
    'codex.dynamic_tool.completed',
    'codex.dynamic_tool.denied',
    'codex.dynamic_tool.execution')
GROUP BY event_type
ORDER BY count DESC;

-- 4) Timeout-triggered recovery verification
SELECT
  t.bridge_request_id,
  t.turn_id,
  t.timeout_ms,
  t."@t" AS timeout_ts,
  i."@t" AS interrupt_ts,
  COALESCE(c."@t", f."@t") AS terminal_ts,
  c.event_type AS completed_event,
  f.event_type AS failed_event
FROM read_json_auto('samples/LmStreaming.Sample/bin/Debug/net9.0/logs/lmstreaming-*.jsonl') t
LEFT JOIN read_json_auto('samples/LmStreaming.Sample/bin/Debug/net9.0/logs/lmstreaming-*.jsonl') i
  ON i.bridge_request_id = t.bridge_request_id
  AND i.event_type = 'codex.app_server.interrupt'
LEFT JOIN read_json_auto('samples/LmStreaming.Sample/bin/Debug/net9.0/logs/lmstreaming-*.jsonl') c
  ON c.bridge_request_id = t.bridge_request_id
  AND c.event_type = 'codex.bridge.run.completed'
LEFT JOIN read_json_auto('samples/LmStreaming.Sample/bin/Debug/net9.0/logs/lmstreaming-*.jsonl') f
  ON f.bridge_request_id = t.bridge_request_id
  AND f.event_type = 'codex.bridge.run.failed'
WHERE t.provider_mode = 'codex'
  AND t.event_type = 'codex.turn.timeout'
ORDER BY timeout_ts;
