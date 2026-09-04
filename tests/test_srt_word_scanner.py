"""Unit tests for subtitle word scanning and parsing concepts."""

import re
import pytest


def parse_srt_block(block_text):
    pattern = re.compile(
        r"(?:^\d+\s*\r?\n)?(?P<start>\d{2}:\d{2}:\d{2}[,\.]\d{3})\s*-->\s*(?P<end>\d{2}:\d{2}:\d{2}[,\.]\d{3})\r?\n(?P<dialogue>(?:.+|\r?\n)+)",
        re.MULTILINE,
    )
    m = pattern.search(block_text.strip())
    if not m:
        return None
    start = m.group("start").replace(",", ".")
    end = m.group("end").replace(",", ".")
    dialogue = re.sub(r"<[^>]+>", "", m.group("dialogue"))
    dialogue = re.sub(r"\{[^\}]+\}", "", dialogue).strip()
    return {"start": start, "end": end, "dialogue": dialogue}


def detect_words(dialogue, target_words):
    found = []
    for word in target_words:
        pattern = re.compile(rf"\b{re.escape(word)}\b", re.IGNORECASE)
        for match in pattern.finditer(dialogue):
            found.append((word, match.start(), match.end()))
    return found


def test_parse_srt_block_clean():
    sample = """1
00:14:22,120 --> 00:14:25,890
You're a bastard, Jon Snow.
"""
    parsed = parse_srt_block(sample)
    assert parsed is not None
    assert parsed["start"] == "00:14:22.120"
    assert parsed["end"] == "00:14:25.890"
    assert parsed["dialogue"] == "You're a bastard, Jon Snow."


def test_parse_srt_block_html_and_ass_tags():
    sample = """42
00:01:05.500 --> 00:01:09.200
<i>{\\an8}Look at that damn fool!</i>
"""
    parsed = parse_srt_block(sample)
    assert parsed is not None
    assert parsed["start"] == "00:01:05.500"
    assert parsed["end"] == "00:01:09.200"
    assert parsed["dialogue"] == "Look at that damn fool!"


def test_detect_words_exact_boundaries():
    text = "He is a bastard. Not bastardized, just a bastard!"
    words = ["bastard"]
    matches = detect_words(text, words)
    assert len(matches) == 2
    assert matches[0][0] == "bastard"
    assert matches[1][0] == "bastard"


def test_detect_multiple_words_case_insensitive():
    text = "What the Hell and God Damn is that shit?"
    targets = ["hell", "god damn", "shit"]
    matches = detect_words(text, targets)
    found_words = [m[0] for m in matches]
    assert "hell" in found_words
    assert "god damn" in found_words
    assert "shit" in found_words
