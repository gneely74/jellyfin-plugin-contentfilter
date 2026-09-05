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


def build_word_pattern(term: str) -> str:
    if not term or not term.strip():
        return ""
    term = term.strip()
    if " " in term:
        return rf"\b{re.escape(term)}\b"
    escaped = re.escape(term)
    if len(term) > 2 and term.lower().endswith("y") and term[-2].lower() not in "aeiou":
        stem = re.escape(term[:-1])
        return rf"\b(?:{stem}ies|{escaped})\b"
    if term.lower().endswith("ss"):
        return rf"\b(?:{escaped}es|{escaped})\b"
    if term.lower().endswith("s"):
        return rf"\b{escaped}\b"
    if any(term.lower().endswith(suffix) for suffix in ("sh", "ch", "x", "z")):
        return rf"\b(?:{escaped}es|{escaped})\b"
    return rf"\b(?:{escaped}s|{escaped})\b"


def detect_words(dialogue, target_words):
    found = []
    for word in target_words:
        pattern = re.compile(build_word_pattern(word), re.IGNORECASE)
        for match in pattern.finditer(dialogue):
            found.append((word, match.start(), match.end(), match.group(0)))
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


def test_detect_plurals_common_swear_words():
    text = "Those bastards and bitches are complete assholes! Don't be an ass or touch those asses."
    targets = ["bastard", "bitch", "asshole", "ass"]
    matches = detect_words(text, targets)
    matched_tokens = [m[3].lower() for m in matches]
    assert "bastards" in matched_tokens
    assert "bitches" in matched_tokens
    assert "assholes" in matched_tokens
    assert "ass" in matched_tokens
    assert "asses" in matched_tokens
    # Ensure unrelated word boundaries aren't matched
    assert "assessment" not in matched_tokens
    assert "bastardized" not in matched_tokens
