-- Check for all activity around the 03:35:59 - 03:36:04 timeframe
SELECT "@t", Message, SourceContext, ClientComponent
FROM read_json_auto('samples/LmStreaming.Sample/logs/lmstreaming-20251222.jsonl')
WHERE "@t" BETWEEN '2025-12-23T03:35:58' AND '2025-12-23T03:36:10'
ORDER BY "@t" ASC;


