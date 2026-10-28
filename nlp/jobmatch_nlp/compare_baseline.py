"""Offline matching evaluation: semantic ranker (MiniLM, as deployed) vs keyword-overlap baseline.

Dissertation Section 3.3.3 / Chapter 5. Two steps:

  1. python -m jobmatch_nlp.compare_baseline make-sheet
     Ranks every sample profile against the whole sample job board with both methods, pools each
     profile's top-3 results, shuffles them and writes a BLIND labelling sheet (no method, rank or
     score shown) plus a separate key file. Also reports how many jobs survive the location and
     availability filter for each profile (coverage under the deployed stage 1).

  2. Fill the 'relevance' column of labelling_sheet.csv (0, 1 or 2; rubric in the sheet) and, for
     about a third of the rows, ask a second rater to fill 'relevance_rater2'. Then:
     python -m jobmatch_nlp.compare_baseline score

Outputs go to reports/matching_comparison/.

Why the whole job board: ranking the whole board tests the ranking itself; the location filter's
effect is reported as coverage. Both rankers receive exactly the same candidates.

The board is both synthetic seed files (152 jobs, DL-020): v1 (32 jobs) left 11 of the 25 profiles
with no result from either method and only 20 pairs to judge. v2 adds one job per taxonomy skill,
so every profile has jobs needing its exact skills and many more in its trade, which is what a
ranking comparison needs. Each profile's situation on this board is computed and stored in the key
('board'); the hand-written 'case' notes describe why the profile was chosen for the v1 board.
"""
from __future__ import annotations

import csv
import json
import math
import random
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]           # nlp/
REPO = ROOT.parent
OUT = ROOT / "reports" / "matching_comparison"
JOB_FILES = [REPO / "src" / "JobMatch.Api" / "Seed" / f"jobs_synthetic_{v}.json" for v in ("v1", "v2")]
TAXONOMY = ROOT / "data" / "taxonomy_v1.json"
LOCALITIES = ROOT / "data" / "localities_v1.json"
ADJACENCY = ROOT / "data" / "district_adjacency_v1.json"
SEED = 13
TOP_K = 3
MIN_SCORE = 0.55   # Matching:MinScore, as deployed

# Sample worker profiles: canonical taxonomy skills, as the dialogue would store them after
# NLU resolution. 'case' records why each profile was included when the board had only the
# v1 jobs (kept for the record; not shown to the rater). See the computed 'board' note instead.
PROFILES = [
    ("P01", "construction", ["Block laying", "Concrete work"], ["Madina"], "Full-time", "exact skills on the board"),
    ("P02", "construction", ["Tiling", "Painting & decorating"], ["Tema"], "Part-time", "two exact skills, two jobs"),
    ("P03", "construction", ["Steel bending", "Setting out & plan reading"], ["Kasoa"], "Full-time", "related skills, no exact job"),
    ("P04", "construction", ["Roofing"], ["Dome"], "Full-time", "related job in another trade (roof carpentry)"),
    ("P05", "woodwork", ["Furniture making", "Upholstery"], ["Kaneshie"], "Full-time", "exact skill"),
    ("P06", "woodwork", ["Door & window frames", "Formwork (shuttering)"], ["Adenta"], "Part-time", "one exact, one cross-trade"),
    ("P07", "woodwork", ["Furniture repair"], ["Dansoman"], "Flexible", "related skill, no exact job"),
    ("P08", "electrical", ["House wiring", "Electrical fault finding"], ["Adenta"], "Full-time", "exact skill"),
    ("P09", "electrical", ["Solar installation", "Meter & distribution board installation"], ["Accra Central"], "Full-time", "exact skill"),
    ("P10", "electrical", ["Electronics repair (TV, radio)"], ["Accra Central"], "Full-time", "related repair jobs only"),
    ("P11", "agriculture", ["Crop farming", "Farm labour"], ["Tema"], "Full-time", "exact skill, distant area"),
    ("P12", "plumbing_metal", ["Pipe fitting", "Water tank & pump installation"], ["Madina"], "Full-time", "no job in this trade"),
    ("P13", "plumbing_metal", ["Arc welding", "Gates & burglar-proof fabrication"], ["Ashaiman"], "Full-time", "no job in this trade"),
    ("P14", "vehicle_repair", ["Engine repair", "Car servicing"], ["Kaneshie"], "Full-time", "no job in this trade"),
    ("P15", "transport", ["Motorbike riding (okada)"], ["Madina"], "Full-time", "exact skill"),
    ("P16", "transport", ["Taxi & ride-hailing driving"], ["Tema"], "Full-time", "related job (company driver)"),
    ("P17", "textile_fashion", ["Dressmaking"], ["Madina"], "Full-time", "exact skill"),
    ("P18", "textile_fashion", ["Alterations & mending", "Embroidery"], ["Kaneshie"], "Part-time", "related job (tailor)"),
    ("P19", "hair_beauty", ["Braiding", "Wig making & installation"], ["Dansoman"], "Full-time", "exact skill"),
    ("P20", "hair_beauty", ["Make-up", "Lashes & brows"], ["Accra Central"], "Full-time", "same trade, other skills"),
    ("P21", "food_catering", ["Pastries & cakes"], ["Kasoa"], "Full-time", "exact skill"),
    ("P22", "food_catering", ["Grilling (kebab, tilapia)", "Juice & local drinks"], ["Teshie"], "Weekends only", "same trade, other skills"),
    ("P23", "domestic_services", ["Elderly & home care"], ["Adenta"], "Full-time", "related job (nanny)"),
    ("P24", "domestic_services", ["Cooking for a household", "Laundry & ironing"], ["Tema"], "Full-time", "one exact, one cross-trade"),
    ("P25", "trading_sales", ["Phone & accessories sales", "Customer service"], ["Accra Central"], "Full-time", "one exact, one related"),
]


def load():
    jobs = [j for path in JOB_FILES for j in json.loads(path.read_text(encoding="utf-8"))["jobs"]]
    for i, j in enumerate(jobs, start=1):
        j["id"] = i
    tax = json.loads(TAXONOMY.read_text(encoding="utf-8"))
    trade_names = {c["id"]: c["name"] for c in tax["categories"]}
    loc = json.loads(LOCALITIES.read_text(encoding="utf-8"))["localities"]
    district = {}
    for l in loc:
        for name in [l["name"], *l.get("aliases", [])]:
            district.setdefault(name.lower(), l["district"])
    adj_raw = json.loads(ADJACENCY.read_text(encoding="utf-8"))["adjacency"]
    adj = {k: set(v) for k, v in adj_raw.items()}
    for k, vs in list(adj.items()):           # symmetric, as in the gateway
        for v in vs:
            adj.setdefault(v, set()).add(k)
    return jobs, trade_names, district, adj


def availability_ok(worker: str, job: str) -> bool:
    """DL-016: full-time takes any job; part-time fits part-time or weekend jobs;
    weekends-only fits weekend jobs; Flexible (either side) fits everyone."""
    if worker == "Flexible" or job == "Flexible" or worker == "Full-time":
        return True
    if worker == "Part-time":
        return job in ("Part-time", "Weekends only")
    if worker == "Weekends only":
        return job == "Weekends only"
    return worker == job


def stage1(profile, jobs, district, adj):
    missing = [a for a in profile["areas"] if a.lower() not in district]
    if missing:
        raise SystemExit(f"Area not in the gazetteer: {missing}")
    areas = {district[a.lower()] for a in profile["areas"]}
    allowed = set(areas)
    for a in areas:
        allowed |= adj.get(a, set())
    unknown = sorted({j["area"] for j in jobs if j["area"].lower() not in district})
    if unknown:
        raise SystemExit(f"Job areas not in the gazetteer: {unknown}")
    return [j for j in jobs if district[j["area"].lower()] in allowed
            and availability_ok(profile["availability"], j["availability"])]


def keyword_rank(skills, jobs, top_k=TOP_K):
    """Replica of OfflineMatchRanker (MatchRankers.cs): share of the job's required skills the worker has;
    at least one shared skill; ties broken by overlap count, then job id."""
    mine = {s.lower() for s in skills}
    scored = []
    for j in jobs:
        req = j.get("skills", [])
        overlap = [s for s in req if s.lower() in mine]
        if overlap:
            scored.append((len(overlap) / len(req), len(overlap), j["id"]))
    scored.sort(key=lambda x: (-x[0], -x[1], x[2]))
    return [{"id": jid, "rank": n + 1, "score": round(sc, 4)} for n, (sc, _, jid) in enumerate(scored[:top_k])]


def semantic_rank(embedder, trade, skills, jobs, top_k=TOP_K, min_score=MIN_SCORE):
    from .matching import rank
    payload = [{"id": j["id"], "title": j["title"], "description": j.get("description"),
                "required_skills": j.get("skills", [])} for j in jobs]
    kept, _, _ = rank(embedder, trade, skills, payload, top_k=top_k, min_score=min_score)
    return [{"id": r.id, "rank": r.rank, "score": r.score} for r in kept]


def make_sheet():
    from .matching import Embedder
    jobs, trade_names, district, adj = load()
    by_id = {j["id"]: j for j in jobs}
    embedder = Embedder()
    OUT.mkdir(parents=True, exist_ok=True)
    rng = random.Random(SEED)
    rows, key, coverage = [], {}, []
    for pid, trade_id, skills, areas, avail, case in PROFILES:
        prof = {"areas": areas, "availability": avail}
        trade = trade_names[trade_id]
        sem = semantic_rank(embedder, trade, skills, jobs)
        kw = keyword_rank(skills, jobs)
        # deployed behaviour, for coverage: stage 1 then each ranker
        cand = stage1(prof, jobs, district, adj)
        coverage.append({"profile": pid, "candidates_after_stage1": len(cand),
                         "semantic_returned": len(semantic_rank(embedder, trade, skills, cand)) if cand else 0,
                         "keyword_returned": len(keyword_rank(skills, cand)) if cand else 0})
        pooled = {}
        for method, res in (("semantic", sem), ("keyword", kw)):
            for r in res:
                pooled.setdefault(r["id"], {})[method] = {"rank": r["rank"], "score": r["score"]}
        exact = sum(1 for j in jobs if set(skills) & set(j.get("skills", [])))
        same_trade = sum(1 for j in jobs if j["trade"] == trade_id)
        board = f"{exact} job(s) need these exact skills; {same_trade} job(s) in the trade"
        key[pid] = {"trade": trade, "skills": skills, "case": case, "board": board,
                    "semantic": sem, "keyword": kw, "pooled": pooled}
        ids = list(pooled)
        rng.shuffle(ids)
        for jid in ids:
            j = by_id[jid]
            rows.append({
                "pair_id": f"{pid}-J{jid:02d}",
                "worker_trade": trade,
                "worker_skills": "; ".join(skills),
                "job_title": j["title"],
                "job_description": j.get("description", ""),
                "job_skills": "; ".join(j.get("skills", [])),
                "relevance": "",
                "relevance_rater2": "",
                "comment": "",
            })
    rng.shuffle(rows)                                   # also shuffle across profiles
    with open(OUT / "labelling_sheet.csv", "w", newline="", encoding="utf-8-sig") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0]))
        w.writeheader(); w.writerows(rows)
    (OUT / "key_DO_NOT_OPEN_BEFORE_LABELLING.json").write_text(json.dumps(key, indent=2), encoding="utf-8")
    (OUT / "coverage_stage1.json").write_text(json.dumps(coverage, indent=2), encoding="utf-8")
    (OUT / "RUBRIC.txt").write_text(
        "Score each row in the 'relevance' column, judging only the worker's trade and skills against the job:\n"
        "  2 = relevant: the worker could do this job with the skills listed\n"
        "  1 = partly relevant: same or neighbouring trade, but the job mainly needs skills the worker did not list\n"
        "  0 = not relevant: different trade, or the skills do not fit\n"
        "Ignore location, pay and availability. Do not open the key file until every row is scored.\n"
        "A second rater scores about a third of the rows in 'relevance_rater2' without seeing your scores.\n",
        encoding="utf-8")
    print(f"{len(rows)} pairs for {len(PROFILES)} profiles from {len(jobs)} jobs -> {OUT / 'labelling_sheet.csv'}")


def ndcg_at_k(gains, ideal, k=TOP_K):
    dcg = sum((2 ** g - 1) / math.log2(i + 2) for i, g in enumerate(gains[:k]))
    idcg = sum((2 ** g - 1) / math.log2(i + 2) for i, g in enumerate(sorted(ideal, reverse=True)[:k]))
    return dcg / idcg if idcg > 0 else None


def wilcoxon(diffs):
    """Wilcoxon signed-rank test, normal approximation with tie correction (two-sided)."""
    d = [x for x in diffs if x != 0]
    n = len(d)
    if n == 0:
        return {"n_nonzero": 0, "W": None, "p": None}
    order = sorted(range(n), key=lambda i: abs(d[i]))
    ranks = [0.0] * n
    i = 0
    while i < n:
        j = i
        while j + 1 < n and abs(d[order[j + 1]]) == abs(d[order[i]]):
            j += 1
        for k in range(i, j + 1):
            ranks[order[k]] = (i + j) / 2 + 1
        i = j + 1
    w_plus = sum(r for r, x in zip(ranks, d) if x > 0)
    mean = n * (n + 1) / 4
    ties = {}
    for x in d:
        ties[abs(x)] = ties.get(abs(x), 0) + 1
    var = n * (n + 1) * (2 * n + 1) / 24 - sum(t ** 3 - t for t in ties.values()) / 48
    z = (w_plus - mean) / math.sqrt(var) if var > 0 else 0.0
    p = math.erfc(abs(z) / math.sqrt(2))
    return {"n_nonzero": n, "W_plus": w_plus, "z": round(z, 3), "p_approx": round(p, 4)}


def kappa(a, b, cats=(0, 1, 2), weighted=False):
    n = len(a)
    if n == 0:
        return None
    k = len(cats)
    idx = {c: i for i, c in enumerate(cats)}
    obs = [[0] * k for _ in range(k)]
    for x, y in zip(a, b):
        obs[idx[x]][idx[y]] += 1
    ra = [sum(r) for r in obs]
    cb = [sum(obs[i][j] for i in range(k)) for j in range(k)]
    wgt = lambda i, j: ((i - j) ** 2 / (k - 1) ** 2) if weighted else (0 if i == j else 1)
    po = sum(wgt(i, j) * obs[i][j] for i in range(k) for j in range(k)) / n
    pe = sum(wgt(i, j) * ra[i] * cb[j] for i in range(k) for j in range(k)) / (n * n)
    return round(1 - po / pe, 3) if pe > 0 else None


def score():
    key = json.loads((OUT / "key_DO_NOT_OPEN_BEFORE_LABELLING.json").read_text(encoding="utf-8"))
    rel, r1, r2 = {}, [], []
    with open(OUT / "labelling_sheet.csv", encoding="utf-8-sig") as f:
        for row in csv.DictReader(f):
            if row["relevance"].strip() == "":
                sys.exit(f"Unscored row {row['pair_id']}: score every row first.")
            v = int(row["relevance"]); rel[row["pair_id"]] = v
            if row.get("relevance_rater2", "").strip():
                r1.append(v); r2.append(int(row["relevance_rater2"]))
    per_profile, totals = [], {m: {"p3": [], "prec": [], "ndcg": [], "covered": 0, "returned": 0, "relevant": 0}
                               for m in ("semantic", "keyword")}
    for pid, k in key.items():
        pool_gains = [rel[f"{pid}-J{int(j):02d}"] for j in k["pooled"]]
        entry = {"profile": pid, "case": k["case"], "board": k.get("board"), "best_available": max(pool_gains) if pool_gains else 0}
        for m in ("semantic", "keyword"):
            gains = [rel[f"{pid}-J{r['id']:02d}"] for r in k[m]]
            t = totals[m]
            t["returned"] += len(gains); t["relevant"] += sum(1 for g in gains if g == 2)
            if gains:
                t["covered"] += 1
            p3 = sum(1 for g in gains if g == 2) / TOP_K
            prec = (sum(1 for g in gains if g == 2) / len(gains)) if gains else None
            nd = ndcg_at_k(gains, pool_gains)
            t["p3"].append(p3); t["prec"].append(prec); t["ndcg"].append(nd)
            entry[m] = {"returned": len(gains), "gains": gains, "p_at_3": round(p3, 3),
                        "ndcg_at_3": None if nd is None else round(nd, 3)}
        per_profile.append(entry)
    mean = lambda xs: round(sum(x for x in xs if x is not None) / max(1, len([x for x in xs if x is not None])), 3)
    summary = {}
    for m, t in totals.items():
        summary[m] = {"profiles": len(key), "profiles_with_a_result": t["covered"],
                      "jobs_returned": t["returned"], "relevant_returned": t["relevant"],
                      "precision_of_returned": round(t["relevant"] / t["returned"], 3) if t["returned"] else None,
                      "mean_p_at_3": mean(t["p3"]), "mean_ndcg_at_3": mean([x if x is not None else 0 for x in t["ndcg"]])}
    nd_s = [x if x is not None else 0 for x in totals["semantic"]["ndcg"]]
    nd_k = [x if x is not None else 0 for x in totals["keyword"]["ndcg"]]
    result = {
        "summary": summary,
        "wilcoxon_ndcg_semantic_minus_keyword": wilcoxon([a - b for a, b in zip(nd_s, nd_k)]),
        "wilcoxon_p3_semantic_minus_keyword": wilcoxon([a - b for a, b in zip(totals["semantic"]["p3"], totals["keyword"]["p3"])]),
        "inter_rater": {"pairs_double_rated": len(r1), "cohen_kappa": kappa(r1, r2),
                        "weighted_kappa_quadratic": kappa(r1, r2, weighted=True)},
        "per_profile": per_profile,
        "coverage_stage1": json.loads((OUT / "coverage_stage1.json").read_text(encoding="utf-8")),
        "settings": {"job_board": [p.name for p in JOB_FILES], "top_k": TOP_K, "min_score_semantic": MIN_SCORE, "relevant_means": "score 2",
                     "ndcg_gain": "2^rel - 1, ideal from the pooled judgements"},
    }
    (OUT / "results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({k: result[k] for k in ("summary", "wilcoxon_ndcg_semantic_minus_keyword",
                                             "wilcoxon_p3_semantic_minus_keyword", "inter_rater")}, indent=2))


if __name__ == "__main__":
    {"make-sheet": make_sheet, "score": score}.get(sys.argv[1] if len(sys.argv) > 1 else "", lambda: sys.exit(
        "usage: python -m jobmatch_nlp.compare_baseline make-sheet | score"))()
