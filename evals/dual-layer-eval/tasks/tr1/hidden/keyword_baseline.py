"""Adversarial baseline for tr1: answer every question with a keyword/regex classifier instead of reading.

A ticket counts as a Qualifying Incident when it uses outage words and none of the obvious exclusion words;
its minutes are the first "<n> minutes" in the text (else 60); its month is the month it was opened; a provider
failure is any mention of CDN or DNS; a customer change is any mention of the customer's side or config words.
Writes <out>/answers.json for the shared checker and prints the per-ticket confusion against the build's truth.

Usage: python keyword_baseline.py <out-dir>
"""
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
FIX = HERE.parent / "fixtures" / "tickets"
OUTAGE = re.compile(r"\b(outage|down|unavailable|unreachable|offline)\b", re.I)
EXCLUDE = re.compile(r"staging|preview|pre-release|your (firewall|dns|own)|internet provider|broadband|not down|no outage"
                     r"|false alarm|invoice|feature", re.I)
PROVIDER = re.compile(r"\b(cdn|dns)\b", re.I)
CUSTOMER_CHANGE = re.compile(r"your side|you changed|we changed|firewall|password|certificate|allow-list", re.I)


def main():
    out = Path(sys.argv[1])
    report = json.load(open(HERE / "build-report.json", encoding="utf-8"))
    params = report["params"]
    picked = []
    for f in sorted(FIX.glob("*.md")):
        t = f.read_text(encoding="utf-8")
        if not OUTAGE.search(t) or EXCLUDE.search(t):
            continue
        acct = re.search(r"\| Account \| (.+?) \|", t).group(1)
        plan = re.search(r"\| Plan \| (.+?) \|", t).group(1)
        month = int(re.search(r"\| Opened \| \d{4}-(\d\d)", t).group(1))
        m = re.search(r"(\d+) minutes", t)
        picked.append({"id": f.stem, "acct": acct, "plan": plan, "month": month, "min": int(m.group(1)) if m else 60,
                       "provider": bool(PROVIDER.search(t)), "change": bool(CUSTOMER_CHANGE.search(t))})
    by = {}
    for p in picked:
        by[p["acct"]] = by.get(p["acct"], 0) + 1
    ans = {
        "q1": len(picked),
        "q2": sorted(p["id"] for p in picked if p["plan"] == "Enterprise" and p["min"] < 30),
        "q3": max(sorted(by), key=lambda a: by[a]) if by else "",
        "q4": sum(p["provider"] for p in picked),
        "q5": sum(p["min"] for p in picked if p["acct"] == params["q5_customer"]),
        "q6": sum(1 for p in picked if p["month"] == params["q6_month"]),
        "q7": sorted(p["id"] for p in picked if p["change"]),
        "q8": len(by),
    }
    out.mkdir(parents=True, exist_ok=True)
    (out / "answers.json").write_text(json.dumps(ans, indent=1), encoding="utf-8")
    truth = {q["ticket"] for q in report["qualifying_incidents"]}
    got = {p["id"] for p in picked}
    print(json.dumps({"picked": len(got), "true": len(truth), "true_positives": len(got & truth),
                      "false_positives": len(got - truth), "missed": len(truth - got)}))


if __name__ == "__main__":
    main()
