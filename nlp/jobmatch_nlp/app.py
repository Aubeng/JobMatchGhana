"""FastAPI service: POST /nlu, POST /match (both stateless) and GET /health.

    uvicorn jobmatch_nlp.app:app --host 127.0.0.1 --port 8000

The gateway owns all conversation state (DL-008); this service only analyses one utterance at a
time. Bind it to localhost: it has no authentication and is meant to be reached only by the
gateway on the same machine or private network.
"""
from __future__ import annotations

import logging
import os
from contextlib import asynccontextmanager

import torch
from fastapi import FastAPI, HTTPException, Request
from pydantic import BaseModel, Field

from .annotation import INTENTS

log = logging.getLogger("jobmatch_nlp")


class NluRequest(BaseModel):
    utterance: str = Field(..., min_length=1, max_length=2000)
    # The gateway's FSM state, logged for analysis; the models do not use it (yet).
    state: str | None = Field(default=None, max_length=64)


class SkillOut(BaseModel):
    canonical_name: str
    skill_id: str
    category_id: str
    category: str
    isco08: str
    confidence: float
    source_text: str
    source: str


class EntityOut(BaseModel):
    label: str
    text: str
    start: int
    end: int
    confidence: float
    source: str
    resolved: dict | None = None


class NluResponse(BaseModel):
    intent: str
    confidence: float
    below_threshold: bool
    intent_ranking: list[dict]
    entities: list[EntityOut]
    skills: list[SkillOut]
    trade_category: dict | None
    location: dict | None
    experience: dict | None
    model_version: str
    taxonomy_version: str
    latency_ms: float


class MatchJob(BaseModel):
    id: int
    title: str = Field(..., max_length=200)
    description: str | None = Field(default=None, max_length=2000)
    required_skills: list[str] = Field(default_factory=list, max_length=30)


class MatchRequest(BaseModel):
    """One worker's profile + the jobs that passed the gateway's geographic filter (stage 1)."""
    trade: str | None = Field(default=None, max_length=128)
    skills: list[str] = Field(..., min_length=1, max_length=30)
    jobs: list[MatchJob] = Field(default_factory=list, max_length=500)
    top_k: int = Field(default=3, ge=1, le=10)
    min_score: float = Field(default=0.55, ge=-1, le=1)


class MatchOut(BaseModel):
    id: int
    rank: int
    score: float
    best_skill: str | None
    best_skill_score: float | None
    skill_overlap: list[str]


class MatchResponse(BaseModel):
    model: str
    profile_text: str
    candidates: int
    matches: list[MatchOut]
    latency_ms: float


def _load_embedder():
    from .matching import Embedder
    return Embedder()


def _load_pipeline():
    from .pipeline import NluPipeline
    return NluPipeline()


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Keep inference from monopolising a shared server's cores.
    torch.set_num_threads(int(os.environ.get("JOBMATCH_NLP_THREADS", "4")))
    app.state.pipeline = None
    app.state.load_error = None
    if getattr(app.state, "pipeline_factory", None) is None:
        app.state.pipeline_factory = _load_pipeline
    if getattr(app.state, "embedder_factory", None) is None:
        app.state.embedder_factory = _load_embedder
    try:
        app.state.pipeline = app.state.pipeline_factory()
        log.info("NLU models loaded: %s", app.state.pipeline.model_version)
    except Exception as exc:  # missing/corrupt models: stay up, report unhealthy
        app.state.load_error = f"{type(exc).__name__}: {exc}"
        log.error("NLU models failed to load: %s", app.state.load_error)
    # The matcher is independent of the NLU models: one failing must not take down the other.
    app.state.embedder = None
    try:
        app.state.embedder = app.state.embedder_factory()
    except Exception as exc:
        app.state.match_error = f"{type(exc).__name__}: {exc}"
        log.error("Matching model failed to load: %s", app.state.match_error)
    yield


app = FastAPI(title="JobMatch Ghana NLU", version="2.0.0", lifespan=lifespan)


@app.get("/health")
def health(request: Request):
    p = request.app.state.pipeline
    e = request.app.state.embedder
    if p is None:
        raise HTTPException(503, detail={"status": "models not loaded", "error": request.app.state.load_error,
                                         "matching": e is not None})
    return {"status": "ok", "model_version": p.model_version, "taxonomy_version": p.taxonomy.version,
            "intents": list(INTENTS), "matching_model": e.model_name if e else None}


@app.post("/match", response_model=MatchResponse)
def match(body: MatchRequest, request: Request):
    """Stage 2 of S&D L5: rank geo-filtered jobs by MiniLM cosine similarity; top-k."""
    from .matching import rank
    e = request.app.state.embedder
    if e is None:
        raise HTTPException(503, detail="matching model not loaded")
    jobs = [j.model_dump() for j in body.jobs]
    matches, ptext, ms = rank(e, body.trade, body.skills, jobs, body.top_k, body.min_score)
    return {"model": e.model_name, "profile_text": ptext, "candidates": len(jobs),
            "matches": [{k: getattr(m, k) for k in ("id", "rank", "score", "best_skill", "best_skill_score",
                                                      "skill_overlap")} for m in matches],
            "latency_ms": ms}


@app.post("/nlu", response_model=NluResponse)
def nlu(body: NluRequest, request: Request):
    # Sync handler: FastAPI runs it in a worker thread, so CPU inference doesn't block the loop.
    p = request.app.state.pipeline
    if p is None:
        raise HTTPException(503, detail="models not loaded")
    return p.analyze(body.utterance.strip()).to_dict()
