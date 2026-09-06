"""Unit tests for process_to_jcf."""

from pathlib import Path
import pytest
from process_to_jcf import (
    Cue,
    build_jcf_content,
    clean_line_typos,
    format_time_seconds,
    map_category,
    map_category_and_channel,
    merge_cues,
    parse_post_into_cues,
    parse_time_to_seconds,
    process_lord_of_the_rings_trilogy,
)


def test_format_time_seconds():
    assert format_time_seconds(0) == "00:00:00.000"
    assert format_time_seconds(78) == "00:01:18.000"
    assert format_time_seconds(3665) == "01:01:05.000"


def test_parse_time_to_seconds():
    assert parse_time_to_seconds("0") == 0
    assert parse_time_to_seconds("00:00") == 0
    assert parse_time_to_seconds("4:13") == 253
    assert parse_time_to_seconds("14:39") == 879
    assert parse_time_to_seconds("1:18:21") == 4701
    assert parse_time_to_seconds("56 seconds") == 56
    assert parse_time_to_seconds("invalid") is None


def test_clean_line_typos():
    assert clean_line_typos("1:18: 21 Ellen kisses") == "1:18:21 Ellen kisses"
    assert clean_line_typos("1:35 28 Shots are fired") == "1:35:28 Shots are fired"


def test_map_category():
    assert map_category("VIOLENCE") == "Violence.Moderate"
    assert map_category("GORE, MAY BE DISTURBING") == "Violence.Gore"
    assert map_category("NUDITY") == "SexAndNudity.FullNudity"
    assert map_category("UNDERWEAR") == "SexAndNudity.PartialNudity"
    assert map_category("ALCOHOL CONTENT") == "Substances.Alcohol"
    assert map_category("INAPPROPRIATE TALK") == "Language.GeneralProfanity"
    assert map_category("SEIZURE WARNING") == "Medical.Events"
    assert map_category("JUMPSCARE") == "Violence.JumpScares"
    assert map_category("UNKNOWN") == "Violence.Moderate"


def test_map_category_and_channel():
    cat, channel, action = map_category_and_channel("INAPPROPRIATE TALK", "f-word used")
    assert cat == "Language.GeneralProfanity"
    assert channel == "audio"
    assert action == "mute"

    cat, channel, action = map_category_and_channel("GORE", "severed hand")
    assert cat == "Violence.Gore"
    assert channel == "video"
    assert action == "skip"

    cat, channel, action = map_category_and_channel("SEIZURE WARNING", "strobe lights flashing")
    assert cat == "Medical.Events"
    assert channel == "both"
    assert action == "skip"


def test_parse_post_into_cues():
    sample_text = """
VIOLENCE
4:13 - 4:25 Explosion knocks character down.
SUGGESTIVE
6:27 - 6:34 Characters wearing suggestive workout outfits.
NUDITY
1:22:51 - 1:24:12 Nude photos on wall.
"""
    cues = parse_post_into_cues(sample_text)
    assert len(cues) == 3
    assert cues[0].start_str == "00:04:13.000"
    assert cues[0].end_str == "00:04:25.000"
    assert cues[0].category == "Violence.Moderate"
    assert "[VIOLENCE]" in cues[0].description

    assert cues[1].start_str == "00:06:27.000"
    assert cues[1].end_str == "00:06:34.000"
    assert cues[1].category == "SexAndNudity.PartialNudity"

    assert cues[2].start_str == "01:22:51.000"
    assert cues[2].end_str == "01:24:12.000"
    assert cues[2].category == "SexAndNudity.FullNudity"


def test_merge_cues():
    cue1 = Cue(
        start_seconds=10,
        end_seconds=20,
        category="Violence.Moderate",
        description="Fight 1",
        raw_category="VIOLENCE",
    )
    cue2 = Cue(
        start_seconds=19,
        end_seconds=30,
        category="Violence.Moderate",
        description="Fight 2",
        raw_category="VIOLENCE",
    )
    merged = merge_cues([cue1, cue2])
    assert len(merged) == 1
    assert merged[0].start_seconds == 10
    assert merged[0].end_seconds == 30
    assert "Fight 1" in merged[0].description
    assert "Fight 2" in merged[0].description


def test_build_jcf_content():
    cues = [
        Cue(
            start_seconds=60,
            end_seconds=90,
            category="Violence.Gore",
            description="[GORE] Blood splatter",
            raw_category="GORE",
        )
    ]
    content = build_jcf_content("The Movie", 1999, cues)
    assert content.startswith("WEBVTT JCF\n\nNOTE\nTITLE The Movie\nYEAR 1999")
    assert "00:01:00.000 --> 00:01:30.000" in content
    assert "category: Violence.Gore" in content
    assert "description: [GORE] Blood splatter" in content
    assert "action: skip" in content


def test_process_lord_of_the_rings_trilogy(tmp_path: Path):
    sample_lotr = """
The Lord of the Rings: Fellowship of the Ring
KISS
1:25:04 - 1:26:36 Kiss scene.
The Lord of the Rings: The Two Towers
POSSIBLE NUDITY?
15:45 - 16:00 Creature seen.
The Lord of the rings: The Return of the King
KISS
27:34 - 27:40 Forehead kiss.
"""
    results = process_lord_of_the_rings_trilogy(sample_lotr, tmp_path)
    assert len(results) == 3
    filenames = [r[0] for r in results]
    assert any("Fellowship" in f for f in filenames)
    assert any("Two Towers" in f for f in filenames)
    assert any("Return of the King" in f for f in filenames)
