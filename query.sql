SELECT "@t", "@l", SourceContext, Message, ClientData
FROM read_json_auto('samples/LmStreaming.Sample/logs/lmstreaming-20251222.jsonl')
WHERE Message LIKE '%pending queue%'
ORDER BY "@t" DESC;
