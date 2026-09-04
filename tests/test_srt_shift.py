"""Unit tests for tools/shift_srt.py (shifting SRT subtitle timestamps)."""

import pytest
from tools.shift_srt import (
    shift_srt,
    parse_srt_timecode_to_ms,
    format_ms_to_srt_timecode,
    parse_srt_cues,
)

SAMPLE_SRT = """1
00:01:10,000 --> 00:01:14,500
Hello, world!
This is a test subtitle.

2
00:02:20,120 --> 00:02:25,890
Second subtitle cue.

3
00:05:00,000 --> 00:05:05,000
Third subtitle cue after a gap.
"""


def test_shift_srt_positive():
    shifted, count, total = shift_srt(SAMPLE_SRT, 2.5)
    assert total == 3
    assert count == 3

    # Check shifted timecodes (+2.5s = 2500ms)
    assert "00:01:12,500 --> 00:01:17,000" in shifted
    assert "00:02:22,620 --> 00:02:28,390" in shifted
    assert "00:05:02,500 --> 00:05:07,500" in shifted
    assert "Hello, world!\nThis is a test subtitle." in shifted


def test_shift_srt_negative():
    shifted, count, total = shift_srt(SAMPLE_SRT, -10.0)
    assert total == 3
    assert count == 3

    assert "00:01:00,000 --> 00:01:04,500" in shifted
    assert "00:02:10,120 --> 00:02:15,890" in shifted
    assert "00:04:50,000 --> 00:04:55,000" in shifted


def test_shift_srt_clamp_to_zero():
    sample = """1
00:00:02,000 --> 00:00:08,000
Early cue.
"""
    # Shifting by -5s would make start -3s, but end 3s
    shifted, count, total = shift_srt(sample, -5.0)
    assert count == 1
    assert "00:00:00,000 --> 00:00:03,000" in shifted


def test_shift_srt_drop_cue_before_zero():
    sample = """1
00:00:01,000 --> 00:00:03,000
Too early.

2
00:00:10,000 --> 00:00:15,000
Surviving cue.
"""
    # Shifting by -5s makes cue 1 end at -2s (dropped), cue 2 starts at 5s (survives)
    shifted, count, total = shift_srt(sample, -5.0)
    assert total == 2
    assert count == 1
    assert "Too early" not in shifted
    assert "Surviving cue" in shifted
    assert "1\n00:00:05,000 --> 00:00:10,000" in shifted


def test_srt_timecode_helpers():
    assert parse_srt_timecode_to_ms("01:02:03,456") == ((1 * 3600) + (2 * 60) + 3) * 1000 + 456
    assert parse_srt_timecode_to_ms("00:00:01.500") == 1500
    assert format_ms_to_srt_timecode(63456) == "00:01:03,456"
    assert format_ms_to_srt_timecode(-50) == "00:00:00,000"
