from datetime import date

import pytest

from jobmatch_nlp import config
from jobmatch_nlp.duration import to_years
from jobmatch_nlp.resolve import Gazetteer, Taxonomy


@pytest.mark.parametrize("text, years", [
    ("5 years", 5.0), ("5yrs", 5.0), ("two yrs", 2.0), ("a year", 1.0), ("6 months", 0.5),
    ("1 and half years", 1.5), ("10+ years", 10.0), ("since 2018", 8.0), ("from 2020", 6.0),
    ("since i left school", None), ("long time", None),
])
def test_experience_to_years(text, years):
    assert to_years(text, today=date(2026, 10, 11)) == years


@pytest.fixture(scope="module")
def taxonomy():
    return Taxonomy.load(config.TAXONOMY_PATH)


@pytest.fixture(scope="module")
def gazetteer():
    return Gazetteer.load(config.DISTRICTS_PATH, config.LOCALITIES_PATH)


def test_taxonomy_shape(taxonomy):
    assert len(taxonomy.categories) == 12
    assert sum(len(c["skills"]) for c in taxonomy.categories.values()) == 120


@pytest.mark.parametrize("text, skill_id, exact", [
    ("Block laying", "construction.block_laying", True),     # canonical name
    ("lay blocks", "construction.block_laying", True),       # synonym
    ("the block work", "construction.block_laying", False),  # filler words stripped
    ("plastring", "construction.plastering", False),         # misspelling -> fuzzy
    ("vulcanising", "vehicle_repair.vulcanizing", True),
    ("POP", "construction.pop_ceiling", True),               # short: exact only
])
def test_resolve_skill(taxonomy, text, skill_id, exact):
    m = taxonomy.resolve_skill(text)
    assert m is not None and m.skill_id == skill_id
    assert (m.score == 1.0) == exact


def test_short_phrases_never_fuzzy_match(taxonomy, gazetteer):
    assert taxonomy.resolve_skill("pip") is None
    assert gazetteer.resolve("lo") is None


def test_resolve_category(taxonomy):
    assert taxonomy.resolve_category("carpenter").category_id == "woodwork"
    assert taxonomy.resolve_category("fitter").category_id == "vehicle_repair"
    assert taxonomy.resolve_category("astronaut") is None


@pytest.mark.parametrize("text, district, region", [
    ("Madina", "La Nkwantanang Madina", "Greater Accra"),
    ("ashaiman", "Ashaiman", "Greater Accra"),               # district name directly
    ("tema community 4", "Tema", "Greater Accra"),           # trailing words trimmed
    ("kaneshie market", "Ablekuma Central", "Greater Accra"),
    ("teshi", "Ledzokuku", "Greater Accra"),                 # fuzzy
    ("kasoa", "Awutu Senya East", "Central"),
])
def test_resolve_location(gazetteer, text, district, region):
    m = gazetteer.resolve(text)
    assert m is not None and (m.district, m.region) == (district, region)


def test_every_locality_maps_to_an_official_district():
    import json
    districts = json.loads(config.DISTRICTS_PATH.read_text(encoding="utf-8"))
    official = {d: r for r, ds in districts.items() for d in ds}
    localities = json.loads(config.LOCALITIES_PATH.read_text(encoding="utf-8"))["localities"]
    for loc in localities:
        assert official.get(loc["district"]) == loc["region"], loc["name"]
