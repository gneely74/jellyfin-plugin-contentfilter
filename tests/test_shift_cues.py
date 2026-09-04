"""Unit tests for tools/shift_cues.py (shifting cues by channel: all, video, audio)."""

import pytest
from tools.shift_cues import shift_jcf, parse_timecode_to_ms, format_ms_to_timecode

SAMPLE_JCF = """WEBVTT JCF

NOTE
TITLE Game of Thrones S01E01
YEAR 2011
SOURCE test

00:10:00.000 --> 00:10:30.000
category: Violence.Graphic
channel: video
action: skip
description: Sword fight

00:20:00.000 --> 00:20:15.000
category: Language.Profanity
channel: audio
action: mute
description: Profanity

00:30:00.000 --> 00:30:45.000
category: SexAndNudity.Graphic
channel: both
action: skip
description: Scene skip
"""


def test_shift_all_cues():
    updated, shifted, total = shift_jcf(SAMPLE_JCF, 2.5, channel="all")
    assert total == 3
    assert shifted == 3

    # All three cues should be shifted by +2.5s (2500ms)
    assert "00:10:02.500 --> 00:10:32.500" in updated
    assert "00:20:02.500 --> 00:20:17.500" in updated
    assert "00:30:02.500 --> 00:30:47.500" in updated
    assert "TITLE Game of Thrones S01E01" in updated


def test_shift_video_cues_only():
    # Shift only video cues by +5.0s
    updated, shifted, total = shift_jcf(SAMPLE_JCF, 5.0, channel="video")
    assert total == 3
    # video cue and both (with skip action) should be shifted, audio cue untouched
    assert "00:10:05.000 --> 00:10:35.000" in updated
    assert "00:20:00.000 --> 00:20:15.000" in updated  # audio unchanged
    assert "00:30:05.000 --> 00:30:50.000" in updated


def test_shift_audio_cues_only():
    # Shift only audio cues by -3.0s
    updated, shifted, total = shift_jcf(SAMPLE_JCF, -3.0, channel="audio")
    assert total == 3
    assert shifted == 1

    assert "00:10:00.000 --> 00:10:30.000" in updated  # video unchanged
    assert "00:19:57.000 --> 00:20:12.000" in updated  # audio shifted
    assert "00:30:00.000 --> 00:30:45.000" in updated  # both (action: skip) unchanged


def test_shift_negative_clamp_to_zero():
    sample = """WEBVTT JCF

00:00:02.000 --> 00:00:10.000
category: Violence.Graphic
channel: video
action: skip
"""
    # Shift earlier by 5 seconds (start would be -3s)
    updated, shifted, total = shift_jcf(sample, -5.0, channel="video")
    assert shifted == 1
    assert "00:00:00.000 --> 00:00:05.000" in updated


def test_timecode_helpers():
    assert parse_timecode_to_ms("01:23:45.678") == (1 * 3600 + 23 * 60 + 45) * 1000 + 678
    assert format_ms_to_timecode(5025) == "00:00:05.025"
