# todo-eval sweep summary

10 runs (2 models x 5 seeds).

## Per model

| Model | Runs | Valid | Completed | Timed out | Avg turns | Task tool calls | Task tool errors | Error rate | Retry storms | Unpaired |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| deepseek-v4-flash | 5 | 5/5 | 5/5 | 0 | 128.2 | 413 | 5 | 1.2 % | 0 | 0 |
| gpt-5.6-luna | 5 | 4/5 | 3/5 | 0 | 170.0 | 625 | 260 | 41.6 % | 14 | 0 |

## Error rate per tool (per model)

Zero-call tools are omitted here; runs.jsonl carries every tool's row.

| Model | Tool | Calls | Errors | Error rate |
|---|---|---:|---:|---:|
| deepseek-v4-flash | add-task | 10 | 0 | 0.0 % |
| deepseek-v4-flash | bulk-initialize | 5 | 0 | 0.0 % |
| deepseek-v4-flash | update-task | 80 | 5 | 6.2 % |
| deepseek-v4-flash | claim-task | 75 | 0 | 0.0 % |
| deepseek-v4-flash | assign-task | 18 | 0 | 0.0 % |
| deepseek-v4-flash | block-task | 5 | 0 | 0.0 % |
| deepseek-v4-flash | get-task | 35 | 0 | 0.0 % |
| deepseek-v4-flash | add-note | 79 | 0 | 0.0 % |
| deepseek-v4-flash | list-notes | 45 | 0 | 0.0 % |
| deepseek-v4-flash | list-tasks | 61 | 0 | 0.0 % |
| gpt-5.6-luna | add-task | 6 | 0 | 0.0 % |
| gpt-5.6-luna | bulk-initialize | 5 | 0 | 0.0 % |
| gpt-5.6-luna | update-task | 107 | 31 | 29.0 % |
| gpt-5.6-luna | claim-task | 94 | 0 | 0.0 % |
| gpt-5.6-luna | assign-task | 3 | 0 | 0.0 % |
| gpt-5.6-luna | block-task | 5 | 0 | 0.0 % |
| gpt-5.6-luna | delete-task | 2 | 0 | 0.0 % |
| gpt-5.6-luna | get-task | 74 | 8 | 10.8 % |
| gpt-5.6-luna | add-note | 292 | 221 | 75.7 % |
| gpt-5.6-luna | list-tasks | 37 | 0 | 0.0 % |

## Retry storms

| Run | Thread | Tool | Consecutive failures | Args |
|---|---|---|---:|---|
| gpt-5.6-luna/seed3 | subagent-bf86863fccfa-596a4f10 | add-note | 72 | {"noteText":"Version bump simulation complete.","subtaskId":0,"taskId":"3.1"} |
| gpt-5.6-luna/seed3 | subagent-bf86863fccfa-596a4f10 | add-note | 24 | {"noteText":"Completed simulated version bump for willow (1.4.2 to 1.4.3).","... |
| gpt-5.6-luna/seed3 | subagent-9ad249c09a69-596a4f10 | add-note | 15 | {"noteText":"Clean build simulation passed on willow release branch; compiler... |
| gpt-5.6-luna/seed3 | subagent-bf86863fccfa-596a4f10 | add-note | 13 | {"noteText":"Version bump simulation complete: willow metadata advanced from ... |
| gpt-5.6-luna/seed1 | subagent-4b10b7d2c29f-78d45a8b | add-note | 11 | {"noteText":"All Packaging simulation steps (version bump, build, and publish... |
| gpt-5.6-luna/seed3 | subagent-bf86863fccfa-596a4f10 | add-note | 11 | {"noteText":"Completed simulated version bump for willow (1.4.2 \u2192 1.4.3)... |
| gpt-5.6-luna/seed1 | subagent-4b10b7d2c29f-78d45a8b | update-task | 10 | {"agent":"","status":"completed","taskId":"3.1"} |
| gpt-5.6-luna/seed1 | subagent-c823cadf5959-78d45a8b | add-note | 8 | {"noteText":"All three documentation deliverables are complete: changelog and... |
| gpt-5.6-luna/seed3 | subagent-9ad249c09a69-596a4f10 | add-note | 5 | {"noteText":"Build passed.","subtaskId":0,"taskId":"1.1"} |
| gpt-5.6-luna/seed3 | subagent-bf86863fccfa-596a4f10 | add-note | 5 | {"noteText":"Version bump simulation complete: willow metadata advanced from ... |
| gpt-5.6-luna/seed3 | subagent-9ad249c09a69-596a4f10 | add-note | 4 | {"noteText":"Clean build simulation passed on willow release branch; compiler... |
| gpt-5.6-luna/seed3 | subagent-bf86863fccfa-596a4f10 | add-note | 4 | {"noteText":"Version bump simulation completed: willow metadata advanced from... |
| gpt-5.6-luna/seed3 | subagent-bf86863fccfa-596a4f10 | add-note | 3 | {"noteText":"Version bump simulation completed: willow metadata advanced from... |
| gpt-5.6-luna/seed3 | subagent-bf86863fccfa-596a4f10 | add-note | 3 | {"noteText":"Version bump simulation complete.","subtaskId":1,"taskId":"3.1"} |

## Validity

| Run | Sub-agent threads | Without task-tool calls | Fabricated-compliance suspects |
|---|---:|---|---|
| gpt-5.6-luna/seed4 | 3 | subagent-99822bf856d9-3d526a62, subagent-cd25101469f9-3d526a62, subagent-df0929ab9d83-3d526a62 | subagent-99822bf856d9-3d526a62, subagent-cd25101469f9-3d526a62, subagent-df0929ab9d83-3d526a62 |

## Unattributed threads

None: every conversation thread is reachable from a run via its sample.subAgentOf chain.
