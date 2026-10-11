"""The NLU pipeline: intent classification + entity recognition + entity resolution.

``NluPipeline.analyze`` runs both models; ``assemble`` turns their raw outputs into the API
result. ``assemble`` is pure (no models), so the post-processing rules are unit-tested directly.
"""
from __future__ import annotations

import json
import os
import re
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path

import torch
from transformers import AutoModelForSequenceClassification, AutoTokenizer

from . import config
from .annotation import Span, spans_from_bio, words
from .duration import to_years
from .ner_model import BertCrfTagger, encode_words
from .resolve import Gazetteer, Taxonomy

LEXICON_BACKOFF = os.environ.get("JOBMATCH_NLP_LEXICON_BACKOFF", "1") != "0"
_LEXICON_CONFIDENCE = 0.6   # a skill phrase found verbatim but missed by the entity model
_MAX_NGRAM = 5
_SKILL_INTENTS = {"SKILL_DECLARE", "TRADE_IDENTIFY", "CORRECTION"}


@dataclass
class Entity:
    label: str
    text: str
    start: int
    end: int
    confidence: float
    source: str = "model"          # "model" (BERT+CRF) or "lexicon" (exact taxonomy phrase backoff)
    resolved: dict | None = None


@dataclass
class NluAnalysis:
    intent: str
    confidence: float
    below_threshold: bool
    intent_ranking: list[dict]
    entities: list[Entity]
    skills: list[dict]
    trade_category: dict | None
    location: dict | None
    experience: dict | None
    model_version: str
    latency_ms: float = 0.0
    taxonomy_version: str = ""
    extra: dict = field(default_factory=dict)

    def to_dict(self) -> dict:
        d = asdict(self)
        d.pop("extra")
        return d


def assemble(text: str, intent_probs: dict[str, float], word_tags: list[str], word_confs: list[float],
             taxonomy: Taxonomy, gazetteer: Gazetteer, model_version: str,
             threshold: float = config.INTENT_CONFIDENCE_THRESHOLD,
             lexicon_backoff: bool = LEXICON_BACKOFF) -> NluAnalysis:
    ranking = sorted(intent_probs.items(), key=lambda kv: kv[1], reverse=True)
    intent, confidence = ranking[0]

    toks = words(text)
    entities: list[Entity] = []
    for span in spans_from_bio(text, word_tags):
        idx = [i for i, (_, ws, we) in enumerate(toks) if ws >= span.start and we <= span.end]
        conf = sum(word_confs[i] for i in idx) / len(idx) if idx else 0.0
        entities.append(Entity(span.label, span.text, span.start, span.end, round(conf, 4)))

    # Only where a skill is expected: elsewhere, words like "market" in "near Kaneshie market"
    # would be misread as skills.
    if (lexicon_backoff and intent in _SKILL_INTENTS
            and not any(e.label == "SKILL_ITEM" for e in entities)):
        entities += _lexicon_skills(text, taxonomy, entities)

    skills: dict[str, dict] = {}
    categories: list[dict] = []
    location = experience = None
    for e in entities:
        if e.label == "SKILL_ITEM":
            m = taxonomy.resolve_skill(e.text)
            if m:
                e.resolved = {"type": "skill", "id": m.skill_id, "name": m.name, "match_score": m.score}
                score = round(e.confidence * m.score, 4)
                if m.skill_id not in skills or skills[m.skill_id]["confidence"] < score:
                    skills[m.skill_id] = {
                        "canonical_name": m.name, "skill_id": m.skill_id, "category_id": m.category_id,
                        "category": m.category_name, "isco08": m.isco08, "confidence": score,
                        "source_text": e.text, "source": e.source}
                categories.append({"category_id": m.category_id, "name": m.category_name,
                                   "confidence": score, "via": "skill"})
                continue
            # A trade word tagged as a skill ("masonry") still tells us the category.
            c = taxonomy.resolve_category(e.text)
            if c:
                e.resolved = {"type": "category", "id": c.category_id, "name": c.name, "match_score": c.score}
                categories.append({"category_id": c.category_id, "name": c.name,
                                   "confidence": round(e.confidence * c.score, 4), "via": "trade"})
        elif e.label == "TRADE_CATEGORY":
            c = taxonomy.resolve_category(e.text) or _category_via_skill(taxonomy, e.text)
            if c:
                e.resolved = {"type": "category", "id": c.category_id, "name": c.name, "match_score": c.score}
                categories.append({"category_id": c.category_id, "name": c.name,
                                   "confidence": round(e.confidence * c.score, 4), "via": "trade"})
        elif e.label == "LOCATION":
            loc = gazetteer.resolve(e.text)
            if loc:
                e.resolved = {"type": "location", "district": loc.district, "region": loc.region,
                              "locality": loc.locality, "match_score": loc.score}
                score = round(e.confidence * loc.score, 4)
                if location is None or location["confidence"] < score:
                    location = {"district": loc.district, "region": loc.region, "locality": loc.locality,
                                "confidence": score, "source_text": e.text}
        elif e.label == "EXPERIENCE_DURATION" and experience is None:
            years = to_years(e.text)
            experience = {"text": e.text, "years": years, "confidence": e.confidence}
            if years is not None:
                e.resolved = {"type": "duration", "years": years}

    # Prefer an explicitly named trade over one inferred from a skill.
    trade = None
    for via in ("trade", "skill"):
        cands = [c for c in categories if c["via"] == via]
        if cands:
            trade = max(cands, key=lambda c: c["confidence"])
            # Lets the dialogue probe skills within the trade (S&D S2: ELICIT_SKILLS).
            trade = {**trade, "example_skills": taxonomy.skill_names(trade["category_id"])[:3]}
            break

    return NluAnalysis(
        intent=intent,
        confidence=round(confidence, 4),
        below_threshold=confidence < threshold,
        intent_ranking=[{"intent": k, "confidence": round(v, 4)} for k, v in ranking[:3]],
        entities=entities,
        skills=sorted(skills.values(), key=lambda s: s["confidence"], reverse=True),
        trade_category=trade,
        location=location,
        experience=experience,
        model_version=model_version,
        taxonomy_version=taxonomy.version,
    )


def _category_via_skill(taxonomy: Taxonomy, text: str):
    m = taxonomy.resolve_skill(text)
    if not m:
        return None
    from .resolve import CategoryMatch
    return CategoryMatch(m.category_id, m.category_name, m.score)


def _lexicon_skills(text: str, taxonomy: Taxonomy, existing: list[Entity]) -> list[Entity]:
    """Longest-first exact matches of taxonomy skill phrases the entity model did not tag."""
    toks = words(text)
    taken = [(e.start, e.end) for e in existing]
    found: list[Entity] = []
    for n in range(min(_MAX_NGRAM, len(toks)), 0, -1):
        for i in range(len(toks) - n + 1):
            start, end = toks[i][1], toks[i + n - 1][2]
            if any(start < te and end > ts for ts, te in taken):
                continue
            phrase = text[start:end]
            if len(phrase) < 4 or not re.search(r"\w", phrase):
                continue
            m = taxonomy.resolve_skill(phrase)
            if m and m.score == 1.0:
                found.append(Entity("SKILL_ITEM", phrase, start, end, _LEXICON_CONFIDENCE, "lexicon"))
                taken.append((start, end))
    return found


class NluPipeline:
    def __init__(self, intent_dir: Path = config.INTENT_MODEL_DIR, ner_dir: Path = config.NER_MODEL_DIR,
                 taxonomy: Taxonomy | None = None, gazetteer: Gazetteer | None = None):
        self.taxonomy = taxonomy or Taxonomy.load(config.TAXONOMY_PATH)
        self.gazetteer = gazetteer or Gazetteer.load(config.DISTRICTS_PATH, config.LOCALITIES_PATH)
        self.intent_tokenizer = AutoTokenizer.from_pretrained(intent_dir)
        self.intent_model = AutoModelForSequenceClassification.from_pretrained(intent_dir).eval()
        self.labels = [self.intent_model.config.id2label[i] for i in range(self.intent_model.config.num_labels)]
        self.ner_model, self.ner_tokenizer = BertCrfTagger.load(ner_dir)
        self.model_version = _version(intent_dir, ner_dir)

    @torch.no_grad()
    def analyze(self, text: str) -> NluAnalysis:
        t0 = time.perf_counter()
        enc = self.intent_tokenizer([text], truncation=True, max_length=config.MAX_LENGTH, return_tensors="pt")
        probs = self.intent_model(**enc).logits.softmax(-1)[0].tolist()
        batch = encode_words(self.ner_tokenizer, [text], config.MAX_LENGTH)
        tags, confs = self.ner_model.predict(batch)
        n_all = len(words(text))
        word_tags = tags[0] + ["O"] * (n_all - len(tags[0]))
        word_confs = confs[0] + [1.0] * (n_all - len(confs[0]))
        result = assemble(text, dict(zip(self.labels, probs)), word_tags, word_confs,
                          self.taxonomy, self.gazetteer, self.model_version)
        result.latency_ms = round((time.perf_counter() - t0) * 1000, 1)
        return result


def _version(intent_dir: Path, ner_dir: Path) -> str:
    def stamp(d: Path) -> str:
        try:
            return json.loads((Path(d) / "version.json").read_text(encoding="utf-8"))["trained_utc"]
        except (OSError, KeyError, ValueError):
            return "unknown"
    return f"intent@{stamp(intent_dir)};ner@{stamp(ner_dir)}"
