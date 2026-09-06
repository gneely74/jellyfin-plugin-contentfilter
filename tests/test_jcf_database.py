"""Tests for the JCF content filter database, parsers, and file validation."""

import json
import sqlite3
import pytest

import tools.reddit_to_jcf as rj
from tools.jcf_db import CATALOG_DB, CATALOG_JSON, MOVIES_DIR, SHOWS_DIR


VALID_CATEGORIES = {
    # Language
    "Language.GeneralProfanity",
    "Language.Blasphemy",
    "Language.RacialAndBigotedSlurs",
    "Language.ChildishLanguage",
    "Language.CaptionsWithProfanity",
    # Sexual References
    "SexualReferences.ExplicitWords",
    "SexualReferences.ContextualDialogue",
    "SexualReferences.Visuals",
    # Sex & Nudity
    "SexAndNudity.Graphic",
    "SexAndNudity.ImpliedSex",
    "SexAndNudity.SexualAssault",
    "SexAndNudity.FullNudity",
    "SexAndNudity.PartialNudity",
    "SexAndNudity.PhysicalIntimacy",
    "SexAndNudity.Mild",
    "SexAndNudity.OnscreenActivity",
    "SexAndNudity.NudityProfiles",
    # Violence & Horror
    "Violence.Mild",
    "Violence.Moderate",
    "Violence.Graphic",
    "Violence.Gore",
    "Violence.JumpScares",
    "Violence.Disturbing",
    "Violence.Tiers",
    # Substances
    "Substances.Tobacco",
    "Substances.Alcohol",
    "Substances.IllegalDrugs",
    "Substances.Usage",
    # Medical
    "Medical.Events",
    "Medical.BodilyFunctions",
    # Structural
    "Structural.Credits",
    "Structural.IntroRecap",
    "Structural.Timestamps",
}

VALID_CHANNELS = {"video", "audio", "both"}
VALID_ACTIONS = {"skip", "mute", "none"}


def test_timestamp_parser_to_ms():
    assert rj.parse_timestamp_to_ms("00:00") == 0
    assert rj.parse_timestamp_to_ms("01:30") == 90_000
    assert rj.parse_timestamp_to_ms("1:02:03") == (3600 + 120 + 3) * 1000
    assert rj.parse_timestamp_to_ms("01:02:03.500") == (3600 + 120 + 3) * 1000 + 500


def test_ms_to_timestamp():
    assert rj.ms_to_timestamp(0) == "00:00:00.000"
    assert rj.ms_to_timestamp(90_000) == "00:01:30.000"
    assert rj.ms_to_timestamp(3723500) == "01:02:03.500"


def test_parse_reddit_post_text():
    sample = """
    **NUDITY**
    12:30 - 14:00 Topless scene
    GORE AND VIOLENCE
    1:05:00 - 1:07:30 Knife battle with blood
    """
    cues = rj.parse_reddit_post_text(sample)
    assert len(cues) == 2
    assert cues[0].category == "SexAndNudity.FullNudity"
    assert cues[0].start_str == "00:12:30.000"
    assert cues[0].end_str == "00:14:00.000"
    assert cues[1].category == "Violence.Gore"
    assert cues[1].start_str == "01:05:00.000"
    assert cues[1].end_str == "01:07:30.000"


def test_invert_safe_ranges():
    safe_ranges = [(0, 10_000), (15_000, 30_000), (45_000, 60_000)]
    skip_cues = rj.invert_safe_ranges(safe_ranges)
    assert len(skip_cues) == 2
    assert skip_cues[0].start_ms == 10_000
    assert skip_cues[0].end_ms == 15_000
    assert skip_cues[1].start_ms == 30_000
    assert skip_cues[1].end_ms == 45_000


def test_database_files_exist():
    assert CATALOG_DB.exists()
    assert CATALOG_JSON.exists()
    assert MOVIES_DIR.exists()
    assert SHOWS_DIR.exists()


def test_catalog_sqlite_integrity():
    conn = sqlite3.connect(CATALOG_DB)
    cur = conn.cursor()
    cur.execute("SELECT COUNT(*) FROM titles")
    title_count = cur.fetchone()[0]
    assert title_count > 600

    cur.execute("SELECT COUNT(*) FROM cues")
    cue_count = cur.fetchone()[0]
    assert cue_count > 10_000

    # Ensure all cues have valid chronological order (end_ms > start_ms)
    cur.execute("SELECT COUNT(*) FROM cues WHERE end_ms <= start_ms")
    invalid_cues = cur.fetchone()[0]
    assert invalid_cues == 0

    # Ensure all cue categories are supported
    cur.execute("SELECT DISTINCT category FROM cues")
    categories = [r[0] for r in cur.fetchall()]
    for cat in categories:
        assert cat in VALID_CATEGORIES, f"Unknown category: {cat}"

    conn.close()


def test_catalog_json_integrity():
    with open(CATALOG_JSON, "r", encoding="utf-8") as f:
        data = json.load(f)
    assert len(data) > 600
    for item in data[:50]:
        assert "title" in item
        assert "media_type" in item
        assert "cue_count" in item
        assert "categories" in item
        assert "jcf_path" in item


def test_jcf_file_formatting_and_webvtt_spec():
    # Sample test 100 movie files and all show files
    movie_files = list(MOVIES_DIR.glob("*.jcf"))[:100]
    show_files = list(SHOWS_DIR.glob("*/*.jcf"))
    assert len(movie_files) >= 100
    assert len(show_files) >= 8

    test_files = movie_files + show_files
    for jcf_path in test_files:
        content = jcf_path.read_text(encoding="utf-8")
        lines = content.splitlines()
        assert len(lines) >= 8, f"{jcf_path} has too few lines"
        assert lines[0] == "WEBVTT JCF", f"{jcf_path} missing WEBVTT JCF header"
        assert lines[2] == "NOTE", f"{jcf_path} missing NOTE block"
        assert lines[3].startswith("TITLE "), f"{jcf_path} missing TITLE"

        cue_lines = [l for l in lines if "-->" in l]
        assert len(cue_lines) > 0, f"{jcf_path} has no cue timecodes"

        for cl in cue_lines:
            parts = cl.split("-->")
            assert len(parts) == 2, f"Invalid cue line: {cl}"
            start_str = parts[0].strip()
            end_str = parts[1].strip()
            s_ms = rj.parse_timestamp_to_ms(start_str)
            e_ms = rj.parse_timestamp_to_ms(end_str)
            assert e_ms > s_ms, f"{jcf_path}: Cue end {end_str} <= start {start_str}"


def test_category_mapping_vidangel_imdb():
    cat, chan, act = rj.map_category_and_channel("VIOLENCE", "decapitation and severe gore")
    assert cat == "Violence.Gore"
    assert chan == "video"
    assert act == "skip"

    cat, chan, act = rj.map_category_and_channel("FEAR", "sudden jump scare monster jumps")
    assert cat == "Violence.JumpScares"

    cat, chan, act = rj.map_category_and_channel("SEX", "rape scene")
    assert cat == "SexAndNudity.SexualAssault"

    cat, chan, act = rj.map_category_and_channel("DRUGS", "characters drinking wine at a bar")
    assert cat == "Substances.Alcohol"

    cat, chan, act = rj.map_category_and_channel("DRUGS", "smoking cigarettes")
    assert cat == "Substances.Tobacco"

    cat, chan, act = rj.map_category_and_channel("DRUGS", "snorting cocaine")
    assert cat == "Substances.IllegalDrugs"

    cat, chan, act = rj.map_category_and_channel("LANGUAGE", "jesus christ god damn")
    assert cat == "Language.Blasphemy"
    assert chan == "audio"
    assert act == "mute"


def test_upgrade_jcf_file(tmp_path):
    legacy_file = tmp_path / "test_legacy.jcf"
    legacy_file.write_text(
        "WEBVTT JCF\n\nNOTE\nTITLE Test Legacy\nYEAR 2020\n\n"
        "00:01:00.000 --> 00:02:00.000\ncategory: Violence.Tiers\nchannel: video\naction: skip\ndescription: A decapitation and severe gore\n\n"
        "00:03:00.000 --> 00:04:00.000\ncategory: Substances.Usage\nchannel: video\naction: skip\ndescription: Drinking alcohol at a bar\n",
        encoding="utf-8",
    )

    did_upgrade = rj.upgrade_jcf_file(legacy_file)
    assert did_upgrade is True

    upgraded_doc = rj.load_jcf_file(legacy_file)
    assert upgraded_doc.cues[0].category == "Violence.Gore"
    assert upgraded_doc.cues[1].category == "Substances.Alcohol"

