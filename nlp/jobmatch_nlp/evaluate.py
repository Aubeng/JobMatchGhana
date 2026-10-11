"""End-to-end evaluation of the full pipeline on the held-out test split.

    python -m jobmatch_nlp.evaluate

Complements the per-model reports with the S&D metric *Entity Extraction Precision* as
specified: "the proportion of skill entities extracted that correctly correspond to the stated
skill". It is scored on **resolved canonical skills** (an extracted skill is correct when the gold
annotation resolves to the same taxonomy skill), not on exact span boundaries. It is reported
with and without the lexicon backoff, alongside inference latency.
Writes reports/pipeline_metrics.json.
"""
from __future__ import annotations

import statistics

from . import config
from .data import load_dataset
from .pipeline import NluPipeline
from .training import environment, write_report


def skill_scores(pairs: list[tuple[set[str], set[str]]]) -> dict:
    tp = sum(len(g & p) for g, p in pairs)
    n_pred = sum(len(p) for _, p in pairs)
    n_gold = sum(len(g) for g, _ in pairs)
    precision = tp / n_pred if n_pred else 0.0
    recall = tp / n_gold if n_gold else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    return {"precision": round(precision, 4), "recall": round(recall, 4), "f1": round(f1, 4),
            "extracted": n_pred, "gold": n_gold, "correct": tp}


def main() -> int:
    test = load_dataset()["test"]
    pipe = NluPipeline()
    pipe.analyze("warm up")  # first call pays one-off initialisation costs

    with_backoff, model_only, latencies, intent_hits = [], [], [], 0
    for ex in test:
        gold = {m.skill_id for s in ex.spans if s.label == "SKILL_ITEM"
                for m in [pipe.taxonomy.resolve_skill(s.text)] if m}
        r = pipe.analyze(ex.text)
        latencies.append(r.latency_ms)
        intent_hits += r.intent == ex.intent
        with_backoff.append((gold, {s["skill_id"] for s in r.skills}))
        model_only.append((gold, {s["skill_id"] for s in r.skills if s["source"] == "model"}))

    lat = sorted(latencies)
    p95 = lat[min(len(lat) - 1, int(round(0.95 * (len(lat) - 1))))]
    result = {
        "test_utterances": len(test),
        "intent_accuracy_end_to_end": round(intent_hits / len(test), 4),
        "skill_extraction": {"model_only": skill_scores(model_only),
                             "with_lexicon_backoff": skill_scores(with_backoff)},
        "sd_targets": {"entity_extraction_precision": 0.80, "intent_accuracy": 0.85,
                       "response_latency_p95_seconds": 5.0},
        "nlu_latency_ms": {"mean": round(statistics.mean(lat), 1), "p50": lat[len(lat) // 2],
                           "p95": p95, "max": lat[-1], "threads": int(environment()["threads"])},
        "model_version": pipe.model_version,
        "caveat": "Synthetic test data from the training distribution: an upper bound. NLU latency is "
                  "one component of the S&D end-to-end response latency.",
        "environment": environment(),
    }
    write_report(config.REPORTS_DIR / "pipeline_metrics.json", result)
    sx = result["skill_extraction"]
    print(f"intent accuracy (end to end): {result['intent_accuracy_end_to_end']:.3f}")
    for k, v in sx.items():
        print(f"skill extraction [{k}]: precision {v['precision']:.3f} recall {v['recall']:.3f} "
              f"({v['correct']}/{v['extracted']} extracted correct, {v['gold']} gold)")
    print(f"NLU latency ms: {result['nlu_latency_ms']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
