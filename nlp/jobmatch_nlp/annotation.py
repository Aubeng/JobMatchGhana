"""Inline entity markup used by the training data: ``[span text](LABEL)``.

One utterance per line, in one file per intent (see docs/annotation-guidelines.md). This module
parses that format into plain text + character spans, splits text into words, and converts spans
to word-level BIO tags for the BERT+CRF entity model.
"""
from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path

INTENTS: tuple[str, ...] = (
    "SKILL_DECLARE",
    "TRADE_IDENTIFY",
    "LOCATION_SHARE",
    "AVAILABILITY_STATE",
    "CONFIRMATION",
    "CORRECTION",
    "HELP_REQUEST",
    "FEEDBACK_PROVIDE",
    "EXIT",
)

ENTITY_LABELS: tuple[str, ...] = ("SKILL_ITEM", "TRADE_CATEGORY", "LOCATION", "EXPERIENCE_DURATION")

# BIO tag set for the entity model; index 0 is "O".
BIO_TAGS: tuple[str, ...] = ("O",) + tuple(f"{p}-{label}" for label in ENTITY_LABELS for p in ("B", "I"))

_MARKUP = re.compile(r"\[([^\[\]]+)\]\(([A-Za-z_]+)\)")
_WORD = re.compile(r"\w+(?:[-'’]\w+)*|[^\w\s]")


class AnnotationError(ValueError):
    """A line that violates the markup rules."""


@dataclass(frozen=True)
class Span:
    start: int
    end: int
    label: str
    text: str


@dataclass(frozen=True)
class Example:
    text: str
    intent: str
    spans: tuple[Span, ...]
    source: str = ""


def parse_line(line: str) -> tuple[str, tuple[Span, ...]]:
    """Strip inline markup, returning the plain utterance and its entity spans."""
    plain: list[str] = []
    spans: list[Span] = []
    pos = 0
    length = 0
    for m in _MARKUP.finditer(line):
        before = line[pos:m.start()]
        plain.append(before)
        length += len(before)
        span_text, label = m.group(1), m.group(2)
        if label not in ENTITY_LABELS:
            raise AnnotationError(f"unknown entity label '{label}'")
        if span_text != span_text.strip() or not span_text.strip():
            raise AnnotationError(f"span '{span_text}' has leading/trailing spaces or is empty")
        spans.append(Span(length, length + len(span_text), label, span_text))
        plain.append(span_text)
        length += len(span_text)
        pos = m.end()
    plain.append(line[pos:])
    text = "".join(plain)
    if any(ch in text for ch in "[]"):
        raise AnnotationError("stray '[' or ']' (malformed markup?)")
    if not text.strip():
        raise AnnotationError("empty utterance")
    _check_word_aligned(text, spans)
    return text, tuple(spans)


def to_markup(text: str, spans: tuple[Span, ...] | list[Span]) -> str:
    out, pos = [], 0
    for s in sorted(spans, key=lambda s: s.start):
        out.append(text[pos:s.start])
        out.append(f"[{text[s.start:s.end]}]({s.label})")
        pos = s.end
    out.append(text[pos:])
    return "".join(out)


def words(text: str) -> list[tuple[str, int, int]]:
    """Split into words/punctuation with character offsets (the entity model's units)."""
    return [(m.group(0), m.start(), m.end()) for m in _WORD.finditer(text)]


def bio_tags(text: str, spans: tuple[Span, ...] | list[Span]) -> list[str]:
    """Word-level BIO tags for ``text``."""
    tags = []
    for _, ws, we in words(text):
        tag = "O"
        for s in spans:
            if ws >= s.start and we <= s.end:
                tag = ("B-" if ws == s.start else "I-") + s.label
                break
        tags.append(tag)
    return tags


def spans_from_bio(text: str, tags: list[str]) -> list[Span]:
    """Inverse of :func:`bio_tags`: rebuild character spans from word-level tags."""
    toks = words(text)
    spans: list[Span] = []
    cur: list[int] | None = None  # [start, end, label_index]
    label = ""
    for (_, ws, we), tag in zip(toks, tags):
        if tag.startswith("B-") or (tag.startswith("I-") and (cur is None or tag[2:] != label)):
            if cur:
                spans.append(Span(cur[0], cur[1], label, text[cur[0]:cur[1]]))
            label, cur = tag[2:], [ws, we]
        elif tag.startswith("I-") and cur is not None:
            cur[1] = we
        else:
            if cur:
                spans.append(Span(cur[0], cur[1], label, text[cur[0]:cur[1]]))
            cur = None
    if cur:
        spans.append(Span(cur[0], cur[1], label, text[cur[0]:cur[1]]))
    return spans


def _check_word_aligned(text: str, spans: list[Span]) -> None:
    starts = {ws for _, ws, _ in words(text)}
    ends = {we for _, _, we in words(text)}
    for s in spans:
        if s.start not in starts or s.end not in ends:
            raise AnnotationError(f"span '{s.text}' cuts through a word")


def load_examples(directory: Path) -> tuple[list[Example], list[str]]:
    """Load every ``<INTENT>.txt`` in ``directory``. Returns (examples, error messages)."""
    examples: list[Example] = []
    errors: list[str] = []
    for path in sorted(Path(directory).glob("*.txt")):
        intent = path.stem
        if intent not in INTENTS:
            errors.append(f"{path.name}: file name is not one of the 9 intents")
            continue
        for n, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            try:
                text, spans = parse_line(line)
            except AnnotationError as e:
                errors.append(f"{path.name}:{n}: {e}: {line}")
                continue
            examples.append(Example(text, intent, spans, f"{path.name}:{n}"))
    return examples, errors
