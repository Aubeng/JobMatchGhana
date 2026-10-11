"""Dataset loading and the train / validation / test split.

Each utterance is assigned to a split by a hash of its normalised text, not by random shuffling.
The assignment is reproducible without storing a seed, and **stable as the dataset grows**: adding
or editing other lines never moves an existing utterance between splits, so the held-out test set
stays held out across DSR iterations (v1 synthetic -> v2 with pilot data).
"""
from __future__ import annotations

import hashlib
import json
from collections import Counter
from pathlib import Path

from . import config
from .annotation import Example, load_examples, to_markup

TEST_BUCKETS = {0}       # 10%
VAL_BUCKETS = {1}        # 10%
SPLITS = ("train", "val", "test")


def split_of(text: str) -> str:
    key = " ".join(text.lower().split()).encode("utf-8")
    bucket = int(hashlib.sha256(key).hexdigest(), 16) % 10
    if bucket in TEST_BUCKETS:
        return "test"
    if bucket in VAL_BUCKETS:
        return "val"
    return "train"


def load_dataset(directory: Path = config.UTTERANCES_DIR) -> dict[str, list[Example]]:
    examples, errors = load_examples(directory)
    if errors:
        raise ValueError("training data has errors; run `python -m jobmatch_nlp.validate_data`:\n  "
                         + "\n  ".join(errors[:20]))
    out: dict[str, list[Example]] = {s: [] for s in SPLITS}
    for ex in examples:
        out[split_of(ex.text)].append(ex)
    return out


def write_split_manifest(splits: dict[str, list[Example]], directory: Path = config.SPLITS_DIR) -> dict:
    """Record exactly which utterances were in each split (reproducibility appendix material)."""
    directory.mkdir(parents=True, exist_ok=True)
    summary = {}
    for name, exs in splits.items():
        with (directory / f"{name}.jsonl").open("w", encoding="utf-8") as f:
            for ex in exs:
                f.write(json.dumps({"intent": ex.intent, "annotated": to_markup(ex.text, ex.spans),
                                    "source": ex.source}, ensure_ascii=False) + "\n")
        summary[name] = {"total": len(exs), "per_intent": dict(sorted(Counter(e.intent for e in exs).items()))}
    (directory / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    return summary
