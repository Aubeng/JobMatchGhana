"""Normalise EXPERIENCE_DURATION spans ("5 yrs", "since 2018", "six months") to years."""
from __future__ import annotations

import re
from datetime import date

_NUMBER_WORDS = {
    "a": 1, "an": 1, "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7,
    "eight": 8, "nine": 9, "ten": 10, "eleven": 11, "twelve": 12, "thirteen": 13, "fourteen": 14,
    "fifteen": 15, "sixteen": 16, "seventeen": 17, "eighteen": 18, "nineteen": 19, "twenty": 20,
    "thirty": 30, "forty": 40, "couple": 2, "few": 3, "several": 4,
}
_UNIT_YEARS = {"year": 1.0, "yr": 1.0, "month": 1 / 12, "mth": 1 / 12, "week": 1 / 52, "wk": 1 / 52}

_HALF = re.compile(r"\band\s+(?:a\s+)?half\b")
_AMOUNT_UNIT = re.compile(
    r"\b(?P<num>\d+(?:\.\d+)?|" + "|".join(sorted(_NUMBER_WORDS, key=len, reverse=True)) + r")"
    r"\s*\+?\s*(?:and\s+(?:a\s+)?half\s+)?"
    r"(?P<unit>years?|yrs?|months?|mths?|weeks?|wks?)\b"
)
_SINCE_YEAR = re.compile(r"\b(?:since|from|in)\s+(?P<year>(?:19|20)\d{2})\b")


def to_years(text: str, today: date | None = None) -> float | None:
    """Best-effort conversion to years; ``None`` when the span can't be quantified
    (e.g. "since i left school"). The raw span is always kept alongside."""
    t = text.lower().replace("½", " and half")
    today = today or date.today()

    m = _SINCE_YEAR.search(t)
    if m:
        year = int(m.group("year"))
        if year <= today.year:
            return float(max(today.year - year, 0))

    m = _AMOUNT_UNIT.search(t)
    if not m:
        return None
    raw = m.group("num")
    amount = float(raw) if raw[0].isdigit() else float(_NUMBER_WORDS[raw])
    if _HALF.search(t):
        amount += 0.5
    unit = m.group("unit").rstrip("s")
    years = amount * _UNIT_YEARS[unit]
    return round(years, 2)
