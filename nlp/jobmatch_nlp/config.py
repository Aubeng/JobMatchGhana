"""Paths, model choices and thresholds. Override paths with environment variables on a server."""
from __future__ import annotations

import os
from pathlib import Path

NLP_ROOT = Path(__file__).resolve().parent.parent
DATA_DIR = Path(os.environ.get("JOBMATCH_NLP_DATA", NLP_ROOT / "data"))
MODELS_DIR = Path(os.environ.get("JOBMATCH_NLP_MODELS", NLP_ROOT / "models"))
REPORTS_DIR = NLP_ROOT / "reports"

UTTERANCES_DIR = DATA_DIR / "utterances"
SPLITS_DIR = DATA_DIR / "splits"
TAXONOMY_PATH = DATA_DIR / "taxonomy_v1.json"
DISTRICTS_PATH = DATA_DIR / "ghana_districts.json"
LOCALITIES_PATH = DATA_DIR / "localities_v1.json"

INTENT_MODEL_DIR = MODELS_DIR / "intent"
NER_MODEL_DIR = MODELS_DIR / "ner"

# Base models fixed by the S&D report (L4): XLM-RoBERTa for intents, BERT+CRF for entities.
# mBERT keeps the entity model multilingual like the intent model, for future Twi/Ga work.
INTENT_BASE_MODEL = "FacebookAI/xlm-roberta-base"
NER_BASE_MODEL = "google-bert/bert-base-multilingual-cased"

# S&D L4: below this intent confidence the dialogue engine should clarify, not act.
INTENT_CONFIDENCE_THRESHOLD = 0.75

SEED = 13
MAX_LENGTH = 64  # WhatsApp utterances are short; 64 subword tokens covers ~45 words
