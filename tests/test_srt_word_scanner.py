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


def compute_smart_clause_window(block_start: float, block_end: float, dialogue: str, match_idx: int, match_len: int):
    """Mirror of SubtitleWordScanner.ComputeSmartClauseWindow for unit verification."""
    block_dur = block_end - block_start
    if block_dur <= 2.2 or not dialogue or len(dialogue) <= 1:
        return block_start, block_end

    total_len = len(dialogue)
    clause_char_start = 0
    clause_char_end = total_len

    clause_re = re.compile(r"(?<=[.?!;])\s+|\s*(?:—|--)\s*|(?<=\s)-\s+")
    splits = list(clause_re.finditer(dialogue))
    if splits:
        cur_start = 0
        for split in splits:
            cur_end = split.start()
            if cur_start <= match_idx < cur_end + len(split.group(0)):
                clause_char_start = cur_start
                clause_char_end = cur_end
                break
            cur_start = split.end()
            clause_char_start = cur_start

    start_ratio = max(0.0, min(1.0, clause_char_start / total_len))
    end_ratio = max(start_ratio, min(1.0, clause_char_end / total_len))
    raw_start = block_start + start_ratio * block_dur
    raw_end = block_start + end_ratio * block_dur

    word_start = block_start + (match_idx / total_len) * block_dur
    word_end = block_start + ((match_idx + max(1, match_len)) / total_len) * block_dur
    if (raw_end - raw_start) > 3.5:
        raw_start = max(raw_start, word_start - 0.9)
        raw_end = min(raw_end, word_end + 0.9)

    padded_start = raw_start - 0.2
    padded_end = raw_end + 0.2
    if (padded_end - padded_start) < 1.25:
        half_def = (1.25 - (padded_end - padded_start)) / 2.0
        padded_start -= half_def
        padded_end += half_def

    c_start = max(block_start, min(block_end, padded_start))
    c_end = max(c_start, min(block_end, padded_end))
    return c_start, c_end


def test_smart_clause_window_splits_multi_sentence_block():
    # 5-second block with a clean first sentence and profane second sentence
    dialogue = "Wait right here for a moment. What the hell are you doing?"
    idx = dialogue.index("hell")
    c_start, c_end = compute_smart_clause_window(10.0, 15.0, dialogue, idx, 4)
    # First sentence ("Wait right here for a moment.") should NOT be muted at 10.0s
    assert c_start > 12.0
    assert c_end == pytest.approx(15.0)
    assert (c_end - c_start) >= 1.25

