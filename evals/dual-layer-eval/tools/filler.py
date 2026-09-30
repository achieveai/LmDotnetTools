"""Seeded boilerplate for the synthetic dual-layer-eval corpora (fw1, ag2).

Everything here is deliberately USELESS to every question: agenda chatter, template sections,
status tables, policy legalese. It must never state a fact a question depends on, so the word
banks avoid the vocabulary the planted facts use (pager / on-call / rota, ownership, reporting
lines, units / cases, exclusions, cut-offs). A reader that skims this text loses nothing; a
reader that pastes all of it into the planner's context pays for it.
"""
import random

TOPICS = [
    "the Q2 capacity plan", "dashboard hygiene", "the vendor renewal for the label printers",
    "flaky integration tests", "the new expense tool", "badge access for the Tilbury annex",
    "the dependency upgrade backlog", "log retention settings", "the quarterly architecture review",
    "the intern onboarding plan", "ticket triage labels", "the laptop refresh", "canary analysis",
    "the shared calendar for demos", "desk moves on the third floor", "the design-doc template",
    "SLO wording for customer contracts", "the offsite agenda", "cost tagging for cloud accounts",
    "the internal wiki search", "doc review turnaround", "the hiring loop rubric",
]
VERBS = ["walked through", "flagged", "asked about", "gave an update on", "raised", "summarised",
         "proposed parking", "reported progress on", "questioned the scope of", "circled back on"]
OUTCOMES = [
    "No decision needed; revisit next week.", "Parked until the numbers are in.",
    "Agreed to take it offline.", "Nothing blocking; carry on as planned.",
    "Consensus was to wait for the vendor's reply.", "Slides to be shared in the channel.",
    "Needs a short design note before anyone commits time.", "Deferred to the next planning cycle.",
    "Follow-up thread to be opened in the team channel.", "Will be covered at the all-hands instead.",
]
FIRST = ["Asha", "Bruno", "Celine", "Dario", "Edda", "Filip", "Greer", "Hollis", "Ivo", "Jana",
         "Kenji", "Lotte", "Mateus", "Nadia", "Orla", "Pavel", "Quinn", "Rhea", "Soren", "Talia"]
POLICY = [
    "This document is reviewed annually and whenever a material change to the business requires it.",
    "Questions about interpretation should be raised with the document's editor in the first instance.",
    "Nothing in this document overrides applicable law or the terms of an individual contract.",
    "Where this document conflicts with a local procedure, the local procedure should be updated to match.",
    "Exceptions must be requested in writing and are granted for a fixed period only.",
    "All staff are expected to have read this document and to apply it in their day-to-day work.",
    "Records created under this document are retained according to the records schedule.",
    "Training material that accompanies this document is available on the learning portal.",
    "Feedback on the clarity of this document is welcome at any time through the usual channel.",
    "Previous versions remain available in the document history for audit purposes.",
    "This section is informative and does not introduce new obligations.",
    "Terms in bold are defined in the glossary at the end of the handbook.",
    "Approval workflows described here run in the ticketing system and leave an audit trail by default.",
    "Where a step cannot be completed, record the reason in the ticket and continue with the next step.",
    "Templates referenced in this section live in the shared drive under the standard folder layout.",
    "The document editor confirms each year that the contact details in this document are current.",
    "Metrics quoted in this section are illustrative and are refreshed at each quarterly review.",
    "Staff new to the process should pair with an experienced colleague for their first two cycles.",
    "Any personal data handled under this process is subject to the data-protection handbook.",
    "Links in this section point to the internal mirror; external copies may be out of date.",
    "The wording of this section was simplified in the last revision without changing its meaning.",
    "Readers outside the core audience can skip this section without losing context.",
    "Teams should budget time for the periodic review described here in their planning cycle.",
    "The checklist in the appendix is a convenience and is not exhaustive.",
]
CHATTER = [
    "Coffee machine on four is fixed again, thanks to facilities.",
    "Reminder that the demo day slot sign-up closes on Friday.",
    "Someone left a blue umbrella in meeting room Estuary; it is at reception.",
    "The team photo is being retaken because half of us were out last time.",
    "Please update your status in the directory if you are travelling.",
    "The wifi in the east wing drops on the hour; IT have a ticket open.",
    "Thanks to everyone who joined the charity run; we raised a respectable amount.",
    "New starters: the buddy list is pinned in the channel.",
    "Lunch-and-learn next week is on accessibility in internal tools.",
    "Please stop booking the big room for one-person calls.",
]
STATUS = ["on track", "at risk", "done", "not started", "blocked on review", "in progress"]
METRICS = ["p50 latency", "p99 latency", "error budget burn", "deploys", "open tickets", "build time",
           "test flake rate", "cost per 1k requests", "queue depth", "cache hit ratio"]


class Filler:
    def __init__(self, rng: random.Random):
        self.r = rng

    def name(self):
        return self.r.choice(FIRST)

    def agenda_item(self):
        r = self.r
        return (f"- {self.name()} {r.choice(VERBS)} {r.choice(TOPICS)}. "
                f"{r.choice(['Some discussion about timing.', 'Brief.', 'Took longer than planned.', 'Two questions from the floor.', ''])} "
                f"{r.choice(OUTCOMES)}").rstrip()

    def agenda(self, n=None):
        n = n or self.r.randint(4, 9)
        return "\n".join(self.agenda_item() for _ in range(n))

    def chatter(self, n=None):
        n = n or self.r.randint(1, 3)
        return "\n".join(f"- {c}" for c in self.r.sample(CHATTER, n))

    def policy(self, n=None):
        n = n or self.r.randint(3, 6)
        return " ".join(self.r.sample(POLICY, n))

    def status_table(self, rows=None):
        r = self.r
        rows = rows or r.randint(4, 8)
        out = ["| Workstream | Status | Notes |", "|---|---|---|"]
        for _ in range(rows):
            out.append(f"| {r.choice(TOPICS).capitalize()} | {r.choice(STATUS)} | {r.choice(OUTCOMES)} |")
        return "\n".join(out)

    def metric_table(self, rows=None):
        r = self.r
        rows = rows or r.randint(4, 7)
        out = ["| Metric | Last period | This period | Target |", "|---|---|---|---|"]
        for m in r.sample(METRICS, rows):
            a = r.randint(10, 900)
            b = max(1, a + r.randint(-60, 60))
            out.append(f"| {m} | {a} | {b} | {r.randint(10, 900)} |")
        return "\n".join(out)

    def actions(self, n=None):
        n = n or self.r.randint(2, 5)
        return "\n".join(f"- [ ] {self.name()}: {self.r.choice(['draft', 'review', 'circulate', 'close out', 'estimate'])} "
                         f"{self.r.choice(TOPICS)}" for _ in range(n))

    def pad(self, n):
        return "\n\n".join(self.template_sections() for _ in range(n))

    def template_sections(self):
        r = self.r
        heads = r.sample(["Purpose", "Scope", "Background", "Principles", "Review cadence", "Glossary",
                          "Related documents", "Revision history", "Out of scope", "Assumptions"], r.randint(3, 5))
        return "\n\n".join(f"## {h}\n\n{self.policy()}" for h in heads)
