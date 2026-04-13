SELECT "@t", Message, ClientData
FROM read_json_auto('samples/LmStreaming.Sample/logs/lmstreaming-20251222.jsonl')
WHERE Message = 'Added message to pending queue'
  AND "@t" BETWEEN '2025-12-23T02:36:40' AND '2025-12-23T02:37:00'
ORDER BY "@t" DESC;



