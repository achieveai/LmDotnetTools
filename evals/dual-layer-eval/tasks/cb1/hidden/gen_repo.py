"""Deterministic generator for cb1: a fictional service-mesh repository (run once; output committed).

Writes fixtures/ (config/, services/, env/, flags/, docs/, platform/, ci/), hidden/key.json,
hidden/build-report.json, task.md and meta.json's requiredFixtures. Knobs: hidden/build.json.

The repo ("Quillfeather mesh", all names invented) resolves one service's mesh settings in layers
(documented in docs/CONFIG.md and implemented in platform/ConfigLoader.cs):

  config/defaults.yaml
  < config/shared/*.yaml listed in the service's `include:` (listed order, later wins; a fragment's own
    `include:` is applied before the fragment's own values)
  < services/<svc>/service.yaml
  < env/<env>/global.yaml
  < env/<env>/services/<svc>.yaml
  < services/<svc>/src/Startup.cs ConfigureMesh (runs last; may read env, feature flags, other settings)

Only the `mesh:` section feeds MeshOptions (`admin:` configures the admin listener). Commented-out
lines (YAML `#`, JSONC `//`, C# `//`) do nothing. Flags: flags/catalog.json default, then
flags/<env>.jsonc. env.IsProduction is true for staging AND prod (platform/MeshEnvironment.cs).

Every question is planted: its layers are drawn at random until the true answer (a) needs 3+ files,
(b) changes if commented-out lines are honoured, (c) changes if Startup.cs is ignored and (d) changes
under at least one more trap. Traps are one resolver with flags: honor_commented, ignore_code,
ignore_env, include_order_reversed, flag_catalog_default, isprod_literal, wrong_section,
skip_nested_include. A trap that flips no question aborts the build.

Usage: python gen_repo.py   (paths are relative to this file)
"""
import copy
import json
import random
import shutil
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
TASK = HERE.parent
sys.path.insert(0, str(TASK.parent.parent / "tools"))
from filler import Filler  # noqa: E402

CFG = json.load(open(HERE / "build.json", encoding="utf-8"))
rng = random.Random(CFG["seed"])
F = Filler(random.Random(CFG["seed"] + 1))

ENVS = ["dev", "qa", "staging", "prod"]
PROD_LIKE = {"staging", "prod"}
KEYS = {
    "max_connections": ("int", 16, 512, 8),
    "request_timeout_ms": ("int", 200, 5000, 50),
    "pool_size": ("int", 4, 128, 4),
    "queue_depth": ("int", 50, 2000, 25),
    "cache_ttl_seconds": ("int", 30, 3600, 30),
    "circuit_threshold": ("int", 3, 60, 1),
    "retry_budget": ("int", 1, 9, 1),
    "lb_policy": ("str", ["roundrobin", "leastload", "ringhash", "maglev", "random"]),
    "compression": ("str", ["gzip", "zstd", "brotli", "none"]),
}
ADMIN_KEYS = {"max_connections", "request_timeout_ms"}
TRAPS = ["honor_commented", "ignore_code", "ignore_env", "include_order_reversed", "flag_catalog_default",
         "isprod_literal", "wrong_section", "skip_nested_include"]
QKEYS = [q["key"] for q in CFG["questions"]]
NOISE_KEYS = list(KEYS)
assert len(set(QKEYS)) == len(QKEYS), "question keys must be distinct"


def pascal(k):
    return "".join(p.capitalize() for p in k.split("_"))


def rand_val(k, r):
    spec = KEYS[k]
    if spec[0] == "str":
        return r.choice(spec[1])
    _, lo, hi, step = spec
    return r.randrange(lo, hi + 1, step)


# ------------------------------------------------------------------ names (invented)
SVC_A = ["ledger", "tally", "parcel", "quill", "ferry", "harbor", "docket", "manifest", "berth", "lantern",
         "sluice", "wicket", "gantry", "bollard", "capstan", "tiller", "coracle", "skiff"]
SVC_B = ["bridge", "hook", "relay", "vault", "gate", "works", "scope", "yard", "loom", "mill"]
svc_names = []
while len(svc_names) < CFG["services"]:
    n = rng.choice(SVC_A) + rng.choice(SVC_B)
    if n not in svc_names and not any(n[:4] == s[:4] and n[-3:] == s[-3:] for s in svc_names):
        svc_names.append(n)
FRAG_N = ["pool", "timeouts", "burst", "egress", "ingress", "retry", "cache", "balance", "fanout", "backpressure",
          "latency", "batch", "stream", "edge", "core", "archive", "sync", "drain"]
FRAG_A = ["standard", "relaxed", "strict", "tolerant", "lean", "wide", "narrow", "steady", "legacy", "shared",
          "regional", "baseline"]
n_frags = CFG["general_fragments"] + 2 * len(CFG["questions"])
frag_names = []
while len(frag_names) < n_frags:
    n = f"{rng.choice(FRAG_N)}-{rng.choice(FRAG_A)}"
    if n not in frag_names:
        frag_names.append(n)
general = frag_names[:CFG["general_fragments"]]
dedicated = frag_names[CFG["general_fragments"]:]
FLAG_WORDS = ["bulk_mode", "fast_lane", "shadow_reads", "strict_tls", "warm_cache", "batch_acks", "lazy_init",
              "wide_window", "quiet_retries", "early_flush"]

# ------------------------------------------------------------------ world (noise first)
# entry = [section, key, value, commented]; op = dict(kind, key, arg, cond, commented)
W = {
    "defaults": {k: rand_val(k, rng) for k in KEYS},
    "frags": {},
    "services": {},
    "env_global": {e: [] for e in ENVS},
    "env_svc": {e: {} for e in ENVS},
    "flag_catalog": {},
    "flag_env": {e: [] for e in ENVS},
}
for i, name in enumerate(frag_names):
    inc = []
    if name in general and i > 0 and rng.random() < 0.4:
        inc = [rng.choice(general[:i])]
    ents = [["mesh", k, rand_val(k, rng), False] for k in rng.sample(NOISE_KEYS, rng.randint(1, 3))]
    W["frags"][name] = {"include": inc, "entries": ents}


def noise_ops(r, svc, keys, n):
    ops = []
    for _ in range(n):
        k = r.choice(keys)
        if KEYS[k][0] == "str":
            op = {"kind": "set", "key": k, "arg": rand_val(k, r)}
        else:
            op = r.choice([{"kind": "mul", "key": k, "arg": 2}, {"kind": "add", "key": k, "arg": KEYS[k][3] * r.randint(1, 4)},
                           {"kind": "max", "key": k, "arg": rand_val(k, r)}, {"kind": "min", "key": k, "arg": rand_val(k, r)}])
        op["cond"] = r.choice([None, ("env", r.choice(ENVS)), ("isprod", True), ("flag", r.choice(W["services"][svc]["flags"]))])
        op["commented"] = r.random() < 0.15
        ops.append(op)
    return ops


for s in svc_names:
    flags = [f"{s}.{w}" for w in rng.sample(FLAG_WORDS, rng.randint(1, 2))]
    for f in flags:
        W["flag_catalog"][f] = rng.random() < 0.3
    W["services"][s] = {
        "includes": rng.sample(general, rng.randint(1, 2)),
        "entries": [["mesh", k, rand_val(k, rng), rng.random() < 0.1]
                    for k in rng.sample(NOISE_KEYS, rng.randint(2, 3))],
        "flags": flags, "code": [], "admin_port": 9100 + rng.randint(0, 80),
    }
    W["services"][s]["code"] = noise_ops(rng, s, [k for k in KEYS if k != "retry_budget"], rng.randint(1, 2))
for e in ENVS:
    W["env_global"][e] = [["mesh", k, rand_val(k, rng), False] for k in rng.sample(NOISE_KEYS, 2)]
    for s in rng.sample(svc_names, CFG["env_overlays_per_env"]):
        W["env_svc"][e][s] = [["mesh", k, rand_val(k, rng), rng.random() < 0.15]
                              for k in rng.sample(NOISE_KEYS, rng.randint(1, 2))]
    for f in rng.sample(sorted(W["flag_catalog"]), 8):
        W["flag_env"][e].append([f, rng.random() < 0.6, rng.random() < 0.15])


# ------------------------------------------------------------------ resolver
def ordered(entries):
    return [x for x in entries if x[0] == "mesh"] + [x for x in entries if x[0] == "admin"]


def resolve(w, svc, env, T=frozenset(), drop=None):
    cfg = dict(w["defaults"])
    trail = {k: ["config/defaults.yaml"] for k in KEYS}
    S = w["services"][svc]

    def apply_file(path, entries):
        if path == drop:
            return
        for sec, k, v, com in ordered(entries):
            if com and "honor_commented" not in T:
                continue
            if sec == "mesh" or (sec == "admin" and "wrong_section" in T):
                cfg[k] = v
                trail[k].append(path)

    def apply_frag(name):
        fr = w["frags"][name]
        if "skip_nested_include" not in T:
            for inc in fr["include"]:
                apply_frag(inc)
        apply_file(f"config/shared/{name}.yaml", fr["entries"])

    for f in (reversed(S["includes"]) if "include_order_reversed" in T else S["includes"]):
        apply_frag(f)
    apply_file(f"services/{svc}/service.yaml", S["entries"])
    if "ignore_env" not in T:
        apply_file(f"env/{env}/global.yaml", w["env_global"][env])
        if svc in w["env_svc"][env]:
            apply_file(f"env/{env}/services/{svc}.yaml", w["env_svc"][env][svc])
    flags = dict(w["flag_catalog"])
    flag_src = {}
    if "flag_catalog_default" not in T and drop != f"flags/{env}.jsonc":
        for f, v, com in w["flag_env"][env]:
            if com and "honor_commented" not in T:
                continue
            flags[f] = v
            flag_src[f] = f"flags/{env}.jsonc"
    if "ignore_code" not in T:
        for op in S["code"]:
            if op["commented"] and "honor_commented" not in T:
                continue
            c = op["cond"]
            if c is None:
                ok = True
            elif c[0] == "env":
                ok = env == c[1]
            elif c[0] == "notenv":
                ok = env != c[1]
            elif c[0] == "isprod":
                isp = env == "prod" if "isprod_literal" in T else env in PROD_LIKE
                ok = isp == c[1]
            elif c[0] == "flag":
                ok = flags.get(c[1], False)
            elif c[0] == "cfg":
                ok = cfg["retry_budget"] >= c[1]
            elif c[0] == "eq":
                ok = cfg[c[1]] == c[2]
            else:
                raise ValueError(c)
            if not ok:
                continue
            k, a = op["key"], op["arg"]
            cfg[k] = {"set": lambda: a, "mul": lambda: cfg[k] * a, "add": lambda: cfg[k] + a,
                      "min": lambda: min(cfg[k], a), "max": lambda: max(cfg[k], a),
                      "derive": lambda: cfg["retry_budget"] * a}[op["kind"]]()
            trail[k].append(f"services/{svc}/src/Startup.cs")
            if c and c[0] == "flag":
                trail[k].append(flag_src.get(c[1], "flags/catalog.json"))
            if op["kind"] == "derive" or (c and c[0] == "cfg"):
                trail[k].extend(trail["retry_budget"])
    return cfg, trail


# ------------------------------------------------------------------ planting
questions = []
used_svcs = set()


def plant(qi, spec, r):
    global W
    K, E = spec["key"], spec["env"]
    is_str = KEYS[K][0] == "str"
    svc = r.choice([s for s in svc_names if s not in used_svcs])
    fa, fb = dedicated[2 * qi], dedicated[2 * qi + 1]
    S = W["services"][svc]
    fc = r.choice(general)
    for fr in (W["frags"][fa], W["frags"][fb], W["frags"][fc]):
        fr["entries"] = [x for x in fr["entries"] if x[1] != K]
    W["frags"][fa]["include"] = [fc] if r.random() < 0.6 else []
    W["frags"][fb]["include"] = []
    incs = [g for g in S["includes"] if g != fc]
    pa = r.randint(0, len(incs))
    incs.insert(pa, fa)
    incs.insert(r.randint(pa + 1, len(incs)), fb)
    S["includes"] = incs

    def vals(n):
        out = []
        while len(out) < n:
            v = rand_val(K, r)
            if v not in out and v != W["defaults"][K]:
                out.append(v)
        return out

    v = vals(6) if not is_str else [rand_val(K, r) for _ in range(6)]
    if r.random() < 0.5:
        W["frags"][fc]["entries"].append(["mesh", K, v[0], False])
    if r.random() < 0.75:
        W["frags"][fa]["entries"].append(["mesh", K, v[1], False])
    if r.random() < 0.6:
        W["frags"][fb]["entries"].append(["mesh", K, v[2], False])
    S["entries"] = [x for x in S["entries"] if x[1] != K and not (x[0] == "admin")]
    W["env_global"][E] = [x for x in W["env_global"][E] if x[1] != K]
    ov = W["env_svc"][E].setdefault(svc, [])
    ov[:] = [x for x in ov if x[1] != K and x[0] != "admin"]
    for layer, val in ((S["entries"], v[3]), (W["env_global"][E], v[4]), (ov, v[5])):
        c = r.choices(["active", "commented", "none"], [4, 3, 3])[0]
        if c != "none":
            layer.append(["mesh", K, val, c == "commented"])
    if K in ADMIN_KEYS and r.random() < 0.7:
        r.choice([S["entries"], ov]).append(["admin", K, rand_val(K, r) // 4 or 4, False])
    others = [e for e in ENVS if e != E]
    if r.random() < 0.5:
        oe = r.choice(others)
        oo = W["env_svc"][oe].setdefault(svc, [])
        oo[:] = [x for x in oo if x[1] != K]
        oo.append(["mesh", K, rand_val(K, r), False])
    if r.random() < 0.4:
        S["entries"] = [x for x in S["entries"] if x[1] != "retry_budget"]
        S["entries"].append(["mesh", "retry_budget", rand_val("retry_budget", r), r.random() < 0.3])
    # startup code for K
    S["code"] = [op for op in S["code"] if op["key"] != K]
    ops = []
    for _ in range(r.randint(2, 4) if is_str else r.randint(1, 3)):
        if is_str:
            op = {"kind": "set", "key": K, "arg": rand_val(K, r)}
        else:
            step = KEYS[K][3]
            op = r.choice([{"kind": "mul", "key": K, "arg": r.choice([2, 3])},
                           {"kind": "add", "key": K, "arg": step * r.randint(1, 6)},
                           {"kind": "min", "key": K, "arg": rand_val(K, r)},
                           {"kind": "max", "key": K, "arg": rand_val(K, r)},
                           {"kind": "set", "key": K, "arg": rand_val(K, r)},
                           {"kind": "derive", "key": K, "arg": max(1, rand_val(K, r) // 5)}])
        ck = r.choice(["none", "env", "notenv", "isprod", "notprod", "flag", "flag", "cfg"] + (["eq", "eq", "eq"] if is_str else []))
        if ck == "none":
            op["cond"] = None
        elif ck == "env":
            op["cond"] = ("env", r.choice([E, E, r.choice(others)]))
        elif ck == "notenv":
            op["cond"] = ("notenv", r.choice(others))
        elif ck in ("isprod", "notprod"):
            op["cond"] = ("isprod", ck == "isprod")
        elif ck == "cfg":
            op["cond"] = ("cfg", r.randint(2, 8))
        elif ck == "eq":
            op["cond"] = ("eq", K, r.choice(v[1:]))
            if op["arg"] == op["cond"][2]:
                op["arg"] = r.choice([x for x in KEYS[K][1] if x != op["cond"][2]])
        else:
            f = r.choice(S["flags"])
            op["cond"] = ("flag", f)
            W["flag_catalog"][f] = r.random() < 0.4
            fe = [x for x in W["flag_env"][E] if x[0] != f]
            c = r.choices(["on", "off", "commented", "none"], [3, 2, 3, 2])[0]
            if c != "none":
                fe.append([f, c != "off", c == "commented"])
                if c == "commented":
                    fe[-1][1] = not W["flag_catalog"][f]
            W["flag_env"][E] = fe
        op["commented"] = r.random() < 0.25
        ops.append(op)
    pos = r.randint(0, len(S["code"]))
    S["code"][pos:pos] = ops
    return svc


def check(spec, svc, w):
    K, E = spec["key"], spec["env"]
    gold_cfg, trail = resolve(w, svc, E)
    gold = gold_cfg[K]
    if KEYS[K][0] == "int" and not (1 <= gold <= 99999):
        return None
    flips = [t for t in TRAPS if resolve(w, svc, E, frozenset([t]))[0][K] != gold]
    # a file "matters" when dropping its active entries changes the answer
    cands = dict.fromkeys(trail[K] + trail["retry_budget"] + [f"flags/{E}.jsonc"])
    cands.pop("config/defaults.yaml", None)
    cands.pop(f"services/{svc}/src/Startup.cs", None)
    matter = [f for f in cands if resolve(w, svc, E, drop=f)[0][K] != gold]
    files = matter + [f"services/{svc}/src/Startup.cs"]
    ok = ("honor_commented" in flips and "ignore_code" in flips and len(set(flips) - {"honor_commented", "ignore_code"}) >= 1
          and len(matter) >= 2 and f"services/{svc}/src/Startup.cs" in trail[K]
          and spec.get("must_flip", "ignore_code") in flips)
    return {"gold": gold, "flips": flips, "evidence": files} if ok else None


for qi, spec in enumerate(CFG["questions"]):
    for attempt in range(CFG["max_attempts"]):
        r = random.Random(CFG["seed"] * 1000 + qi * 7919 + attempt)
        saved = copy.deepcopy(W)
        svc = plant(qi, spec, r)
        res = check(spec, svc, W)
        if res:
            break
        W = saved
    else:
        sys.exit(f"q{qi + 1}: no valid plant in {CFG['max_attempts']} attempts")
    used_svcs.add(svc)
    questions.append({"spec": spec, "svc": svc, "attempt": attempt})

gold, flips, evid = {}, {}, {}
for i, q in enumerate(questions):
    res = check(q["spec"], q["svc"], W)
    if not res:
        sys.exit(f"q{i + 1}: invalidated by a later plant")
    gold[f"q{i + 1}"], flips[f"q{i + 1}"], evid[f"q{i + 1}"] = res["gold"], res["flips"], res["evidence"]
trap_answers = {t: {f"q{i + 1}": resolve(W, q["svc"], q["spec"]["env"], frozenset([t]))[0][q["spec"]["key"]]
                    for i, q in enumerate(questions)} for t in TRAPS}
dead = [t for t in TRAPS if not any(t in f for f in flips.values())]
if dead:
    sys.exit(f"traps that flip no question: {dead}")


# ------------------------------------------------------------------ rendering
files = {}


def emit(rel, text):
    assert rel not in files, rel
    files[rel] = text.rstrip("\n") + "\n"


def yaml_val(v):
    return str(v)


def comment_block(n):
    words = F.policy(n).split()
    lines, cur = [], "#"
    for wd in words:
        if len(cur) + len(wd) + 1 > 96:
            lines.append(cur)
            cur = "#"
        cur += " " + wd
    lines.append(cur)
    return "\n".join(lines)


def render_sections(entries, extra=None):
    out = []
    mesh = [x for x in entries if x[0] == "mesh"]
    admin = [x for x in entries if x[0] == "admin"]
    if mesh:
        out.append("mesh:")
        for _, k, v, com in mesh:
            out.append(f"  # {k}: {yaml_val(v)}" if com else f"  {k}: {yaml_val(v)}")
        out.append("")
    for block in (extra or []):
        out.extend(block)
        out.append("")
    if admin:
        out.append("admin:")
        for _, k, v, com in admin:
            out.append(f"  {k}: {yaml_val(v)}")
        out.append("")
    return "\n".join(out)


def telemetry_block(r, svc):
    return ["telemetry:", f"  service_name: {svc}", f"  exporter: {r.choice(['otlp', 'statsd', 'stdout'])}",
            f"  sample_rate: {r.choice(['0.01', '0.05', '0.1', '0.25'])}", f"  flush_interval_s: {r.choice([5, 10, 15, 30])}"]


def labels_block(r):
    return ["labels:", f"  tier: {r.choice(['gold', 'silver', 'bronze'])}",
            f"  cost_centre: cc{r.randint(1000, 9999)}", f"  paging_channel: ops{r.randint(10, 99)}"]


# defaults
emit("config/defaults.yaml", "\n".join([
    "# Platform-wide defaults for every service in the Quillfeather mesh.",
    "# Every service starts from these values; docs/CONFIG.md explains what can override them.",
    comment_block(3), "",
    "mesh:"] + [f"  {k}: {yaml_val(v)}" for k, v in W["defaults"].items()] + [
    "", "telemetry:", "  exporter: otlp", "  sample_rate: 0.05", "  flush_interval_s: 15", "",
    "admin:", "  port: 9100", "  max_connections: 8", "  request_timeout_ms: 2000"]))

for name, fr in W["frags"].items():
    head = [f"# Shared fragment {name}. Services pull it in through the `include:` list of their service.yaml.",
            comment_block(2), ""]
    if fr["include"]:
        head += ["include:"] + [f"  - shared/{i}.yaml" for i in fr["include"]] + [""]
    emit(f"config/shared/{name}.yaml", "\n".join(head) + "\n" + render_sections(fr["entries"]))

for svc, S in W["services"].items():
    r = random.Random(f"{CFG['seed']}-{svc}")
    head = [f"# Service configuration for {svc}.", "# Merged by platform/ConfigLoader.cs; see docs/CONFIG.md for the layer order.",
            comment_block(2), "", "include:"] + [f"  - shared/{i}.yaml" for i in S["includes"]] + [""]
    emit(f"services/{svc}/service.yaml", "\n".join(head) + "\n" + render_sections(
        S["entries"], [telemetry_block(r, svc), labels_block(r)]) + f"\nadmin_port: {S['admin_port']}\n")

for e in ENVS:
    emit(f"env/{e}/global.yaml", "\n".join([
        f"# Overlay for every service in the {e} environment.",
        f"# Applied after each service's own service.yaml and before env/{e}/services/<service>.yaml.",
        comment_block(2), ""]) + "\n" + render_sections(W["env_global"][e]))
    for svc, ents in W["env_svc"][e].items():
        emit(f"env/{e}/services/{svc}.yaml", "\n".join([
            f"# {e} overlay for {svc}. Applied after env/{e}/global.yaml.", comment_block(1), ""]) + "\n" + render_sections(ents))

catalog = {"about": "Feature flag catalog. Each flag's default applies in every environment unless flags/<env>.jsonc sets it.",
           "flags": [{"name": f, "default": v, "owner": f.split(".")[0],
                      "description": F.r.choice(["Experimental; see the service README.", "Rollout switch for the change described in the service README.",
                                                 "Temporary; remove after the migration.", "Kill switch; leave as is unless paged."])}
                     for f, v in sorted(W["flag_catalog"].items())]}
emit("flags/catalog.json", json.dumps(catalog, indent=2))
for e in ENVS:
    ents = sorted(W["flag_env"][e], key=lambda x: x[0])
    lines = [f"// Feature flag values for {e}. A flag not listed here keeps its default from flags/catalog.json.",
             "// Lines starting with // are ignored by the loader (platform/FeatureFlags.cs).", "{"]
    active_idx = [i for i, x in enumerate(ents) if not x[2]]
    for i, (f, v, com) in enumerate(ents):
        comma = "," if (com or (active_idx and i != active_idx[-1])) else ""
        lines.append(f'  // "{f}": {str(v).lower()},' if com else f'  "{f}": {str(v).lower()}{comma}')
    lines.append("}")
    emit(f"flags/{e}.jsonc", "\n".join(lines))


# ---- C#
def cond_text(c):
    if c[0] == "env":
        return f'env.Name == "{c[1]}"'
    if c[0] == "notenv":
        return f'env.Name != "{c[1]}"'
    if c[0] == "isprod":
        return "env.IsProduction" if c[1] else "!env.IsProduction"
    if c[0] == "flag":
        return f'flags.IsOn("{c[1]}")'
    if c[0] == "cfg":
        return f"o.RetryBudget >= {c[1]}"
    if c[0] == "eq":
        return f'o.{pascal(c[1])} == "{c[2]}"'
    raise ValueError(c)


def stmt_text(op):
    p, a = pascal(op["key"]), op["arg"]
    return {"set": lambda: f'o.{p} = "{a}";' if isinstance(a, str) else f"o.{p} = {a};",
            "mul": lambda: f"o.{p} *= {a};", "add": lambda: f"o.{p} += {a};",
            "min": lambda: f"o.{p} = Math.Min(o.{p}, {a});", "max": lambda: f"o.{p} = Math.Max(o.{p}, {a});",
            "derive": lambda: f"o.{p} = o.RetryBudget * {a};"}[op["kind"]]()


def op_lines(op):
    ind = " " * 8
    if op["cond"] is None:
        body = [stmt_text(op)]
    else:
        body = [f"if ({cond_text(op['cond'])})", "{", "    " + stmt_text(op), "}"]
    if op["commented"]:
        return [ind + "// " + b for b in body]
    return [ind + b for b in body]


VERBS = ["Reconcile", "Dispatch", "Settle", "Allocate", "Audit", "Archive", "Route", "Quote", "Replay", "Stage",
         "Release", "Validate", "Merge", "Split", "Tag", "Price"]
NOUNS = ["Batch", "Manifest", "Consignment", "Pallet", "Invoice", "Booking", "Slot", "Crate", "Voyage", "Docket",
         "Receipt", "Hold"]


def cs_ns(svc):
    return svc.capitalize()


def handler_cs(svc, name, r):
    ns = cs_ns(svc)
    lo = name[0].lower() + name[1:]
    helpers = r.sample(["Validate", "Enrich", "Persist", "Publish", "Measure", "Normalise"], r.randint(2, 4))
    lines = [
        "using System;", "using System.Threading;", "using System.Threading.Tasks;", "using Quillfeather.Platform;",
        f"using Quillfeather.Services.{ns}.Models;", "",
        f"namespace Quillfeather.Services.{ns}.Handlers;", "",
        "/// <summary>",
        f"/// Handles {name} requests for {svc}. {r.choice(['Idempotent by request id.', 'Safe to retry.', 'Called from the queue consumer.', 'Called from the HTTP front door.'])}",
        "/// </summary>",
        f"public sealed class {name}Handler",
        "{",
        "    private readonly IClock _clock;",
        "    private readonly ILog _log;",
        "    private readonly IMetrics _metrics;",
        "",
        f"    public {name}Handler(IClock clock, ILog log, IMetrics metrics)",
        "    {",
        "        _clock = clock;",
        "        _log = log;",
        "        _metrics = metrics;",
        "    }",
        "",
        f"    public async Task<HandlerResult> HandleAsync({name}Request request, CancellationToken ct)",
        "    {",
        "        if (request is null)",
        "        {",
        '            return HandlerResult.Rejected("request is required");',
        "        }",
        "",
        "        var started = _clock.UtcNow;",
        f'        _log.Info("{lo} received", request.Id);',
        "",
        "        foreach (var line in request.Lines)",
        "        {",
        "            ct.ThrowIfCancellationRequested();",
        "            if (line.Quantity <= 0)",
        "            {",
        f'                _metrics.Increment("{svc}.{lo}.skipped");',
        "                continue;",
        "            }",
        "",
    ]
    for h in helpers:
        lines.append(f"            await {h}Async(line, ct).ConfigureAwait(false);")
    lines += [
        "        }",
        "",
        f'        _metrics.Observe("{svc}.{lo}.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);',
        "        return HandlerResult.Ok();",
        "    }",
    ]
    for h in helpers:
        body = r.choice([
            [f'_log.Debug("{h.lower()} line", line.Sku);', "return Task.CompletedTask;"],
            ["if (string.IsNullOrWhiteSpace(line.Sku))", "{", '    throw new ArgumentException("sku is required", nameof(line));', "}",
             "", "return Task.CompletedTask;"],
            [f'_metrics.Increment("{svc}.{lo}.{h.lower()}");', "return Task.CompletedTask;"],
            ["var note = line.Note ?? string.Empty;", "if (note.Length > 200)", "{",
             f'    _log.Info("{h.lower()} note truncated", line.Sku);', "}", "", "return Task.CompletedTask;"],
        ])
        lines += ["", f"    private Task {h}Async({name}Line line, CancellationToken ct)", "    {"]
        lines += [("        " + b) if b else "" for b in body]
        lines += ["    }"]
    lines.append("}")
    return "\n".join(lines)


def models_cs(svc, names):
    ns = cs_ns(svc)
    lines = ["using System;", "using System.Collections.Generic;", "", f"namespace Quillfeather.Services.{ns}.Models;"]
    for n in names:
        rec = f"public sealed record {n}Request(string Id, IReadOnlyList<{n}Line> Lines, DateTimeOffset ReceivedAt);"
        if len(rec) > 120:
            lines += ["", f"public sealed record {n}Request(", "    string Id,", f"    IReadOnlyList<{n}Line> Lines,",
                      "    DateTimeOffset ReceivedAt", ");"]
        else:
            lines += ["", rec]
        lines += ["", f"public sealed record {n}Line(string Sku, int Quantity, string? Note);"]
    return "\n".join(lines)


def startup_cs(svc, S, r):
    ns = cs_ns(svc)
    lines = ["using System;", "using Quillfeather.Platform;", "", f"namespace Quillfeather.Services.{ns};", "",
             "public static class Startup", "{",
             "    public static void ConfigureMesh(MeshOptions o, MeshEnvironment env, IFeatureFlags flags)", "    {",
             "        // Runs after every configuration layer has been merged (see docs/CONFIG.md).",
             ""]
    for i, op in enumerate(S["code"]):
        if i:
            lines.append("")
        lines += op_lines(op)
    lines += ["    }", "",
              "    public static void ConfigureAdmin(AdminOptions a, MeshEnvironment env)", "    {",
              f"        a.Port = {S['admin_port']};",
              "        if (env.IsProduction)", "        {", f"            a.MaxConnections = {r.choice([4, 6, 8])};", "        }",
              "    }", "",
              "    public static void ConfigureTelemetry(TelemetryOptions t)", "    {",
              f'        t.ServiceName = "{svc}";',
              f'        t.Tags["tier"] = "{r.choice(["gold", "silver", "bronze"])}";',
              "    }", "}"]
    return "\n".join(lines)


for svc, S in W["services"].items():
    r = random.Random(f"{CFG['seed']}-cs-{svc}")
    names = []
    while len(names) < CFG["handlers_per_service"]:
        n = r.choice(VERBS) + r.choice(NOUNS)
        if n not in names:
            names.append(n)
    for n in names:
        emit(f"services/{svc}/src/Handlers/{n}Handler.cs", handler_cs(svc, n, r))
    emit(f"services/{svc}/src/Models/Requests.cs", models_cs(svc, names))
    emit(f"services/{svc}/src/Startup.cs", startup_cs(svc, S, r))
    emit(f"services/{svc}/README.md", f"""# {svc}

{svc} is one of the services in the Quillfeather mesh. It exposes {", ".join(names)} handlers.

## Running locally

Use the platform launcher with `--service {svc} --env dev`. Configuration is merged from the layers
described in `docs/CONFIG.md`; the service's own settings are in `service.yaml` and its start-up code in
`src/Startup.cs`.

## Recent changes

{F.agenda(4)}

{F.pad(CFG["padding"])}
""")

EMPTY_STR = ' = "";'
MESH_PROPS = "\n\n".join(
    f"    public {'string' if KEYS[k][0] == 'str' else 'int'} {pascal(k)} {{ get; set; }}{EMPTY_STR if KEYS[k][0] == 'str' else ''}"
    for k in KEYS)
PLATFORM = {
    "platform/MeshOptions.cs": """namespace Quillfeather.Platform;

/// <summary>
/// Settings bound from the <c>mesh:</c> section of the merged configuration. Keys are snake_case in
/// YAML and PascalCase here (for example <c>max_connections</c> binds to <see cref="MaxConnections"/>).
/// </summary>
public sealed class MeshOptions
{
""" + MESH_PROPS + "\n}",
    "platform/AdminOptions.cs": """namespace Quillfeather.Platform;

/// <summary>
/// Settings for the admin listener, bound from the <c>admin:</c> section. These never affect mesh traffic.
/// </summary>
public sealed class AdminOptions
{
    public int Port { get; set; }

    public int MaxConnections { get; set; }

    public int RequestTimeoutMs { get; set; }
}""",
    "platform/MeshEnvironment.cs": """using System;

namespace Quillfeather.Platform;

public sealed class MeshEnvironment
{
    // Staging runs production-shaped traffic, so it counts as production for start-up decisions.
    private static readonly string[] ProductionLike = ["staging", "prod"];

    public MeshEnvironment(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public bool IsProduction => Array.IndexOf(ProductionLike, Name) >= 0;
}""",
    "platform/FeatureFlags.cs": """using System.Collections.Generic;

namespace Quillfeather.Platform;

public interface IFeatureFlags
{
    bool IsOn(string name);
}

/// <summary>
/// Flag values come from flags/catalog.json (the default of each flag), then flags/&lt;env&gt;.jsonc, whose
/// entries replace the defaults. Lines that start with // in the .jsonc file are comments and are skipped.
/// </summary>
public sealed class FileFeatureFlags : IFeatureFlags
{
    private readonly IReadOnlyDictionary<string, bool> _values;

    public FileFeatureFlags(IReadOnlyDictionary<string, bool> values)
    {
        _values = values;
    }

    public bool IsOn(string name) => _values.TryGetValue(name, out var on) && on;
}""",
    "platform/ConfigLoader.cs": """using System.Collections.Generic;

namespace Quillfeather.Platform;

/// <summary>
/// Builds the ordered list of YAML layers for one service in one environment. Later layers win, key by
/// key, inside the <c>mesh:</c> section. Lines starting with # are YAML comments and set nothing.
/// </summary>
public sealed class ConfigLoader
{
    private readonly IYamlReader _yaml;

    public ConfigLoader(IYamlReader yaml)
    {
        _yaml = yaml;
    }

    public IReadOnlyList<string> LayersFor(string service, string environment)
    {
        var serviceFile = $"services/{service}/service.yaml";
        var layers = new List<string> { "config/defaults.yaml" };
        foreach (var include in _yaml.ReadIncludes(serviceFile))
        {
            AddFragment(layers, $"config/{include}");
        }

        layers.Add(serviceFile);
        layers.Add($"env/{environment}/global.yaml");
        layers.Add($"env/{environment}/services/{service}.yaml");
        return layers;
    }

    private void AddFragment(List<string> layers, string fragment)
    {
        // A fragment's own includes go first, so the fragment can override what it includes.
        foreach (var nested in _yaml.ReadIncludes(fragment))
        {
            AddFragment(layers, $"config/{nested}");
        }

        layers.Add(fragment);
    }
}""",
    "platform/MeshHost.cs": """namespace Quillfeather.Platform;

public static class MeshHost
{
    /// <summary>
    /// Start-up order: merge the YAML layers (ConfigLoader), bind the mesh section to MeshOptions, load
    /// feature flags, then call the service's Startup.ConfigureMesh, which has the last word.
    /// </summary>
    public static MeshOptions Build(
        ConfigLoader loader,
        IOptionsBinder binder,
        IFeatureFlags flags,
        MeshEnvironment env,
        IServiceStartup startup,
        string service
    )
    {
        var options = binder.BindMesh(loader.LayersFor(service, env.Name));
        startup.ConfigureMesh(options, env, flags);
        return options;
    }
}""",
}
for rel, text in PLATFORM.items():
    emit(rel, text)

emit("docs/CONFIG.md", f"""# Configuration layers

{F.template_sections()}

## How a service's mesh settings are resolved

{F.policy(3)}

The loader starts from `config/defaults.yaml`. It then applies the shared fragments that the service
lists under `include:` in its `service.yaml`, in the order they are listed, so a later fragment overrides
an earlier one; when a fragment itself has an `include:` list, those files are applied first and the
fragment's own values then override them. After the fragments comes the service's own `service.yaml`,
then the environment overlay `env/<env>/global.yaml`, then the per-service overlay
`env/<env>/services/<service>.yaml`. Each layer overrides earlier ones key by key. Only keys under
`mesh:` configure mesh traffic; `admin:` belongs to the admin listener and never changes a mesh setting.
A line that is commented out (YAML `#`, JSONC `//`, C# `//`) is not configuration, whatever it says.

Once the layers are merged, the host calls the service's `Startup.ConfigureMesh` (in
`services/<service>/src/Startup.cs`). Code there runs last and can change any setting, often
conditionally on the environment (`env.Name`, `env.IsProduction`; see `platform/MeshEnvironment.cs`),
on a feature flag (`docs/FLAGS.md`) or on another merged setting. Statements run top to bottom.

{F.policy(4)}

## Naming

YAML keys are snake_case; the matching `MeshOptions` properties are PascalCase (`max_connections` is
`MaxConnections`).

{F.pad(CFG["padding"])}
""")
emit("docs/FLAGS.md", f"""# Feature flags

{F.policy(3)}

Every flag is declared in `flags/catalog.json` with a default. An environment file `flags/<env>.jsonc`
may set a flag for that environment; its value replaces the catalog default there. A flag absent from
the environment file keeps its default. Commented-out lines in the `.jsonc` files are ignored.

{F.pad(CFG["padding"])}
""")
for doc in ["ONBOARDING", "RELEASES", "ARCHITECTURE"]:
    emit(f"docs/{doc}.md", f"# {doc.capitalize()}\n\n{F.agenda(6)}\n\n{F.status_table()}\n\n{F.pad(CFG['padding'] + 1)}\n")
emit("README.md", f"""# Quillfeather mesh

Monorepo for the Quillfeather service mesh: {len(svc_names)} services under `services/`, shared configuration
under `config/`, environment overlays under `env/`, feature flags under `flags/`, the platform library
under `platform/` and documentation under `docs/` (start with `docs/CONFIG.md`).

{F.policy(4)}
""")
for ci in ["build", "deploy", "nightly"]:
    emit(f"ci/{ci}.yaml", "\n".join([f"# {ci} pipeline", comment_block(2), "", "stages:"] +
                                     [f"  - name: {s}\n    timeout_minutes: {F.r.choice([10, 20, 30])}"
                                      for s in F.r.sample(["restore", "compile", "unit", "package", "publish", "smoke", "notify"], 4)]))

out = TASK / "fixtures"
if out.exists():
    shutil.rmtree(out)
total = 0
for rel, text in files.items():
    p = out / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    total += len(text.encode("utf-8"))


# ------------------------------------------------------------------ key, report, task.md, meta
def qtext(q):
    k, s, e = q["spec"]["key"], q["svc"], q["spec"]["env"]
    return (f"What is the effective mesh setting `{k}` of service `{s}` in environment `{e}`, "
            f"once every configuration layer and the service's start-up code have been applied?")


key = {"task": "cb1", "source": "synthetic", "questions": [
    {"id": f"q{i + 1}", "kind": "int" if KEYS[q["spec"]["key"]][0] == "int" else "norm", "question": qtext(q),
     "answers": [str(gold[f"q{i + 1}"])], "evidence": evid[f"q{i + 1}"], "trap_flips": flips[f"q{i + 1}"]}
    for i, q in enumerate(questions)]}
with open(HERE / "key.json", "w", encoding="utf-8", newline="\n") as f:
    json.dump(key, f, indent=2)
    f.write("\n")
n = len(questions)
predicted = {t: round(sum(1 for q in gold if t not in flips[q]) / n, 4) for t in TRAPS}
counts = {
    "services/*/service.yaml": len(W["services"]), "services/*/src/Startup.cs": len(W["services"]),
    "config/shared/*.yaml": len(W["frags"]), "env/*/global.yaml": len(ENVS), "flags/*.jsonc": len(ENVS),
    "docs/*.md": 5, "platform/*.cs": len(PLATFORM),
}
report = {"task": "cb1", "fixture_files": len(files), "fixture_bytes": total, "approx_tokens": total // 4,
          "gold": gold, "trap_answers": {t: {q: str(v) for q, v in a.items()} for t, a in trap_answers.items()},
          "trap_flips": flips, "predicted_trap_scores": predicted,
          "targets": {f"q{i + 1}": {"service": q["svc"], **q["spec"], "attempt": q["attempt"]} for i, q in enumerate(questions)},
          "counts": counts}
with open(HERE / "build-report.json", "w", encoding="utf-8", newline="\n") as f:
    json.dump(report, f, indent=2)
    f.write("\n")

qs = "\n".join(f"- **q{i + 1}.** {qtext(q)}" for i, q in enumerate(questions))
msg = f"""Project code name: {{SEED}}.

This workspace is a checkout of the Quillfeather mesh, a (fictional) service-mesh monorepo: {len(files)} files
of YAML/JSON configuration, C# start-up code and docs under `config/`, `services/`, `env/`, `flags/`,
`platform/` and `docs/`. Nothing in it is from a real project, so the answers are only in these files;
general knowledge will not help. Much of it is routine boilerplate.

A service's effective settings are assembled from several files and then adjusted by its start-up code.
How the layers combine is documented in the repo itself (start with `docs/CONFIG.md`). Read the actual
files; do not assume.

Use the file and shell tools (Glob, Grep, Read, Bash) to find and read what you need.

Write `answers.json` in the workspace root:

```json
{{
  "q1": {{ "answer": "<value>", "evidence": ["services/.../service.yaml", "..."] }},
  "q2": {{ "answer": "...", "evidence": ["..."] }}
}}
```

- `answer` is the value exactly as the setting would hold it: a whole number for numeric settings, the
  bare word for text settings (for example `gzip`).
- `evidence` lists the files the answer rests on; it is optional and not scored.
- Include every question from q1 to q{n}. If you cannot pin one down, give your best guess anyway.

Questions:

{qs}

When `answers.json` is written, reply with one line per question: `qN: <answer>`.
"""
header = f"""# cb1 — effective-config tracing in a fictional service-mesh repo (synthetic)

Family `config-tracing`, split `dev`. Workspace = `fixtures/` ({len(files)} files, {total / 1e6:.2f} MB,
~{total // 4 // 1000}k tokens; mostly handler/model boilerplate and padded docs). {n} questions "effective
value of X for service Y in env Z". Each answer rests on 3+ files: defaults < shared fragments (listed order,
nested includes first) < service.yaml < env global overlay < env service overlay < Startup.ConfigureMesh
(conditional on env / IsProduction (staging+prod) / feature flags (catalog default < env .jsonc) / another
setting). Every question has a commented-out override that would change it and a code step that changes it.
Checker: exact integer or normalised word; score = correct / {n}. Trap flips and predicted trap-only
scores: `hidden/build-report.json`. Generator + knobs: `hidden/gen_repo.py`, `hidden/build.json`."""
with open(TASK / "task.md", "w", encoding="utf-8", newline="\n") as f:
    f.write(header + "\n\n---\n\n" + msg)
meta_path = TASK / "meta.json"
meta = json.load(open(meta_path, encoding="utf-8")) if meta_path.exists() else {
    "family": "config-tracing", "split": "dev", "seeds": ["aurora", "basalt", "cascade", "delta", "ember"],
    "timeoutMinutes": 45}
meta["requiredFixtures"] = [{"glob": g, "count": c} for g, c in counts.items()]
with open(meta_path, "w", encoding="utf-8", newline="\n") as f:
    json.dump(meta, f, indent=2)
    f.write("\n")
print(json.dumps({"files": len(files), "bytes": total, "gold": gold, "flips": flips, "predicted": predicted,
                  "attempts": [q["attempt"] for q in questions]}, indent=1))
