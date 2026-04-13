-- Find the most recent RunAssignment messages and their context
SELECT "@t", Message, ClientComponent
FROM read_json_auto('samples/LmStreaming.Sample/logs/lmstreaming-20251222.jsonl')
WHERE Message IN ('Run assignment received', 'Activated pending message', 'Added message to pending queue')
ORDER BY "@t" DESC
LIMIT 20;
