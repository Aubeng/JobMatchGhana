"""Matching (S&D L5 stage 2) with a deterministic bag-of-words embedder, so no model download.

tests/fixtures/match_response.json is the shared HTTP contract for /match, also read by the .NET
gateway's tests.
"""
import json
import re
from pathlib import Path

import pytest
import torch
from fastapi.testclient import TestClient

from jobmatch_nlp.app import MatchResponse, app
from jobmatch_nlp.matching import job_text, profile_text, rank

FIXTURE = Path(__file__).parent / "fixtures" / "match_response.json"


class BagOfWordsEmbedder:
    """Cosine over word counts: enough to test ranking logic without MiniLM."""
    model_name = "bag-of-words-test"

    def __init__(self):
        self.vocab: dict[str, int] = {}

    def embed(self, texts):
        rows = []
        for t in texts:
            v = torch.zeros(512)
            for w in re.findall(r"[a-z]+", t.lower()):
                v[self.vocab.setdefault(w, len(self.vocab) % 512)] += 1
            rows.append(torch.nn.functional.normalize(v, dim=0))
        return torch.stack(rows)


JOBS = [
    {"id": 1, "title": "Tiler needed", "description": "Lay tiles in bathrooms", "required_skills": ["Tiling"]},
    {"id": 2, "title": "Block layer", "description": "Lay blocks for a house", "required_skills": ["Block laying"]},
    {"id": 3, "title": "Baker", "description": "Bake bread early mornings", "required_skills": ["Baking (bread)"]},
]


def test_texts_follow_the_sd_definition():
    assert profile_text("Construction & Building", ["Tiling", "Block laying"]) == "Construction & Building: Tiling, Block laying."
    assert job_text("Tiler", "Lay tiles.", ["Tiling"]) == "Tiler. Lay tiles. Skills: Tiling."


def test_rank_orders_by_similarity_applies_threshold_and_explains():
    matches, ptext, _ = rank(BagOfWordsEmbedder(), "Construction", ["Tiling"], JOBS, top_k=3, min_score=0.2)
    assert [m.id for m in matches] == [1]                       # baker and block layer fall below 0.2
    assert matches[0].rank == 1
    assert matches[0].best_skill == "Tiling" and matches[0].skill_overlap == ["Tiling"]
    assert ptext == "Construction: Tiling."


def test_top_k_and_ties_broken_by_explicit_skill_overlap():
    jobs = [{"id": 10, "title": "Work", "description": "", "required_skills": []},
            {"id": 11, "title": "Work", "description": "", "required_skills": ["Tiling"]}]
    matches, _, _ = rank(BagOfWordsEmbedder(), None, ["Tiling"], jobs, top_k=1, min_score=-1)
    assert [m.id for m in matches] == [11]


def test_no_jobs_or_no_skills_returns_nothing():
    assert rank(BagOfWordsEmbedder(), "x", ["Tiling"], [], 3, 0)[0] == []
    assert rank(BagOfWordsEmbedder(), "x", [], JOBS, 3, 0)[0] == []


@pytest.fixture
def client():
    app.state.pipeline_factory = lambda: (_ for _ in ()).throw(FileNotFoundError("no NLU models in this test"))
    app.state.embedder_factory = BagOfWordsEmbedder
    try:
        with TestClient(app) as c:
            yield c
    finally:
        app.state.pipeline_factory = None
        app.state.embedder_factory = None


def test_match_endpoint_works_even_when_nlu_models_are_missing(client):
    r = client.post("/match", json={"trade": "Construction", "skills": ["Tiling"], "jobs": JOBS, "min_score": 0.2})
    assert r.status_code == 200
    body = r.json()
    assert body["candidates"] == 3 and [m["id"] for m in body["matches"]] == [1]
    assert client.post("/nlu", json={"utterance": "hi"}).status_code == 503


def test_match_request_validation(client):
    assert client.post("/match", json={"skills": [], "jobs": JOBS}).status_code == 422
    assert client.post("/match", json={"skills": ["Tiling"], "jobs": JOBS, "top_k": 50}).status_code == 422


def test_fixture_matches_the_response_schema(client):
    fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
    MatchResponse.model_validate(fixture)
    live = client.post("/match", json={"trade": "Construction", "skills": ["Tiling"], "jobs": JOBS, "min_score": 0.2}).json()
    assert set(live) == set(fixture) and set(live["matches"][0]) == set(fixture["matches"][0])
