-- Look for server-side message reception logs after 03:36:04
SELECT "@t", Message, SourceContext
FROM read_json_auto('samples/LmStreaming.Sample/logs/lmstreaming-20251222.jsonl')
WHERE "@t" >= '2025-12-23T03:36:04'
  AND (SourceContext LIKE '%ChatWebSocketManager%' OR SourceContext LIKE '%MultiTurnAgentLoop%')
  AND Message IS NOT NULL
ORDER BY "@t" ASC
LIMIT 30;


