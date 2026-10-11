import pytest

from jobmatch_nlp.annotation import (AnnotationError, bio_tags, parse_line, spans_from_bio, to_markup,
                                     words)


def test_parse_strips_markup_and_records_spans():
    text, spans = parse_line("i can [lay blocks](SKILL_ITEM) at [Madina](LOCATION)")
    assert text == "i can lay blocks at Madina"
    assert [(s.text, s.label) for s in spans] == [("lay blocks", "SKILL_ITEM"), ("Madina", "LOCATION")]
    assert text[spans[0].start:spans[0].end] == "lay blocks"


def test_markup_round_trip():
    line = "am a [mason](TRADE_CATEGORY) for [5 yrs](EXPERIENCE_DURATION), i [plaster](SKILL_ITEM) too"
    text, spans = parse_line(line)
    assert to_markup(text, spans) == line


@pytest.mark.parametrize("line, message", [
    ("i [weld](SKILLS)", "unknown entity label"),
    ("i [ weld](SKILL_ITEM)", "leading/trailing"),
    ("i [weld(SKILL_ITEM)", "stray"),
    ("i [wel](SKILL_ITEM)ding", "cuts through a word"),
    ("   ", "empty"),
])
def test_rejects_malformed_lines(line, message):
    with pytest.raises(AnnotationError, match=message):
        parse_line(line)


def test_words_keep_contractions_and_split_punctuation():
    assert [w for w, _, _ in words("i'm a tiler, 5yrs!")] == ["i'm", "a", "tiler", ",", "5yrs", "!"]


def test_bio_tags_and_inverse():
    text, spans = parse_line("i [fix burst pipes](SKILL_ITEM) in [tema community 4](LOCATION)")
    tags = bio_tags(text, spans)
    assert tags == ["O", "B-SKILL_ITEM", "I-SKILL_ITEM", "I-SKILL_ITEM", "O",
                    "B-LOCATION", "I-LOCATION", "I-LOCATION"]
    assert [(s.text, s.label) for s in spans_from_bio(text, tags)] == [
        ("fix burst pipes", "SKILL_ITEM"), ("tema community 4", "LOCATION")]


def test_spans_from_bio_treats_orphan_inside_tag_as_a_start():
    text = "braiding and wigs"
    spans = spans_from_bio(text, ["I-SKILL_ITEM", "O", "B-SKILL_ITEM"])
    assert [s.text for s in spans] == ["braiding", "wigs"]
