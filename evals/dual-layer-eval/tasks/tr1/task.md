# tr1 — judge every ticket against a subtle policy, then aggregate (synthetic)

Family `judged-aggregation`, split `dev`. Workspace = `fixtures/`: 157 support tickets of a fictional hosting
company (400-1200 tokens each), its service-credit policy (six clauses that interact, one amended by an appendix) and
its maintenance calendar; 0.56 MB, ~140k tokens. No keyword identifies a ticket's class: every
class uses the same vocabulary, and the class shows only in what the narrative says happened. 8 questions aggregate over
judged tickets (counts, id lists, an argmax), each changed by at least two clause misreadings. Checker: integer /
normalised name / id set; score = correct / 8. Trap answers and predicted trap-only scores: `hidden/build-report.json`.
Generator + knobs: `hidden/gen_tickets.py`, `hidden/render.py`, `hidden/build.json`.

---

Project code name: {SEED}.

This workspace is an export of the first-quarter 2026 support tickets of Tamberlow Hosting, a (fictional) managed hosting
company, for its quarterly service-credit review. Nothing in it is about a real organisation or person, so the
answers are only in these files; general knowledge will not help.

- `policy/service-credit-policy.md` — the policy the review applies. Its terms (Qualifying Incident, Qualifying
  Minutes and so on) are used in the questions exactly as the policy defines them.
- `status/maintenance-calendar-q1-2026.md` — the maintenance section of the status page.
- `tickets/` — 157 tickets, one Markdown file each. Ticket titles are written by customers.

Use the file and shell tools (Glob, Grep, Read, Bash) to read what you need.

Write `answers.json` in the workspace root:

```json
{
  "q1": { "answer": 12, "evidence": ["tickets/T-4105.md", "..."] },
  "q2": { "answer": ["T-4105", "T-4230"], "evidence": ["..."] }
}
```

- A count or total is a whole number; a customer is the account name as written in the ticket header; a list of
  tickets is a JSON array of ticket ids (order does not matter, and every id must be right).
- `evidence` lists the files the answer rests on; it is optional and not scored.
- Include every question from q1 to q8. If you cannot pin one down, give your best guess anyway.

Questions:

- **q1.** How many Qualifying Incidents do these tickets record?
- **q2.** List the ticket ids of the Qualifying Incidents of Enterprise-plan accounts whose Qualifying Minutes were fewer than 30 (one id per incident: the ticket the policy counts).
- **q3.** Which customer account has the most Qualifying Incidents?
- **q4.** How many Qualifying Incidents were caused by a failure at an outside service provider (not in Tamberlow's own systems and not a change the customer made)?
- **q5.** What is the total number of Qualifying Minutes across all Qualifying Incidents of Pellshaw Joinery?
- **q6.** How many Qualifying Incidents began in February 2026?
- **q7.** List the ticket ids of the Qualifying Incidents that were triggered by a change the customer made on its own side (one id per incident: the ticket the policy counts).
- **q8.** How many different customer accounts had at least one Qualifying Incident?
