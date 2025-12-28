SELECT "@t", Message, ClientData
FROM read_json_auto('samples/LmStreaming.Sample/logs/lmstreaming-20251222.jsonl')
WHERE Message IN ('Run assignment received', 'Activated pending message', 'More inputIds than pending messages')
ORDER BY "@t" DESC;



