"""Unit tests for got_to_jcf."""

import os
import pytest
from got_to_jcf import build_jcf, fmt_ts, invert_ranges, main, parse_range


def test_fmt_ts_standard():
    """Validate fmt_ts correctly pads and appends milliseconds."""
    assert fmt_ts("00:30:30") == "00:30:30.000"
    assert fmt_ts("1:05:09") == "01:05:09.000"
    assert fmt_ts(" 00:00:00 ") == "00:00:00.000"


def test_fmt_ts_invalid():
    """Validate fmt_ts raises ValueError on invalid formats."""
    with pytest.raises(ValueError, match="Invalid timestamp"):
        fmt_ts("invalid")
    with pytest.raises(ValueError, match="Invalid timestamp"):
        fmt_ts("30:30")
    with pytest.raises(ValueError, match="Invalid timestamp"):
        fmt_ts("01:23:45.678")


def test_parse_range_valid():
    """Validate parse_range parses start and end timestamps."""
    start, end = parse_range("00:00:00-00:30:30")
    assert start == "00:00:00.000"
    assert end == "00:30:30.000"


def test_parse_range_invalid():
    """Validate parse_range raises ValueError when delimiter is missing."""
    with pytest.raises(ValueError, match="Invalid range"):
        parse_range("00:00:00")


def test_invert_ranges_basic():
    """Validate invert_ranges correctly finds gaps between safe ranges."""
    safe = [
        ("00:00:00.000", "00:30:30.000"),
        ("00:31:15.000", "00:31:30.000"),
        ("00:31:55.000", "00:32:10.000"),
    ]
    gaps = invert_ranges(safe)
    assert gaps == [
        ("00:30:30.000", "00:31:15.000"),
        ("00:31:30.000", "00:31:55.000"),
    ]


def test_invert_ranges_empty_and_single():
    """Validate invert_ranges with empty or single range produces no gaps."""
    assert invert_ranges([]) == []
    assert invert_ranges([("00:00:00.000", "00:30:00.000")]) == []


def test_invert_ranges_unsorted_and_overlapping():
    """Validate invert_ranges handles unsorted and overlapping safe ranges."""
    safe = [
        ("00:10:00.000", "00:20:00.000"),
        ("00:00:00.000", "00:12:00.000"),  # overlaps 00:00:00-00:20:00
        ("00:25:00.000", "00:30:00.000"),
    ]
    gaps = invert_ranges(safe)
    assert gaps == [
        ("00:20:00.000", "00:25:00.000"),
    ]


def test_build_jcf_empty():
    """Validate build_jcf returns empty string when input is empty or whitespace."""
    assert build_jcf("S01E03", "", "Game of Thrones S01E03", "2011") == ""
    assert build_jcf("S01E03", "   ", "Game of Thrones S01E03", "2011") == ""


def test_build_jcf_valid():
    """Validate build_jcf generates valid WEBVTT JCF output."""
    ranges = "00:00:00-00:10:00,+00:15:00-00:20:00"
    content = build_jcf("S01E01", ranges, "Game of Thrones S01E01", "2011")
    assert content.startswith("WEBVTT JCF\n\nNOTE\nTITLE Game of Thrones S01E01\nYEAR 2011")
    assert "00:10:00.000 --> 00:15:00.000" in content
    assert "category: Violence.Tiers" in content
    assert "channel: video" in content
    assert "action: skip" in content


def test_main_generates_files(tmp_path, monkeypatch):
    """Validate main generates sidecar files in the output directory."""
    monkeypatch.setattr("sys.argv", ["got_to_jcf.py", str(tmp_path)])
    main()
    jcf_files = list(tmp_path.glob("*.jcf"))
    assert len(jcf_files) > 0
    s01e01 = tmp_path / "S01E01.jcf"
    assert s01e01.exists()
    content = s01e01.read_text(encoding="utf-8")
    assert "WEBVTT JCF" in content
    assert "00:30:30.000 --> 00:31:15.000" in content

