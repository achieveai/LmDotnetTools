# todo-eval sweep summary

10 runs (2 models x 5 seeds).

## Per model

| Model | Runs | Valid | Completed | Timed out | Avg turns | Task tool calls | Task tool errors | Error rate | Retry storms | Unpaired |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| deepseek-v4-flash | 5 | 5/5 | 5/5 | 0 | 133.0 | 420 | 5 | 1.2 % | 0 | 0 |
| gpt-5.6-luna | 5 | 4/5 | 3/5 | 0 | 106.8 | 305 | 3 | 1.0 % | 0 | 0 |

## Error rate per tool (per model)

Zero-call tools are omitted here; runs.jsonl carries every tool's row.

| Model | Tool | Calls | Errors | Error rate |
|---|---|---:|---:|---:|
| deepseek-v4-flash | add-task | 10 | 0 | 0.0 % |
| deepseek-v4-flash | bulk-initialize | 5 | 0 | 0.0 % |
| deepseek-v4-flash | update-task | 79 | 4 | 5.1 % |
| deepseek-v4-flash | claim-task | 79 | 0 | 0.0 % |
| deepseek-v4-flash | assign-task | 9 | 1 | 11.1 % |
| deepseek-v4-flash | block-task | 5 | 0 | 0.0 % |
| deepseek-v4-flash | get-task | 39 | 0 | 0.0 % |
| deepseek-v4-flash | add-note | 80 | 0 | 0.0 % |
| deepseek-v4-flash | edit-note | 2 | 0 | 0.0 % |
| deepseek-v4-flash | list-notes | 45 | 0 | 0.0 % |
| deepseek-v4-flash | list-tasks | 67 | 0 | 0.0 % |
| gpt-5.6-luna | add-task | 6 | 0 | 0.0 % |
| gpt-5.6-luna | bulk-initialize | 5 | 0 | 0.0 % |
| gpt-5.6-luna | update-task | 96 | 3 | 3.1 % |
| gpt-5.6-luna | claim-task | 58 | 0 | 0.0 % |
| gpt-5.6-luna | block-task | 5 | 0 | 0.0 % |
| gpt-5.6-luna | get-task | 29 | 0 | 0.0 % |
| gpt-5.6-luna | add-note | 72 | 0 | 0.0 % |
| gpt-5.6-luna | list-tasks | 34 | 0 | 0.0 % |

## Retry storms

None detected.

## Validity

| Run | Sub-agent threads | Without task-tool calls | Fabricated-compliance suspects |
|---|---:|---|---|
| gpt-5.6-luna/seed4 | 3 | subagent-106b632f8185-0fa7a0a7, subagent-1f48a01b711d-0fa7a0a7, subagent-a65c30565450-0fa7a0a7 | subagent-106b632f8185-0fa7a0a7, subagent-1f48a01b711d-0fa7a0a7, subagent-a65c30565450-0fa7a0a7 |

## Unattributed threads

None: every conversation thread is reachable from a run via its sample.subAgentOf chain.
