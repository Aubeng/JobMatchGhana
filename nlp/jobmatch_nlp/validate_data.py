"""Quality checks and statistics for the training utterances.

    python -m jobmatch_nlp.validate_data            # report; exit 1 on errors
    python -m jobmatch_nlp.validate_data --review   # also write data/review/review_sheet.csv

Checks: markup parses, labels are valid, spans align to words, no duplicates (within or across
intents), each intent has at least --min examples, and every SKILL_ITEM / TRADE_CATEGORY /
LOCATION span resolves against the taxonomy or gazetteer. Unresolved spans are warnings: they
are either a missing synonym (add it to the taxonomy) or a labelling mistake.
"""
from __future__ import annotations

import argparse
import csv
import sys
from collections import Counter, defaultdict

from . import config
from .annotation import ENTITY_LABELS, INTENTS, load_examples, to_markup
from .duration import to_years
from .resolve import Gazetteer, Taxonomy


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--min", type=int, default=150, help="minimum examples per intent (S&D: 150)")
    ap.add_argument("--review", action="store_true", help="write a CSV review sheet for the researcher")
    ap.add_argument("--show-unresolved", type=int, default=25, help="how many unresolved spans to list")
    args = ap.parse_args(argv)

    examples, errors = load_examples(config.UTTERANCES_DIR)
    taxonomy = Taxonomy.load(config.TAXONOMY_PATH)
    gazetteer = Gazetteer.load(config.DISTRICTS_PATH, config.LOCALITIES_PATH)

    # Duplicates: the same utterance twice is wasted data; under two intents it is a
    # contradiction the classifier cannot learn.
    seen: dict[str, str] = {}
    for ex in examples:
        key = " ".join(ex.text.lower().split())
        if key in seen:
            errors.append(f"{ex.source}: duplicate of {seen[key]}: {ex.text}")
        else:
            seen[key] = ex.source

    per_intent = Counter(ex.intent for ex in examples)
    for intent in INTENTS:
        if per_intent[intent] < args.min:
            errors.append(f"{intent}: {per_intent[intent]} examples (< {args.min})")

    per_label = Counter(s.label for ex in examples for s in ex.spans)
    unresolved: dict[str, list[str]] = defaultdict(list)
    skill_hits: Counter[str] = Counter()
    category_hits: Counter[str] = Counter()
    durations_parsed = 0
    for ex in examples:
        for s in ex.spans:
            if s.label == "SKILL_ITEM":
                m = taxonomy.resolve_skill(s.text)
                if m:
                    skill_hits[m.skill_id] += 1
                    category_hits[m.category_id] += 1
                else:
                    unresolved[s.label].append(f"{ex.source}: {s.text}")
            elif s.label == "TRADE_CATEGORY":
                m = taxonomy.resolve_category(s.text)
                if m:
                    category_hits[m.category_id] += 1
                else:
                    unresolved[s.label].append(f"{ex.source}: {s.text}")
            elif s.label == "LOCATION":
                if not gazetteer.resolve(s.text):
                    unresolved[s.label].append(f"{ex.source}: {s.text}")
            elif s.label == "EXPERIENCE_DURATION":
                if to_years(s.text) is not None:
                    durations_parsed += 1

    total_skills = sum(len(c["skills"]) for c in taxonomy.categories.values())
    print(f"Utterances: {len(examples)}  (taxonomy {taxonomy.version}, gazetteer {gazetteer.version})")
    print("\nPer intent:")
    for intent in INTENTS:
        print(f"  {intent:<20} {per_intent[intent]:>5}")
    print("\nEntity spans:")
    for label in ENTITY_LABELS:
        print(f"  {label:<20} {per_label[label]:>5}   unresolved: {len(unresolved[label])}")
    if per_label["EXPERIENCE_DURATION"]:
        print(f"  EXPERIENCE_DURATION normalised to years: {durations_parsed}/{per_label['EXPERIENCE_DURATION']}")
    exp_only = sum(1 for ex in examples if ex.intent == "SKILL_DECLARE"
                   and ex.spans and all(s.label == "EXPERIENCE_DURATION" for s in ex.spans))
    print(f"  SKILL_DECLARE lines that are experience-only (open guideline decision): {exp_only}")
    print(f"\nTaxonomy coverage: {len(skill_hits)}/{total_skills} skills, "
          f"{len(category_hits)}/{len(taxonomy.categories)} categories mentioned")
    thin = sorted(c for c in taxonomy.categories if category_hits[c] < 15)
    if thin:
        print(f"  categories with < 15 mentions: {', '.join(thin)}")

    for label, items in unresolved.items():
        if items:
            print(f"\nUnresolved {label} (first {args.show_unresolved}):")
            for item in items[:args.show_unresolved]:
                print(f"  {item}")

    if args.review:
        out = config.DATA_DIR / "review"
        out.mkdir(exist_ok=True)
        path = out / "review_sheet.csv"
        with path.open("w", newline="", encoding="utf-8-sig") as f:  # BOM: opens cleanly in Excel
            w = csv.writer(f)
            w.writerow(["source", "intent", "annotated", "approved (Y/N)", "corrected intent", "notes"])
            for ex in examples:
                w.writerow([ex.source, ex.intent, to_markup(ex.text, ex.spans), "", "", ""])
        print(f"\nReview sheet: {path}")

    if errors:
        print(f"\nERRORS ({len(errors)}):")
        for e in errors:
            print(f"  {e}")
        return 1
    print("\nOK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
