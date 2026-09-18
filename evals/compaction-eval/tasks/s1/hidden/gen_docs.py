"""Deterministic fixture generator for s1 (run once; the generated files are committed).

Writes fixtures/docs/report-NN.txt (14 field-station reports, ~3.2k words each), fixtures/stopwords.txt,
hidden/counts.json (per-document counts of every non-stop-word token under the task's tokenizer:
maximal runs of ASCII letters, lower-cased) and hidden/stations.json (per-document station id and
verified sensor count). The text is template prose: readable, repetitive, no apostrophes, no digits
in the 300..999 range other than the two inventory figures. Each report states a stale inventory
figure in its Inventory section and corrects it once, later, with the phrase "verified sensor count".

Usage: python gen_docs.py  (from tasks/s1/hidden)
"""
import json
import os
import random
import re
from collections import Counter

SEED = 20260915
HERE = os.path.dirname(os.path.abspath(__file__))
DOCS = os.path.join(HERE, "..", "fixtures", "docs")
N_DOCS = 14
TARGET_WORDS = 3200

STOPWORDS = """the a an and or but of to in on at by for with from as is are was were be been being it its
this that these those we our they their he she his her you your i not no so than then there here which who
what when where while will would can could should may might has have had do does did into over under up
down out about after before during each all any some more most such only also if because until again both
same very just now still per one two three s t""".split()

STATIONS = [
    ("ST-241", 340, 355), ("ST-118", 512, 497), ("ST-306", 288, 301), ("ST-472", 610, 634),
    ("ST-159", 425, 418), ("ST-388", 377, 392), ("ST-207", 466, 451), ("ST-533", 719, 733),
    ("ST-124", 358, 371), ("ST-461", 593, 580), ("ST-275", 402, 417), ("ST-349", 531, 548),
    ("ST-196", 449, 436), ("ST-508", 667, 682),
]

NOUNS = """sensor logger battery panel mast gauge anemometer probe cabinet enclosure antenna modem cable
conduit tripod datalogger transect gully ridge stream culvert fence gate track trail catchment outcrop
valley plateau shelter reading sample calibration drift offset baseline threshold record interval
firmware bracket clamp desiccant vent filter solar regulator inverter beacon relay junction""".split()
ADJ = """steady intermittent damp corroded loose stable seasonal upstream downstream northern southern
exposed sheltered recent original replacement manual automatic partial nominal marginal elevated""".split()
VERBS_PAST = """checked replaced logged cleaned tightened recorded flagged reset inspected photographed
measured calibrated swapped taped labelled traced rerouted drained secured reseated""".split()
TIMES = ["at dawn", "before noon", "after the storm", "during the site visit", "on the second day", "overnight",
         "late in the afternoon", "at the end of the week", "during the routine sweep", "after the frost"]

TEMPLATES = [
    "The {noun} on the {adj} side was {verb} {time} and the {noun2} reading settled within the {noun3} threshold.",
    "We {verb} the {adj} {noun} because the {noun2} had drifted since the {adj2} visit.",
    "A {adj} {noun} near the {noun2} was {verb} and the {noun3} interval was left unchanged.",
    "The team {verb} every {noun} along the {noun2} and found the {noun3} in {adj} condition.",
    "Nothing about the {noun} changed {time}, although the {noun2} showed a {adj} offset.",
    "After the {noun} was {verb}, the {noun2} returned to a {adj} baseline within the hour.",
    "The {adj} {noun} still needs a {noun2} and the {noun3} should be {verb} on the next visit.",
    "Field notes describe the {noun} as {adj}, which matches what the {noun2} logged {time}.",
    "The {noun} feeding the {noun2} was {verb} twice, once {time} and once after the {noun3} check.",
    "Readings from the {adj} {noun} agree with the {noun2} to within the usual {noun3} margin.",
    "The crew {verb} the {noun}, {verb2} the {noun2}, and left the {adj} {noun3} for the maintenance round.",
    "Because the {noun} is {adj}, the {noun2} was {verb} rather than replaced.",
    "The {noun} record shows a {adj} pattern that the {noun2} does not, so the {noun3} was {verb}.",
    "Both the {noun} and the {noun2} were {verb} {time} without any change to the {noun3}.",
    "A {adj} {noun} was noted at the {noun2}; it was {verb} and the {noun3} was {verb2}.",
    "The {noun} sits on the {adj} bank of the {noun2}, well clear of the {noun3}.",
    "When the {noun} was {verb}, the {noun2} showed the {adj} drift we expected from the {noun3}.",
    "The {adj} {noun} is the only {noun2} that was not {verb} on this visit.",
    "The {noun} and the {noun2} share a {noun3}, so both were {verb} together.",
    "Weather {time} was {adj} enough that the {noun} could be {verb} without a {noun2}.",
]

SECTIONS = [
    ("Summary", 3), ("Site conditions", 4), ("Inventory", 4), ("Instrument status", 4),
    ("Reconciliation and corrections", 3), ("Observations", 4), ("Maintenance", 3), ("Next steps", 2),
]

TOKEN = re.compile(r"[A-Za-z]+")


class Prose:
    def __init__(self, rng):
        self.rng = rng
        # per-document vocabulary bias so the top words differ between reports
        self.nouns = NOUNS[:]
        rng.shuffle(self.nouns)
        self.adj = ADJ[:]
        rng.shuffle(self.adj)
        self.verbs = VERBS_PAST[:]
        rng.shuffle(self.verbs)

    def pick(self, pool):
        # triangular pick: early items of the shuffled pool are favoured, giving each report its own top words
        i = int(self.rng.triangular(0, len(pool), 0))
        return pool[min(i, len(pool) - 1)]

    def sentence(self):
        t = self.rng.choice(TEMPLATES)
        n = [self.pick(self.nouns) for _ in range(3)]
        while n[1] == n[0]:
            n[1] = self.pick(self.nouns)
        while n[2] in n[:2]:
            n[2] = self.pick(self.nouns)
        v = [self.pick(self.verbs) for _ in range(2)]
        a = [self.pick(self.adj) for _ in range(2)]
        s = t.format(noun=n[0], noun2=n[1], noun3=n[2], verb=v[0], verb2=v[1], adj=a[0], adj2=a[1],
                     time=self.rng.choice(TIMES))
        s = re.sub(r"\b[Aa] (?=[aeiou])", lambda m: m.group(0)[0] + "n ", s)
        return s[0].upper() + s[1:]

    def paragraph(self, n_sentences=None):
        n = n_sentences or self.rng.randint(5, 8)
        return " ".join(self.sentence() for _ in range(n))


def build_doc(idx, rng):
    station, stale, verified = STATIONS[idx]
    p = Prose(rng)
    out = [f"Field report for station {station}", ""]
    out.append(f"Filed after the visit of week {rng.randint(10, 22)}. Station {station} is the "
               f"{p.pick(p.adj)} site of the {p.pick(p.nouns)} network and this report covers every {p.pick(p.nouns)} "
               f"we {p.pick(p.verbs)} while on site.")
    out.append("")
    words = 0
    for name, paras in SECTIONS:
        out.append(name.upper())
        out.append("")
        for k in range(paras):
            para = p.paragraph()
            if name == "Inventory" and k == 1:
                para = (f"The site inventory sheet lists {stale} sensors across the station, counting every "
                        f"{p.pick(p.nouns)} and {p.pick(p.nouns)} channel as one. " + para)
            if name == "Reconciliation and corrections" and k == 1:
                para = (para + f" Correction to the inventory figure given earlier in this report: the count of {stale} "
                        f"sensors came from the ledger that was not updated after the spring swap, so it is stale; the "
                        f"verified sensor count for station {station} is {verified}.")
            out.append(para)
            out.append("")
    text = "\n".join(out)
    # pad or trim toward the target
    while len(text.split()) < TARGET_WORDS:
        text += "\n" + p.paragraph() + "\n"
    return text


def main():
    rng = random.Random(SEED)
    os.makedirs(DOCS, exist_ok=True)
    stop = set(STOPWORDS)
    counts = {}
    stations = {}
    for i in range(N_DOCS):
        name = f"report-{i + 1:02d}.txt"
        text = build_doc(i, random.Random(SEED + i))
        with open(os.path.join(DOCS, name), "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        toks = [t.lower() for t in TOKEN.findall(text)]
        c = Counter(t for t in toks if t not in stop)
        counts[name] = dict(sorted(c.items(), key=lambda kv: (-kv[1], kv[0])))
        station, stale, verified = STATIONS[i]
        stations[name] = {"station": station, "verified_count": verified, "stale_count": stale}
        assert text.count("verified sensor count") == 1, name
        assert text.count(str(verified)) == 1 and text.count(str(stale)) == 2, name
        print(name, "words", len(text.split()), "distinct", len(c), "top15 threshold", sorted(c.values(), reverse=True)[14])
    assert len({s["station"] for s in stations.values()}) == N_DOCS
    assert len({s["verified_count"] for s in stations.values()}) == N_DOCS
    with open(os.path.join(HERE, "..", "fixtures", "stopwords.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(STOPWORDS) + "\n")
    with open(os.path.join(HERE, "counts.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(counts, f, indent=1)
        f.write("\n")
    with open(os.path.join(HERE, "stations.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(stations, f, indent=2)
        f.write("\n")


if __name__ == "__main__":
    main()
