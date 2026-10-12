"""Matching engine, stage 2: semantic ranking (S&D L5; Design §6.5).

The gateway runs stage 1 (the geographic filter: the worker's district plus adjacent districts) in
SQL and sends the surviving jobs here. Each job is scored by the cosine similarity between the
embedding of the worker's profile text and the embedding of the job text, using
paraphrase-MiniLM-L6-v2 (Reimers and Gurevych, 2019). The top-k jobs are returned.

Fairness (NFR-13): the inputs are the trade, skills, experience and availability only. Nothing
else about the worker exists in the request.

Each match also gets an explanation for FR-13: the worker's skill most similar to the job, and any
exact overlap with the job's required skills. The full breakdown is returned so the gateway can
store it in ``Match.ScoreBreakdownJson`` (research data).
"""
from __future__ import annotations

import time
from collections import OrderedDict
from dataclasses import dataclass

import torch
from transformers import AutoModel, AutoTokenizer

EMBEDDING_MODEL = "sentence-transformers/paraphrase-MiniLM-L6-v2"
DEFAULT_MIN_SCORE = 0.55   # brief: Nlu:MatchThreshold; the gateway sends its configured value


def profile_text(trade: str | None, skills: list[str]) -> str:
    """S&D: embed(profile_text). Trade and skills only; experience and availability are
    reported but don't shape the semantic text."""
    head = f"{trade}: " if trade else ""
    return head + ", ".join(skills) + "."


def job_text(title: str, description: str | None, required_skills: list[str]) -> str:
    parts = [title.rstrip(".") + "."]
    if description:
        parts.append(description)
    if required_skills:
        parts.append("Skills: " + ", ".join(required_skills) + ".")
    return " ".join(parts)


class Embedder:
    """Mean-pooled MiniLM sentence embeddings, with a small LRU cache (job texts repeat)."""

    def __init__(self, model_name: str = EMBEDDING_MODEL, cache_size: int = 4096):
        self.model_name = model_name
        self.tokenizer = AutoTokenizer.from_pretrained(model_name)
        self.model = AutoModel.from_pretrained(model_name).eval()
        self._cache: OrderedDict[str, torch.Tensor] = OrderedDict()
        self._cache_size = cache_size

    @torch.no_grad()
    def embed(self, texts: list[str]) -> torch.Tensor:
        missing = [t for t in dict.fromkeys(texts) if t not in self._cache]
        if missing:
            enc = self.tokenizer(missing, padding=True, truncation=True, max_length=128, return_tensors="pt")
            hidden = self.model(**enc).last_hidden_state
            mask = enc["attention_mask"].unsqueeze(-1).float()
            pooled = (hidden * mask).sum(1) / mask.sum(1).clamp(min=1e-9)
            for t, v in zip(missing, torch.nn.functional.normalize(pooled, dim=-1)):
                self._cache[t] = v
                if len(self._cache) > self._cache_size:
                    self._cache.popitem(last=False)
        for t in texts:
            self._cache.move_to_end(t)
        return torch.stack([self._cache[t] for t in texts])


@dataclass
class RankedJob:
    id: int
    rank: int
    score: float                  # cosine(profile, job): the S&D ranking score
    best_skill: str | None        # the worker's skill closest to this job (explanation)
    best_skill_score: float | None
    skill_overlap: list[str]      # worker skills the job explicitly requires
    job_text: str


def rank(embedder: Embedder, trade: str | None, skills: list[str], jobs: list[dict],
         top_k: int = 3, min_score: float = DEFAULT_MIN_SCORE) -> tuple[list[RankedJob], str, float]:
    """Rank already geo-filtered ``jobs`` for a worker. Returns (matches, profile text, ms)."""
    t0 = time.perf_counter()
    ptext = profile_text(trade, skills)
    if not jobs or not skills:
        return [], ptext, 0.0
    jtexts = [job_text(j["title"], j.get("description"), j.get("required_skills", [])) for j in jobs]
    vectors = embedder.embed([ptext, *jtexts, *skills])
    profile_vec, job_vecs = vectors[0], vectors[1:1 + len(jobs)]
    skill_vecs = vectors[1 + len(jobs):]

    scores = (job_vecs @ profile_vec).tolist()
    per_skill = (job_vecs @ skill_vecs.T)                      # (jobs, skills)
    wanted = {s.lower() for s in skills}

    ranked = []
    for i, j in enumerate(jobs):
        best = int(per_skill[i].argmax())
        ranked.append(RankedJob(
            id=j["id"], rank=0, score=round(scores[i], 4),
            best_skill=skills[best], best_skill_score=round(float(per_skill[i, best]), 4),
            skill_overlap=[s for s in j.get("required_skills", []) if s.lower() in wanted],
            job_text=jtexts[i]))
    # Highest similarity first; an explicit skill overlap breaks ties.
    ranked.sort(key=lambda r: (r.score, len(r.skill_overlap)), reverse=True)
    kept = [r for r in ranked if r.score >= min_score][:top_k]
    for n, r in enumerate(kept, start=1):
        r.rank = n
    return kept, ptext, round((time.perf_counter() - t0) * 1000, 1)
