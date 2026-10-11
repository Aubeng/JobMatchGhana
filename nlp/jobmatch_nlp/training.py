"""Shared training utilities: seeding, a linear warm-up schedule, and report writing."""
from __future__ import annotations

import json
import os
import platform
import random
import time
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import torch
import transformers


def seed_everything(seed: int) -> None:
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)


def configure_threads() -> int:
    """Use the physical cores for training unless JOBMATCH_NLP_THREADS says otherwise."""
    threads = int(os.environ.get("JOBMATCH_NLP_THREADS", max(1, (os.cpu_count() or 2) // 2 + 4)))
    torch.set_num_threads(threads)
    return threads


def linear_schedule(optimizer, total_steps: int, warmup_ratio: float = 0.1):
    warmup = max(1, int(total_steps * warmup_ratio))

    def factor(step: int) -> float:
        if step < warmup:
            return (step + 1) / warmup
        return max(0.0, (total_steps - step) / max(1, total_steps - warmup))

    return torch.optim.lr_scheduler.LambdaLR(optimizer, factor)


def batches(items: list, size: int, shuffle: bool, rng: random.Random):
    order = list(range(len(items)))
    if shuffle:
        rng.shuffle(order)
    for i in range(0, len(order), size):
        yield [items[j] for j in order[i:i + size]]


def environment() -> dict:
    return {
        "python": platform.python_version(),
        "torch": torch.__version__,
        "transformers": transformers.__version__,
        "device": "cpu",
        "processor": platform.processor(),
        "threads": torch.get_num_threads(),
    }


def write_report(path: Path, report: dict) -> str:
    """Write a metrics report; returns its UTC timestamp (also used as the model version stamp)."""
    path.parent.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(timezone.utc).isoformat(timespec="seconds")
    path.write_text(json.dumps({"generated_utc": stamp, **report}, indent=2), encoding="utf-8")
    return stamp


def write_version(model_dir: Path, info: dict) -> None:
    """models/<name>/version.json: what the NLU service reports as its model version."""
    (Path(model_dir) / "version.json").write_text(json.dumps(info, indent=2), encoding="utf-8")


class Timer:
    def __enter__(self):
        self.start = time.perf_counter()
        return self

    def __exit__(self, *exc):
        self.seconds = round(time.perf_counter() - self.start, 1)
