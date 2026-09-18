"""Deterministic fixture generator for m1 (run once; the generated files are committed).

Writes fixtures/specs/NN-<component>.md (8 component specifications, ~4.5k words each) that the lead
must read in full, fixtures/workstreams/<name>/brief.md (3 worker briefs, ~1.8k words each) that each
worker must read in full, and hidden/keys.json:

  gates:    spec file -> the one ACTIVE release gate id it declares ("Release gate GATE-nn: ...", exactly
            once per file). Each spec also names one or two RETIRED/superseded gate ids as decoys.
  handoffs: workstream -> {code, void}. The brief states "your handoff code for this cycle is
            HANDOFF-<name>-XXXX" exactly once and names a VOID code from the previous cycle as a decoy.

Template prose: readable, repetitive, no apostrophes. Usage: python gen_specs.py  (from tasks/m1/hidden)
"""
import json
import os
import random
import re

SEED = 20260916
HERE = os.path.dirname(os.path.abspath(__file__))
FIX = os.path.join(HERE, "..", "fixtures")
SPEC_WORDS = 4500
BRIEF_WORDS = 1800

# (file stem, component, active gate, retired gates)
SPECS = [
    ("01-gateway", "gateway", "GATE-17", ["GATE-9"]),
    ("02-storage", "storage layer", "GATE-23", ["GATE-11", "GATE-4"]),
    ("03-pipeline", "build pipeline", "GATE-31", ["GATE-12"]),
    ("04-scheduler", "scheduler", "GATE-38", ["GATE-19", "GATE-2"]),
    ("05-identity", "identity service", "GATE-42", ["GATE-26"]),
    ("06-telemetry", "telemetry collector", "GATE-49", ["GATE-33"]),
    ("07-billing", "billing engine", "GATE-56", ["GATE-7", "GATE-41"]),
    ("08-notifications", "notification hub", "GATE-63", ["GATE-58"]),
]
GATE_TEXT = {
    "GATE-17": "no release proceeds while the gateway error budget for the trailing seven days is below forty percent",
    "GATE-23": "every storage migration must have completed a rehearsal restore on a copy of production within the last fourteen days",
    "GATE-31": "the release candidate must have passed the full pipeline twice in a row from a clean cache",
    "GATE-38": "the scheduler must show zero orphaned leases for at least one hour before the promotion step",
    "GATE-42": "identity token rotation must have run to completion in staging within the last seventy-two hours",
    "GATE-49": "the telemetry collector must be dropping fewer than one in ten thousand samples during the canary hour",
    "GATE-56": "the billing reconciliation report for the previous day must balance to the cent before invoices are enabled",
    "GATE-63": "notification delivery to the internal test channel must succeed end to end within the release window",
}
# workstream -> (active handoff code, void decoy code)
HANDOFFS = {
    "gateway": ("HANDOFF-gateway-K4TR", "HANDOFF-gateway-P9QM"),
    "storage": ("HANDOFF-storage-W7NC", "HANDOFF-storage-D2LH"),
    "pipeline": ("HANDOFF-pipeline-M3XV", "HANDOFF-pipeline-R8SB"),
}

NOUNS = """request handler route upstream replica shard lease snapshot checkpoint queue worker retry timeout
budget quota tenant region zone canary rollout rollback manifest artifact cache index token session
credential audit ledger invoice sample metric alert dashboard runbook owner reviewer approval window
schedule trigger payload envelope header cursor batch stream partition consumer producer probe""".split()
ADJ = """idempotent stateless durable transient regional global primary secondary degraded healthy
throttled pinned versioned signed encrypted immutable optional mandatory nominal elevated""".split()
VERBS = """retries drains rejects accepts persists replays reconciles evicts promotes demotes signs
verifies isolates tags counts samples publishes queues acknowledges rotates""".split()
TEMPLATES = [
    "The {comp} {verb} each {noun} before the {noun2} is written, so a {adj} {noun3} never reaches the {noun4}.",
    "When a {noun} is {adj}, the {comp} {verb} it and records the {noun2} in the {noun3}.",
    "Every {noun} carries a {adj} {noun2}; the {comp} {verb} the {noun3} only after the {noun2} is checked.",
    "Operators should expect the {comp} to be {adj} during a {noun}, because it {verb} the {noun2} first.",
    "A {adj} {noun} is the normal case; a {adj2} {noun} is an error that the {comp} {verb} and reports.",
    "The {noun} and the {noun2} are kept {adj} so that the {comp} {verb} them independently.",
    "If the {noun} exceeds its {noun2}, the {comp} {verb} the {adj} {noun3} and raises a {noun4}.",
    "Nothing in the {noun} path is {adj}; the {comp} {verb} the {noun2} on every call.",
    "The {comp} {verb} a {noun} per {noun2}, which keeps the {adj} {noun3} small.",
    "Under load the {comp} {verb} the oldest {noun} first and leaves the {adj} {noun2} untouched.",
    "The {adj} {noun} is owned by the {comp}; no other component {verb} it.",
    "Because the {noun} is {adj}, the {comp} {verb} the {noun2} without holding a {noun3}.",
    "The design keeps the {noun} {adj} and the {noun2} {adj2}, and the {comp} {verb} both.",
    "A {noun} that arrives without a {noun2} is treated as {adj}; the {comp} {verb} it after one {noun3}.",
    "During a {noun} the {comp} {verb} the {adj} {noun2} and the {noun3} follows a minute later.",
    "The {comp} never {verb} a {noun} twice; the {adj} {noun2} guarantees that.",
]
SPEC_SECTIONS = [("Purpose", 3), ("Interfaces", 4), ("Data model", 4), ("Failure handling", 4),
                 ("Release gates", 3), ("Operations", 4), ("Open questions", 2)]
BRIEF_SECTIONS = [("Context", 3), ("What you own", 3), ("Handoff", 3), ("Constraints", 3)]


class Prose:
    def __init__(self, rng, comp):
        self.rng = rng
        self.comp = comp
        self.nouns = NOUNS[:]
        rng.shuffle(self.nouns)

    def pick(self, pool):
        i = int(self.rng.triangular(0, len(pool), 0))
        return pool[min(i, len(pool) - 1)]

    def sentence(self):
        t = self.rng.choice(TEMPLATES)
        n = []
        while len(n) < 4:
            x = self.pick(self.nouns)
            if x not in n:
                n.append(x)
        a = self.rng.sample(ADJ, 2)
        s = t.format(comp=self.comp, noun=n[0], noun2=n[1], noun3=n[2], noun4=n[3], adj=a[0], adj2=a[1],
                     verb=self.rng.choice(VERBS))
        s = re.sub(r"\b[Aa] (?=[aeiou])", lambda m: m.group(0)[0] + "n ", s)
        return s[0].upper() + s[1:]

    def paragraph(self):
        return " ".join(self.sentence() for _ in range(self.rng.randint(5, 8)))


def build_spec(stem, comp, gate, retired, rng):
    p = Prose(rng, comp)
    out = [f"# {comp.capitalize()} specification", "", f"Component: {comp}. Revision {rng.randint(4, 12)}, release cycle 2026-Q3.", ""]
    for name, paras in SPEC_SECTIONS:
        out += [f"## {name}", ""]
        for k in range(paras):
            para = p.paragraph()
            if name == "Purpose" and k == 2 and len(retired) > 1:
                para += (f" The earlier gate {retired[1]} that used to sit in front of this component was retired in the "
                         f"previous cycle and no longer blocks a release.")
            if name == "Release gates" and k == 0:
                para = (f"The gate {retired[0]} described in older revisions of this document is superseded and must not "
                        f"be cited; it is listed here only so that readers of the old runbook recognise the number. " + para)
            if name == "Release gates" and k == 1:
                para = para + f" Release gate {gate}: {GATE_TEXT[gate]}."
            out += [para, ""]
    text = "\n".join(out)
    while len(text.split()) < SPEC_WORDS:
        text += "\n" + p.paragraph() + "\n"
    return text


def build_brief(name, code, void, rng):
    p = Prose(rng, name + " workstream")
    out = [f"# Brief for the {name} workstream", "", f"Cycle 2026-Q3. Read this whole brief before you start.", ""]
    for sec, paras in BRIEF_SECTIONS:
        out += [f"## {sec}", ""]
        for k in range(paras):
            para = p.paragraph()
            if sec == "Context" and k == 1:
                para += (f" The code {void} from the previous cycle is void and must not be reported; "
                         f"anything still carrying it is stale.")
            if sec == "Handoff" and k == 1:
                para = (f"Your handoff code for this cycle is {code}; report it verbatim to the release lead "
                        f"when you finish, because the lead cannot sign off without it. " + para)
            out += [para, ""]
    text = "\n".join(out)
    while len(text.split()) < BRIEF_WORDS:
        text += "\n" + p.paragraph() + "\n"
    return text


def main():
    os.makedirs(os.path.join(FIX, "specs"), exist_ok=True)
    gates = {}
    for i, (stem, comp, gate, retired) in enumerate(SPECS):
        text = build_spec(stem, comp, gate, retired, random.Random(SEED + i))
        fn = f"{stem}.md"
        with open(os.path.join(FIX, "specs", fn), "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        assert text.count("Release gate ") == 1 and text.count(gate) == 1, fn
        for r in retired:
            assert text.count(r) == 1, (fn, r)
        gates[fn] = {"gate": gate, "retired": retired}
        print(fn, "words", len(text.split()))
    handoffs = {}
    for j, (name, (code, void)) in enumerate(HANDOFFS.items()):
        d = os.path.join(FIX, "workstreams", name)
        os.makedirs(d, exist_ok=True)
        text = build_brief(name, code, void, random.Random(SEED + 100 + j))
        with open(os.path.join(d, "brief.md"), "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        assert text.count(code) == 1 and text.count(void) == 1, name
        handoffs[name] = {"code": code, "void": void}
        print(name, "brief words", len(text.split()))
    all_gate_ids = [g["gate"] for g in gates.values()] + [r for g in gates.values() for r in g["retired"]]
    assert len(all_gate_ids) == len(set(all_gate_ids)), "gate ids must be unique across the corpus"
    with open(os.path.join(HERE, "keys.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"gates": gates, "handoffs": handoffs}, f, indent=2)
        f.write("\n")


if __name__ == "__main__":
    main()
