-- DuckDB query pack for Codex internal tool surfacing diagnostics.
-- Expected input: structured application logs with event_type/event_status fields.

-- 1) Internal tool calls without a terminal result.
WITH calls AS (
    SELECT
        properties->>'tool_call_id' AS tool_call_id,
        properties->>'tool_name' AS tool_name,
        min(timestamp) AS first_seen_at
    FROM logs
    WHERE properties->>'event_type' = 'codex.internal_tool.call.emitted'
    GROUP BY 1, 2
),
results AS (
    SELECT DISTINCT properties->>'tool_call_id' AS tool_call_id
    FROM logs
    WHERE properties->>'event_type' = 'codex.internal_tool.result.emitted'
)
SELECT c.*
FROM calls c
LEFT JOIN results r USING (tool_call_id)
WHERE r.tool_call_id IS NULL
ORDER BY c.first_seen_at DESC;

-- 2) Duplicate suppression count by source event.
SELECT
    properties->>'source_event' AS source_event,
    properties->>'tool_name' AS tool_name,
    count(*) AS duplicate_count
FROM logs
WHERE properties->>'event_type' = 'codex.internal_tool.duplicate_ignored'
GROUP BY 1, 2
ORDER BY duplicate_count DESC;

-- 3) web_search latency distribution.
SELECT
    approx_quantile(CAST(properties->>'latency_ms' AS DOUBLE), 0.50) AS p50_ms,
    approx_quantile(CAST(properties->>'latency_ms' AS DOUBLE), 0.90) AS p90_ms,
    approx_quantile(CAST(properties->>'latency_ms' AS DOUBLE), 0.99) AS p99_ms
FROM logs
WHERE properties->>'event_type' = 'codex.internal_tool.result.emitted'
  AND properties->>'tool_name' = 'web_search';

-- 4) Tool-call visibility coverage by type.
SELECT
    properties->>'tool_name' AS tool_name,
    sum(CASE WHEN properties->>'event_type' = 'codex.internal_tool.call.emitted' THEN 1 ELSE 0 END) AS call_events,
    sum(CASE WHEN properties->>'event_type' = 'codex.internal_tool.result.emitted' THEN 1 ELSE 0 END) AS result_events
FROM logs
WHERE properties->>'event_type' IN ('codex.internal_tool.call.emitted', 'codex.internal_tool.result.emitted')
GROUP BY 1
ORDER BY tool_name;
