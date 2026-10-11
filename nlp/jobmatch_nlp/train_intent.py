"""Fine-tune XLM-RoBERTa as the 9-way intent classifier (S&D L4; Design §6.3.1).

    python -m jobmatch_nlp.train_intent [--epochs 8] [--lr 3e-5] [--batch-size 16]

Trains on the train split, keeps the epoch with the best validation macro-F1, then evaluates
once on the held-out test split. Writes the model to models/intent/ and the metrics (accuracy,
macro-F1, per-intent precision/recall, confusion matrix) to reports/intent_metrics.json.
"""
from __future__ import annotations

import argparse
import copy
import random

import torch
from sklearn.metrics import accuracy_score, classification_report, confusion_matrix, f1_score
from transformers import AutoModelForSequenceClassification, AutoTokenizer

from . import config
from .annotation import INTENTS
from .data import load_dataset, write_split_manifest
from .training import (Timer, batches, configure_threads, environment, linear_schedule,
                       seed_everything, write_report, write_version)


def evaluate(model, tokenizer, examples, batch_size: int = 64) -> tuple[list[int], list[int], list[float]]:
    model.eval()
    gold, pred, conf = [], [], []
    with torch.no_grad():
        for batch in batches(examples, batch_size, False, random.Random(0)):
            enc = tokenizer([e.text for e in batch], truncation=True, max_length=config.MAX_LENGTH,
                            padding=True, return_tensors="pt")
            probs = model(**enc).logits.softmax(-1)
            p, idx = probs.max(-1)
            pred += idx.tolist()
            conf += p.tolist()
            gold += [INTENTS.index(e.intent) for e in batch]
    return gold, pred, conf


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base-model", default=config.INTENT_BASE_MODEL)
    ap.add_argument("--epochs", type=int, default=8)
    ap.add_argument("--lr", type=float, default=3e-5)
    ap.add_argument("--batch-size", type=int, default=16)
    ap.add_argument("--seed", type=int, default=config.SEED)
    ap.add_argument("--output", default=str(config.INTENT_MODEL_DIR))
    args = ap.parse_args(argv)

    seed_everything(args.seed)
    threads = configure_threads()
    splits = load_dataset()
    manifest = write_split_manifest(splits)
    train, val, test = splits["train"], splits["val"], splits["test"]
    print(f"train {len(train)} / val {len(val)} / test {len(test)} utterances; {threads} CPU threads")

    tokenizer = AutoTokenizer.from_pretrained(args.base_model)
    model = AutoModelForSequenceClassification.from_pretrained(
        args.base_model, num_labels=len(INTENTS),
        id2label=dict(enumerate(INTENTS)), label2id={l: i for i, l in enumerate(INTENTS)})

    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=0.01)
    steps_per_epoch = -(-len(train) // args.batch_size)
    scheduler = linear_schedule(optimizer, steps_per_epoch * args.epochs)
    rng = random.Random(args.seed)

    history, best_f1, best_state, best_epoch = [], -1.0, None, 0
    with Timer() as timer:
        for epoch in range(1, args.epochs + 1):
            model.train()
            total = 0.0
            for batch in batches(train, args.batch_size, True, rng):
                enc = tokenizer([e.text for e in batch], truncation=True, max_length=config.MAX_LENGTH,
                                padding=True, return_tensors="pt")
                labels = torch.tensor([INTENTS.index(e.intent) for e in batch])
                loss = model(**enc, labels=labels).loss
                loss.backward()
                torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
                optimizer.step()
                scheduler.step()
                optimizer.zero_grad()
                total += loss.item() * len(batch)
            gold, pred, _ = evaluate(model, tokenizer, val)
            f1 = f1_score(gold, pred, average="macro")
            acc = accuracy_score(gold, pred)
            history.append({"epoch": epoch, "train_loss": round(total / len(train), 4),
                            "val_accuracy": round(acc, 4), "val_macro_f1": round(f1, 4)})
            print(f"epoch {epoch}: loss {total / len(train):.4f}  val acc {acc:.3f}  macro-F1 {f1:.3f}", flush=True)
            if f1 > best_f1:
                best_f1, best_epoch = f1, epoch
                best_state = copy.deepcopy(model.state_dict())

    model.load_state_dict(best_state)
    gold, pred, conf = evaluate(model, tokenizer, test)
    below = sum(c < config.INTENT_CONFIDENCE_THRESHOLD for c in conf)
    report = classification_report(gold, pred, labels=list(range(len(INTENTS))), target_names=list(INTENTS),
                                   output_dict=True, zero_division=0)
    test_acc = accuracy_score(gold, pred)
    print(f"\nTEST accuracy {test_acc:.3f}  macro-F1 {f1_score(gold, pred, average='macro'):.3f} "
          f"(best epoch {best_epoch}; S&D target >= 0.85)")

    model.save_pretrained(args.output)
    tokenizer.save_pretrained(args.output)
    stamp = write_report(config.REPORTS_DIR / "intent_metrics.json", {
        "model": "XLM-RoBERTa intent classifier",
        "base_model": args.base_model,
        "hyperparameters": {"epochs": args.epochs, "lr": args.lr, "batch_size": args.batch_size,
                            "seed": args.seed, "max_length": config.MAX_LENGTH, "warmup_ratio": 0.1,
                            "weight_decay": 0.01, "selection": "best validation macro-F1"},
        "data": manifest,
        "training_seconds": timer.seconds,
        "best_epoch": best_epoch,
        "history": history,
        "test": {
            "accuracy": round(test_acc, 4),
            "macro_f1": round(f1_score(gold, pred, average="macro"), 4),
            "meets_sd_target_0_85": test_acc >= 0.85,
            "below_confidence_threshold": {"threshold": config.INTENT_CONFIDENCE_THRESHOLD,
                                           "count": below, "of": len(conf)},
            "per_intent": {k: {m: round(v, 4) for m, v in report[k].items()} for k in INTENTS},
            "confusion_matrix": {"labels": list(INTENTS),
                                 "rows_gold_cols_pred": confusion_matrix(
                                     gold, pred, labels=list(range(len(INTENTS)))).tolist()},
        },
        "caveat": "Test utterances are synthetic and drawn from the same distribution as training data; "
                  "this overstates real-world accuracy. Evaluate on annotated pilot messages for the study.",
        "environment": environment(),
    })
    write_version(args.output, {"model": "intent", "base_model": args.base_model, "trained_utc": stamp,
                                "best_epoch": best_epoch, "test_accuracy": round(test_acc, 4)})
    print(f"model -> {args.output}\nreport -> {config.REPORTS_DIR / 'intent_metrics.json'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
