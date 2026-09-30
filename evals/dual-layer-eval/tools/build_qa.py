"""Deterministic builder for the dual-layer eval's public-QA tasks (mq1, hq1, lb1).

Reads the task's knobs from tasks/<id>/hidden/build.json, the raw download from
.logs/dual-layer-eval-build/raw/ (never committed), and writes:

  tasks/<id>/fixtures/...        the corpus the agent sees (committed)
  tasks/<id>/fixtures/SOURCE.md  provenance, copied into the workspace (no answers in it)
  tasks/<id>/hidden/key.json     the answer key check.ps1 scores against
  tasks/<id>/hidden/build-report.json  what was selected and how big the corpus came out
  tasks/<id>/task.md             the prompt (header + message after ---)
  tasks/<id>/meta.json           requiredFixtures count refreshed

Same build.json + same raw file (sha256 checked) => byte-identical output.

Usage:  python evals/dual-layer-eval/tools/build_qa.py <task-id> [--raw-dir DIR]
"""
import argparse
import hashlib
import json
import os
import random
import re
import shutil
import subprocess
import sys
import unicodedata
from pathlib import Path

HERE = Path(__file__).resolve().parent
EVAL = HERE.parent
REPO = EVAL.parent.parent
DEFAULT_RAW = REPO / ".logs" / "dual-layer-eval-build" / "raw"


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def slug(title):
    t = unicodedata.normalize("NFKD", title).encode("ascii", "ignore").decode("ascii")
    t = re.sub(r"[^A-Za-z0-9]+", "_", t).strip("_")
    return (t[:80].rstrip("_") or "untitled")


def write_text(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def write_json(path, obj):
    write_text(path, json.dumps(obj, indent=2, ensure_ascii=False) + "\n")


def reset_dir(path):
    if path.exists():
        shutil.rmtree(path)
    path.mkdir(parents=True)


# ---------------------------------------------------------------- corpus from titled paragraphs

def build_title_corpus(rng, rows_paragraphs, docs_dir):
    """rows_paragraphs: list of lists of (title, text). Merges by title (exact text dedupe),
    shuffles paragraph order inside a file, returns {title: filename}."""
    by_title = {}
    for paras in rows_paragraphs:
        for title, text in paras:
            text = text.strip()
            if not text:
                continue
            lst = by_title.setdefault(title, [])
            if text not in lst:
                lst.append(text)
    names = {}
    used = set()
    for title in sorted(by_title):
        base = slug(title)
        name = base
        k = 2
        while name.lower() in used:
            name = f"{base}_{k}"
            k += 1
        used.add(name.lower())
        names[title] = name + ".md"
    reset_dir(docs_dir)
    total = 0
    for title in sorted(by_title):
        paras = list(by_title[title])
        rng.shuffle(paras)
        body = f"# {title}\n\n" + "\n\n".join(paras) + "\n"
        write_text(docs_dir / names[title], body)
        total += len(body.encode("utf-8"))
    return names, total


QA_RULES = """Write `answers.json` in the workspace root, shaped like this:

```json
{{
  "q1": {{ "answer": "<short answer>", "evidence": ["<file name>.md", "<file name>.md"] }},
  "q2": {{ "answer": "...", "evidence": ["..."] }}
}}
```

- `answer` is the shortest span that answers the question: a name, date, number or short phrase,
  written the way the articles write it. Not a sentence, no explanation.
- `evidence` lists the files the answer rests on. It is optional and not scored.
- Include every question from q1 to q{n}. If you cannot pin one down, give your best guess anyway.
"""


def qa_message(n, n_files, questions, blurb):
    lines = [
        "Project code name: {SEED}.",
        "",
        blurb.format(files=n_files, n=n),
        "",
        "Use the file and shell tools (Glob, Grep, Read, Bash) to find and read the articles you need.",
        "Answer from what the files say, not from memory: where the two disagree, the files win.",
        "",
        QA_RULES.format(n=n).rstrip(),
        "",
        "Questions:",
        "",
    ]
    for q in questions:
        lines.append(f"- **{q['id']}.** {q['question']}")
    lines += ["", "When `answers.json` is written, reply with one line per question: `qN: <answer>`.", ""]
    return "\n".join(lines)


# ---------------------------------------------------------------- MuSiQue

def build_musique(task_dir, cfg, raw_dir):
    raw = raw_dir / cfg["raw_file"]
    rows = [json.loads(l) for l in open(raw, encoding="utf-8")]
    rng = random.Random(cfg["seed"])
    rows = [r for r in rows if r.get("answerable", True)]
    rows.sort(key=lambda r: r["id"])
    order = rows[:]
    rng.shuffle(order)  # one permutation of the whole pool: excluding an id only replaces that slot
    excluded = set(cfg.get("exclude_source_ids", {}))

    def hop(r):
        return r["id"].split("__")[0][:4]  # 2hop / 3hop / 4hop

    def ok_target(r):
        a = r["answer"].strip()
        return (r["id"] not in excluded and a.lower() not in ("yes", "no")
                and 0 < len(a) <= cfg.get("max_answer_chars", 60)
                and len(a.split()) <= cfg.get("max_answer_words", 5))

    targets = []
    seen_answers = set()
    for h, count in cfg["hop_mix"].items():
        got = 0
        for r in order:
            if got >= count:
                break
            key = r["answer"].strip().lower()
            if hop(r) != h or not ok_target(r) or key in seen_answers:
                continue
            seen_answers.add(key)
            targets.append(r)
            got += 1
    target_ids = {r["id"] for r in targets}
    distractors = [r for r in order if r["id"] not in target_ids][: cfg["distractor_questions"]]
    targets.sort(key=lambda r: order.index(r))

    corpus_rows = targets + distractors
    names, total = build_title_corpus(
        rng,
        [[(p["title"], p["paragraph_text"]) for p in r["paragraphs"]] for r in corpus_rows],
        task_dir / "fixtures" / "docs",
    )
    questions = []
    for i, r in enumerate(targets, 1):
        answers = [r["answer"]] + [a for a in r.get("answer_aliases", []) if a]
        evidence = sorted({names[p["title"]] for p in r["paragraphs"] if p["is_supporting"]})
        questions.append({
            "id": f"q{i}", "question": r["question"].strip(), "kind": "text",
            "answers": answers, "evidence": evidence, "source_id": r["id"], "hops": hop(r),
        })
    return questions, names, total, [r["id"] for r in distractors]


# ---------------------------------------------------------------- HotpotQA (distractor dev)

def load_hotpot(raw_dir, cfg):
    jl = raw_dir / cfg["raw_file_jsonl"]
    if not jl.exists():
        pq = raw_dir / cfg["raw_file"]
        subprocess.run(["duckdb", "-c", f"COPY (SELECT * FROM '{pq.as_posix()}') TO '{jl.as_posix()}' (FORMAT JSON);"], check=True)
    return [json.loads(l) for l in open(jl, encoding="utf-8")]


def build_hotpot(task_dir, cfg, raw_dir):
    rows = load_hotpot(raw_dir, cfg)
    rows.sort(key=lambda r: r["id"])
    rng = random.Random(cfg["seed"])
    order = rows[:]
    rng.shuffle(order)  # one permutation of the whole pool: excluding an id only replaces that slot
    excluded = set(cfg.get("exclude_source_ids", {}))

    def ok_target(r):
        a = r["answer"].strip()
        return (r["id"] not in excluded and r["level"] in cfg["levels"] and a.lower() not in ("yes", "no")
                and 0 < len(a) <= cfg.get("max_answer_chars", 60)
                and len(a.split()) <= cfg.get("max_answer_words", 5))

    targets = []
    seen = set()
    for t, count in cfg["type_mix"].items():
        got = 0
        for r in order:
            if got >= count:
                break
            key = r["answer"].strip().lower()
            if r["type"] != t or not ok_target(r) or key in seen:
                continue
            seen.add(key)
            targets.append(r)
            got += 1
    tid = {r["id"] for r in targets}
    distractors = [r for r in order if r["id"] not in tid][: cfg["distractor_questions"]]
    targets.sort(key=lambda r: order.index(r))

    def paras(r):
        return [(t, "".join(s)) for t, s in zip(r["context"]["title"], r["context"]["sentences"])]

    names, total = build_title_corpus(rng, [paras(r) for r in targets + distractors], task_dir / "fixtures" / "docs")
    questions = []
    for i, r in enumerate(targets, 1):
        questions.append({
            "id": f"q{i}", "question": r["question"].strip(), "kind": "text", "answers": [r["answer"]],
            "evidence": sorted({names[t] for t in r["supporting_facts"]["title"] if t in names}),
            "source_id": r["id"], "type": r["type"], "level": r["level"],
        })
    return questions, names, total, [r["id"] for r in distractors]


# ---------------------------------------------------------------- LongBench v2 (multi-doc QA)

def split_pages(context, min_chars, max_chars):
    raw_pages = [p.strip() for p in re.split(r"\n{3,}", context) if p.strip()]
    pages = []
    buf = ""
    for p in raw_pages:
        buf = (buf + "\n\n" + p) if buf else p
        if len(buf) >= min_chars:
            pages.append(buf)
            buf = ""
    if buf:
        if pages and len(buf) < min_chars:
            pages[-1] += "\n\n" + buf
        else:
            pages.append(buf)
    out = []
    for p in pages:  # hard-split oversized pages on line boundaries
        while len(p) > max_chars:
            cut = p.rfind("\n", 0, max_chars)
            cut = cut if cut > max_chars // 2 else max_chars
            out.append(p[:cut].strip())
            p = p[cut:].strip()
        if p:
            out.append(p)
    return out


def build_longbench(task_dir, cfg, raw_dir):
    data = json.load(open(raw_dir / cfg["raw_file"], encoding="utf-8"))
    pool = [x for x in data if x["domain"] == cfg["domain"] and x["length"] in cfg["lengths"]
            and x["sub_domain"] in cfg["sub_domains"]]
    pool.sort(key=lambda x: x["_id"])
    rng = random.Random(cfg["seed"])
    chosen = []
    for diff, count in cfg["difficulty_mix"].items():
        p = [x for x in pool if x["difficulty"] == diff]
        rng.shuffle(p)
        chosen += p[:count]
    rng.shuffle(chosen)
    items_dir = task_dir / "fixtures" / "items"
    reset_dir(items_dir)
    questions, total, pages_total = [], 0, 0
    for i, x in enumerate(chosen, 1):
        qid = f"q{i}"
        pages = split_pages(x["context"], cfg["min_page_chars"], cfg["max_page_chars"])
        for j, pg in enumerate(pages, 1):
            body = f"<!-- {qid} page {j} of {len(pages)} -->\n\n{pg}\n"
            write_text(items_dir / qid / f"p{j:03d}.md", body)
            total += len(body.encode("utf-8"))
        pages_total += len(pages)
        questions.append({
            "id": qid, "kind": "choice", "answers": [x["answer"]], "question": x["question"].strip(),
            "choices": {k: x[f"choice_{k}"].strip() for k in "ABCD"}, "pages": len(pages),
            "source_id": x["_id"], "sub_domain": x["sub_domain"], "difficulty": x["difficulty"],
        })
    return questions, pages_total, total


def longbench_message(questions):
    n = len(questions)
    lines = [
        "Project code name: {SEED}.",
        "",
        f"`items/` has one folder per question, `items/q1/` to `items/q{n}/`. Each folder holds the source",
        "material for its question as page files (`p001.md`, `p002.md`, ... in reading order). A folder",
        "usually combines several documents (papers, reports, articles) and most pages are irrelevant to",
        "the question. Answer each multiple-choice question from its own folder's material only.",
        "",
        "Use the file and shell tools (Glob, Grep, Read, Bash) to find and read the pages you need.",
        "",
        "Write `answers.json` in the workspace root: `{\"q1\": \"B\", \"q2\": \"D\", ...}` with exactly one",
        f"letter A-D per question, q1 to q{n}. If you cannot decide, give your best guess anyway.",
        "",
        "Questions:",
        "",
    ]
    for q in questions:
        lines.append(f"### {q['id']} (folder `items/{q['id']}/`, {q['pages']} pages)")
        lines.append("")
        lines.append(q["question"])
        lines.append("")
        for k in "ABCD":
            lines.append(f"- ({k}) {q['choices'][k]}")
        lines.append("")
    lines += ["When `answers.json` is written, reply with one line per question: `qN: <letter>`.", ""]
    return "\n".join(lines)


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("task")
    ap.add_argument("--raw-dir", default=str(DEFAULT_RAW))
    a = ap.parse_args()
    task_dir = EVAL / "tasks" / a.task
    raw_dir = Path(a.raw_dir)
    cfg = json.load(open(task_dir / "hidden" / "build.json", encoding="utf-8"))
    src = cfg["source"]
    got = sha256(raw_dir / cfg["raw_file"])
    if got != cfg["raw_sha256"]:
        sys.exit(f"raw file sha256 mismatch: {got} != {cfg['raw_sha256']}")

    if src in ("musique", "hotpotqa"):
        fn = build_musique if src == "musique" else build_hotpot
        questions, names, total, distractor_ids = fn(task_dir, cfg, raw_dir)
        n_files = len(names)
        message = qa_message(len(questions), n_files, questions, cfg["blurb"])
        glob, count = "docs/*.md", n_files
        extra = {"distractor_source_ids": distractor_ids}
    elif src == "longbench-v2":
        questions, pages_total, total = build_longbench(task_dir, cfg, raw_dir)
        message = longbench_message(questions)
        glob, count = "items/*/*.md", pages_total
        extra = {}
    else:
        sys.exit(f"unknown source {src}")

    for q in questions:
        if q["kind"] == "choice":
            continue
        # A filename that IS the answer would let a listing answer the question; record it, don't hide it.
        q["answer_is_a_filename"] = any(slug(x).lower() + ".md" in {n.lower() for n in names.values()} for x in q["answers"])

    extra_aliases = cfg.get("extra_aliases", {})
    for q in questions:
        for alias in extra_aliases.get(q["source_id"], {}).get("aliases", []):
            if alias not in q["answers"]:
                q["answers"].append(alias)
    write_json(task_dir / "hidden" / "key.json", {"task": a.task, "source": src, "questions": questions})
    write_json(task_dir / "hidden" / "build-report.json", {
        "task": a.task, "questions": len(questions), "fixture_files": count, "fixture_bytes": total,
        "approx_tokens": total // 4, **extra,
    })
    header = cfg["header"].format(files=count, n=len(questions), mb=round(total / 1e6, 2))
    write_text(task_dir / "task.md", header.rstrip() + "\n\n---\n\n" + message)
    meta_path = task_dir / "meta.json"
    meta = json.load(open(meta_path, encoding="utf-8"))
    meta["requiredFixtures"] = [{"glob": glob, "count": count}]
    write_json(meta_path, meta)
    print(f"{a.task}: {len(questions)} questions, {count} fixture files, {total/1e6:.2f} MB")


if __name__ == "__main__":
    main()
