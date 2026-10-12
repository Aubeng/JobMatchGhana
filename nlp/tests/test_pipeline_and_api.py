"""Post-processing (assemble) and the HTTP contract, without loading any models.

tests/fixtures/nlu_response.json is the shared contract: the .NET gateway's HttpNluClient tests
parse the same file, so a breaking change on either side fails a test.
"""
import json
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from jobmatch_nlp import config
from jobmatch_nlp.annotation import INTENTS, bio_tags, parse_line
from jobmatch_nlp.app import NluResponse, app
from jobmatch_nlp.pipeline import assemble
from jobmatch_nlp.resolve import Gazetteer, Taxonomy

FIXTURE = Path(__file__).parent / "fixtures" / "nlu_response.json"


@pytest.fixture(scope="module")
def resources():
    return Taxonomy.load(config.TAXONOMY_PATH), Gazetteer.load(config.DISTRICTS_PATH, config.LOCALITIES_PATH)


def probs(top: str, p: float) -> dict[str, float]:
    rest = (1 - p) / (len(INTENTS) - 1)
    return {i: (p if i == top else rest) for i in INTENTS}


def run(line: str, resources, intent="SKILL_DECLARE", p=0.93, **kw):
    text, spans = parse_line(line)
    tags = bio_tags(text, spans)          # pretend the entity model was perfect
    return assemble(text, probs(intent, p), tags, [0.9] * len(tags), *resources, "test", **kw)


def test_skills_location_trade_and_experience_are_resolved(resources):
    r = run("am a [mason](TRADE_CATEGORY), i [lay blocks](SKILL_ITEM) and [plastring](SKILL_ITEM) "
            "for [5 yrs](EXPERIENCE_DURATION) at [kaneshie](LOCATION)", resources)
    assert r.intent == "SKILL_DECLARE" and not r.below_threshold
    assert [s["canonical_name"] for s in r.skills] == ["Block laying", "Plastering"]  # exact beats fuzzy
    assert r.trade_category["category_id"] == "construction" and r.trade_category["via"] == "trade"
    assert r.location["district"] == "Ablekuma Central"
    assert r.experience == {"text": "5 yrs", "years": 5.0, "confidence": 0.9}


def test_low_intent_confidence_is_flagged(resources):
    assert run("hmm", resources, intent="HELP_REQUEST", p=0.5).below_threshold


def test_trade_inferred_from_skill_when_no_trade_named(resources):
    r = run("i do [braiding](SKILL_ITEM)", resources)
    assert r.trade_category["category_id"] == "hair_beauty" and r.trade_category["via"] == "skill"


def test_lexicon_backoff_finds_skills_the_model_missed(resources):
    r = run("i can do tiling and welding", resources)
    assert {s["canonical_name"] for s in r.skills} == {"Tiling", "Arc welding"}
    assert all(s["source"] == "lexicon" and s["confidence"] == 0.6 for s in r.skills)
    assert run("i can do tiling", resources, lexicon_backoff=False).skills == []


def test_lexicon_backoff_only_runs_for_skill_bearing_intents(resources):
    assert run("i live behind kaneshie market", resources, intent="LOCATION_SHARE").skills == []


def test_unresolvable_skill_is_kept_as_entity_but_not_a_skill(resources):
    r = run("i do [rocket science](SKILL_ITEM)", resources)
    assert r.skills == [] and r.entities[0].resolved is None


def test_fixture_matches_the_response_schema_and_assemble_output(resources):
    fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
    NluResponse.model_validate(fixture)
    live = run("i can [lay blocks](SKILL_ITEM) at [madina](LOCATION)", resources).to_dict()
    assert set(live) == set(fixture)
    assert set(live["skills"][0]) == set(fixture["skills"][0])


class FakePipeline:
    model_version = "fake"

    def __init__(self, resources):
        self.taxonomy, self.gazetteer = resources
        self.calls = []

    def analyze(self, text):
        self.calls.append(text)
        return run("i can [lay blocks](SKILL_ITEM) at [madina](LOCATION)", (self.taxonomy, self.gazetteer))


def test_api_contract(resources):
    fake = FakePipeline(resources)
    app.state.pipeline_factory = lambda: fake
    app.state.embedder_factory = lambda: None   # matching isn't under test here
    try:
        with TestClient(app) as client:
            assert client.get("/health").json()["status"] == "ok"
            r = client.post("/nlu", json={"utterance": "  i can lay blocks at madina ", "state": "SkillsElicitation"})
            assert r.status_code == 200
            body = r.json()
            assert body["intent"] == "SKILL_DECLARE"
            assert body["skills"][0]["canonical_name"] == "Block laying"
            assert fake.calls == ["i can lay blocks at madina"]
            assert client.post("/nlu", json={"utterance": ""}).status_code == 422
    finally:
        app.state.pipeline_factory = None
        app.state.embedder_factory = None


def test_api_reports_unhealthy_when_models_are_missing():
    def broken():
        raise FileNotFoundError("models/intent")
    app.state.pipeline_factory = broken
    app.state.embedder_factory = lambda: None
    try:
        with TestClient(app) as client:
            assert client.get("/health").status_code == 503
            assert client.post("/nlu", json={"utterance": "hi"}).status_code == 503
    finally:
        app.state.pipeline_factory = None
        app.state.embedder_factory = None
