"""Train the BERT+CRF entity recogniser: SKILL_ITEM, TRADE_CATEGORY, LOCATION,
EXPERIENCE_DURATION (S&D L4; Design §6.3.2).

    python -m jobmatch_nlp.train_ner [--epochs 10] [--lr 3e-5] [--head-lr 1e-3]

Uses every utterance in every intent file: lines without entities teach the model what *not*
to tag. Keeps the epoch with the best validation entity-level F1, evaluates once on the
held-out test split, and writes models/ner/ and reports/ner_metrics.json. The headline S&D
metric, "Entity Extraction Precision", is the micro-averaged entity-level precision.
"""
from __future__ import annotations

import argparse
import copy
import random

import torch
from seqeval.metrics import classification_report, f1_score, precision_score, recall_score
from seqeval.scheme import IOB2
from transformers import AutoTokenizer

from . import config
from .annotation import ENTITY_LABELS, bio_tags
from .data import load_dataset, write_split_manifest
from .ner_model import BertCrfTagger, encode_words
from .training import (Timer, batches, configure_threads, environment, linear_schedule,
                       seed_everything, write_report, write_version)


def gold_and_predicted(model, tokenizer, examples, batch_size: int = 64):
    model.eval()
    gold, pred = [], []
    for batch in batches(examples, batch_size, False, random.Random(0)):
        texts = [e.text for e in batch]
        enc = encode_words(tokenizer, texts, config.MAX_LENGTH)
        tags, _ = model.predict(enc)
        for e, t, n in zip(batch, tags, enc["n_words"]):
            g = bio_tags(e.text, e.spans)
            gold.append(g)
            pred.append(t[:n] + ["O"] * (len(g) - n))  # words cut off by max_length count as "O"
    return gold, pred


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base-model", default=config.NER_BASE_MODEL)
    ap.add_argument("--epochs", type=int, default=10)
    ap.add_argument("--lr", type=float, default=3e-5, help="encoder learning rate")
    ap.add_argument("--head-lr", type=float, default=1e-3, help="classifier + CRF learning rate")
    ap.add_argument("--batch-size", type=int, default=16)
    ap.add_argument("--seed", type=int, default=config.SEED)
    ap.add_argument("--output", default=str(config.NER_MODEL_DIR))
    args = ap.parse_args(argv)

    seed_everything(args.seed)
    threads = configure_threads()
    splits = load_dataset()
    manifest = write_split_manifest(splits)
    train, val, test = splits["train"], splits["val"], splits["test"]
    print(f"train {len(train)} / val {len(val)} / test {len(test)} utterances; {threads} CPU threads")

    tokenizer = AutoTokenizer.from_pretrained(args.base_model)
    model = BertCrfTagger.from_base(args.base_model)
    head_params = list(model.classifier.parameters()) + list(model.crf.parameters())
    optimizer = torch.optim.AdamW([
        {"params": model.encoder.parameters(), "lr": args.lr},
        {"params": head_params, "lr": args.head_lr},
    ], weight_decay=0.01)
    steps_per_epoch = -(-len(train) // args.batch_size)
    scheduler = linear_schedule(optimizer, steps_per_epoch * args.epochs)
    rng = random.Random(args.seed)

    history, best_f1, best_state, best_epoch = [], -1.0, None, 0
    with Timer() as timer:
        for epoch in range(1, args.epochs + 1):
            model.train()
            total = 0.0
            for batch in batches(train, args.batch_size, True, rng):
                enc = encode_words(tokenizer, [e.text for e in batch], config.MAX_LENGTH,
                                   [bio_tags(e.text, e.spans) for e in batch])
                loss = model.loss(enc)
                loss.backward()
                torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
                optimizer.step()
                scheduler.step()
                optimizer.zero_grad()
                total += loss.item() * len(batch)
            gold, pred = gold_and_predicted(model, tokenizer, val)
            f1 = f1_score(gold, pred, mode="strict", scheme=IOB2)
            history.append({"epoch": epoch, "train_loss": round(total / len(train), 4),
                            "val_entity_f1": round(f1, 4)})
            print(f"epoch {epoch}: loss {total / len(train):.4f}  val entity-F1 {f1:.3f}", flush=True)
            if f1 > best_f1:
                best_f1, best_epoch = f1, epoch
                best_state = copy.deepcopy(model.state_dict())

    model.load_state_dict(best_state)
    gold, pred = gold_and_predicted(model, tokenizer, test)
    strict = {"mode": "strict", "scheme": IOB2}
    precision = precision_score(gold, pred, **strict)
    recall = recall_score(gold, pred, **strict)
    f1 = f1_score(gold, pred, **strict)
    per_label = classification_report(gold, pred, output_dict=True, zero_division=0, **strict)
    print(f"\nTEST entity precision {precision:.3f}  recall {recall:.3f}  F1 {f1:.3f} (best epoch {best_epoch})")

    model.save(args.output, tokenizer)
    stamp = write_report(config.REPORTS_DIR / "ner_metrics.json", {
        "model": "BERT+CRF entity recogniser",
        "base_model": args.base_model,
        "hyperparameters": {"epochs": args.epochs, "encoder_lr": args.lr, "head_lr": args.head_lr,
                            "batch_size": args.batch_size, "seed": args.seed,
                            "max_length": config.MAX_LENGTH, "warmup_ratio": 0.1, "weight_decay": 0.01,
                            "tagging": "word-level BIO (first subword), CRF with BIO-constrained transitions",
                            "selection": "best validation entity-level F1 (seqeval strict IOB2)"},
        "data": manifest,
        "training_seconds": timer.seconds,
        "best_epoch": best_epoch,
        "history": history,
        "test": {
            "entity_extraction_precision": round(precision, 4),
            "entity_recall": round(recall, 4),
            "entity_f1": round(f1, 4),
            "per_label": {k: {m: round(float(v), 4) for m, v in per_label[k].items()}
                          for k in ENTITY_LABELS if k in per_label},
        },
        "caveat": "Test utterances are synthetic and drawn from the same distribution as training data; "
                  "this overstates real-world performance. Evaluate on annotated pilot messages for the study.",
        "environment": environment(),
    })
    write_version(args.output, {"model": "ner", "base_model": args.base_model, "trained_utc": stamp,
                                "best_epoch": best_epoch, "test_entity_f1": round(f1, 4)})
    print(f"model -> {args.output}\nreport -> {config.REPORTS_DIR / 'ner_metrics.json'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
