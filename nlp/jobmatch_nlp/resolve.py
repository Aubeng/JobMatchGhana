"""Entity resolution: map extracted text spans to canonical taxonomy entries and districts.

The entity model finds *where* a skill, trade or place is mentioned; this module decides
*which* canonical entry it is, by exact then fuzzy matching against the taxonomy's names and
synonyms (S&D Design §6.3.2: "entity resolution ... via fuzzy string matching"). That copes
with the spelling variation of WhatsApp Ghanaian English ("plastring", "vulcanizer").
"""
from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path

from rapidfuzz import fuzz, process

# Choices shorter than this are matched exactly only; fuzzy matching on "la", "pop" or "ho"
# would fire on almost anything.
_MIN_FUZZY_LEN = 4
_FILLER = re.compile(r"\b(?:the|a|an|of|my|some|and|work|works|job|jobs)\b")


def normalise(text: str) -> str:
    t = text.lower().replace("’", "'")
    t = re.sub(r"[^\w\s'-]", " ", t)
    return re.sub(r"\s+", " ", t).strip()


@dataclass(frozen=True)
class SkillMatch:
    skill_id: str
    name: str
    category_id: str
    category_name: str
    isco08: str
    score: float  # 0..1 string-match quality


@dataclass(frozen=True)
class CategoryMatch:
    category_id: str
    name: str
    score: float


@dataclass(frozen=True)
class LocationMatch:
    locality: str | None
    district: str
    region: str
    score: float


class _Index:
    """Normalised phrase -> payload index with exact-then-fuzzy lookup."""

    def __init__(self, threshold: float):
        self.threshold = threshold
        self.exact: dict[str, object] = {}
        self.fuzzy_choices: list[str] = []
        self.fuzzy_payload: list[object] = []

    def add(self, phrase: str, payload: object) -> None:
        key = normalise(phrase)
        if not key or key in self.exact:
            return
        self.exact[key] = payload
        if len(key) >= _MIN_FUZZY_LEN:
            self.fuzzy_choices.append(key)
            self.fuzzy_payload.append(payload)

    def lookup(self, text: str) -> tuple[object, float] | None:
        key = normalise(text)
        if key in self.exact:
            return self.exact[key], 1.0
        stripped = re.sub(r"\s+", " ", _FILLER.sub(" ", key)).strip()
        if stripped in self.exact:
            return self.exact[stripped], 0.97
        if len(key) < _MIN_FUZZY_LEN or not self.fuzzy_choices:
            return None
        hit = process.extractOne(key, self.fuzzy_choices, scorer=fuzz.WRatio,
                                 score_cutoff=self.threshold * 100)
        if hit is None:
            return None
        _, score, idx = hit
        return self.fuzzy_payload[idx], round(score / 100, 3)


class Taxonomy:
    def __init__(self, data: dict, threshold: float = 0.82):
        self.version: str = data["version"]
        self._skills = _Index(threshold)
        self._categories = _Index(threshold)
        self.categories: dict[str, dict] = {}
        for cat in data["categories"]:
            self.categories[cat["id"]] = cat
            cmatch = CategoryMatch(cat["id"], cat["name"], 1.0)
            self._categories.add(cat["name"], cmatch)
            for term in cat["occupation_terms"]:
                self._categories.add(term, cmatch)
            for sk in cat["skills"]:
                smatch = SkillMatch(sk["id"], sk["name"], cat["id"], cat["name"], sk["isco08"], 1.0)
                self._skills.add(sk["name"], smatch)
                for syn in sk["synonyms"]:
                    self._skills.add(syn, smatch)

    @classmethod
    def load(cls, path: Path, threshold: float = 0.82) -> "Taxonomy":
        return cls(json.loads(Path(path).read_text(encoding="utf-8")), threshold)

    def resolve_skill(self, text: str) -> SkillMatch | None:
        hit = self._skills.lookup(text)
        if not hit:
            return None
        m, score = hit
        return SkillMatch(m.skill_id, m.name, m.category_id, m.category_name, m.isco08, score)

    def resolve_category(self, text: str) -> CategoryMatch | None:
        hit = self._categories.lookup(text)
        if not hit:
            return None
        m, score = hit
        return CategoryMatch(m.category_id, m.name, score)

    def skill_names(self, category_id: str) -> list[str]:
        return [s["name"] for s in self.categories[category_id]["skills"]]


class Gazetteer:
    """District-level location resolution (S&D S4: ELICIT_LOCATION at district level)."""

    def __init__(self, districts: dict[str, list[str]], localities: dict, threshold: float = 0.88):
        self.version: str = localities["version"]
        self._index = _Index(threshold)
        for region, names in districts.items():
            for d in names:
                self._index.add(d, LocationMatch(None, d, region, 1.0))
        for loc in localities["localities"]:
            m = LocationMatch(loc["name"], loc["district"], loc["region"], 1.0)
            self._index.add(loc["name"], m)
            for alias in loc["aliases"]:
                self._index.add(alias, m)

    @classmethod
    def load(cls, districts_path: Path, localities_path: Path) -> "Gazetteer":
        return cls(json.loads(Path(districts_path).read_text(encoding="utf-8")),
                   json.loads(Path(localities_path).read_text(encoding="utf-8")))

    def resolve(self, text: str) -> LocationMatch | None:
        key = normalise(text)
        # "tema community 4", "kaneshie market area": retry with trailing words trimmed.
        parts = key.split()
        for n in range(len(parts), 0, -1):
            hit = self._index.lookup(" ".join(parts[:n]))
            if hit:
                m, score = hit
                return LocationMatch(m.locality, m.district, m.region,
                                     round(score * (1.0 if n == len(parts) else 0.95), 3))
        return None
