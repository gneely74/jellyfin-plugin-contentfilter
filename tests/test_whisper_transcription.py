import pytest
import re
from typing import Any


def format_srt_timestamp(seconds: float) -> str:
    """Format seconds into SRT timestamp HH:MM:SS,mmm."""
    if seconds < 0:
        seconds = 0.0
    total_ms = int(round(seconds * 1000))
    hours = total_ms // 3600000
    total_ms %= 3600000
    minutes = total_ms // 60000
    total_ms %= 60000
    secs = total_ms // 1000
    ms = total_ms % 1000
    return f"{hours:02d}:{minutes:02d}:{secs:02d},{ms:03d}"


def convert_segments_to_srt(segments: list[dict[str, Any]]) -> str:
    """Convert Whisper segments to valid SRT string."""
    blocks = []
    for i, seg in enumerate(segments, 1):
        start_ts = format_srt_timestamp(seg.get("start", 0.0))
        end_ts = format_srt_timestamp(seg.get("end", 0.0))
        text = seg.get("text", "").strip()
        blocks.append(f"{i}\n{start_ts} --> {end_ts}\n{text}\n")
    return "\n".join(blocks)


def get_generated_sub_paths(video_path: str, lang: str = "en") -> tuple[str, str]:
    """Calculate sidecar filenames matching Jellyfin ExternalPathParser conventions."""
    stem = video_path.rsplit(".", 1)[0]
    filtered = f"{stem}.{lang}.Generated - Filtered.default.srt"
    unfiltered = f"{stem}.{lang}.Generated - Unfiltered.srt"
    return filtered, unfiltered


def has_media_changed(
    recorded_path: str,
    recorded_size: int,
    recorded_mtime: int,
    current_path: str,
    current_size: int,
    current_mtime: int,
) -> bool:
    """Detect if media file was upgraded or replaced."""
    if recorded_path != current_path:
        return True
    if recorded_size != current_size:
        return True
    if current_mtime > recorded_mtime:
        return True
    return False


def calculate_word_mute_cues(
    words: list[dict[str, Any]], profanity_terms: set[str], pad_start: float = 0.1, pad_end: float = 0.15
) -> list[tuple[float, float, str]]:
    """Generate mute cues from word-level timestamps with padding and merge overlaps."""
    cues = []
    for w in words:
        clean_word = re.sub(r"[^\w]", "", w.get("word", "").lower())
        if clean_word in profanity_terms:
            start = max(0.0, float(w.get("start", 0.0)) - pad_start)
            end = float(w.get("end", 0.0)) + pad_end
            cues.append((start, end, clean_word))

    # Merge overlapping cues
    if not cues:
        return []
    cues.sort(key=lambda c: c[0])
    merged = [cues[0]]
    for cur_start, cur_end, cur_word in cues[1:]:
        prev_start, prev_end, prev_word = merged[-1]
        if cur_start <= prev_end:
            merged[-1] = (prev_start, max(prev_end, cur_end), f"{prev_word},{cur_word}")
        else:
            merged.append((cur_start, cur_end, cur_word))
    return merged


# ================= TESTS =================


def test_format_srt_timestamp():
    assert format_srt_timestamp(0.0) == "00:00:00,000"
    assert format_srt_timestamp(1.5) == "00:00:01,500"
    assert format_srt_timestamp(65.123) == "00:01:05,123"
    assert format_srt_timestamp(3661.045) == "01:01:01,045"
    assert format_srt_timestamp(-5.0) == "00:00:00,000"


def test_convert_segments_to_srt():
    segments = [
        {"start": 1.2, "end": 4.5, "text": "Hello world"},
        {"start": 5.0, "end": 7.8, "text": "This is Whisper transcription."},
    ]
    srt = convert_segments_to_srt(segments)
    expected = (
        "1\n00:00:01,200 --> 00:00:04,500\nHello world\n\n"
        "2\n00:00:05,000 --> 00:00:07,800\nThis is Whisper transcription.\n"
    )
    assert srt == expected


def test_get_generated_sub_paths():
    video_file = "/media/movies/A Bridge Too Far (1977)/A Bridge Too Far (1977) - 1080p.mkv"
    filtered, unfiltered = get_generated_sub_paths(video_file, "en")

    assert filtered == "/media/movies/A Bridge Too Far (1977)/A Bridge Too Far (1977) - 1080p.en.Generated - Filtered.default.srt"
    assert unfiltered == "/media/movies/A Bridge Too Far (1977)/A Bridge Too Far (1977) - 1080p.en.Generated - Unfiltered.srt"

    # Verify that Jellyfin tokens would detect '.default.srt'
    assert filtered.endswith(".default.srt")
    assert not unfiltered.endswith(".default.srt")


def test_has_media_changed_upgrade_path():
    # Resolution upgraded from 1080p to 2160p (different path)
    old_path = "/media/movies/Movie (2020)/Movie (2020) [1080p].mkv"
    new_path = "/media/movies/Movie (2020)/Movie (2020) [2160p 4K Remux].mkv"
    assert has_media_changed(old_path, 5000000000, 1700000000, new_path, 25000000000, 1710000000)


def test_has_media_changed_same_path_newer_mtime():
    path = "/media/movies/Movie (2020)/Movie (2020).mkv"
    assert has_media_changed(path, 5000, 1000, path, 5000, 1050)


def test_has_media_changed_same_path_different_size():
    path = "/media/movies/Movie (2020)/Movie (2020).mkv"
    assert has_media_changed(path, 5000, 1000, path, 5200, 1000)


def test_has_media_unchanged():
    path = "/media/movies/Movie (2020)/Movie (2020).mkv"
    assert not has_media_changed(path, 5000, 1000, path, 5000, 1000)


def test_calculate_word_mute_cues():
    words = [
        {"word": "You", "start": 10.0, "end": 10.3},
        {"word": "bloody", "start": 10.4, "end": 10.8},
        {"word": "fool", "start": 10.9, "end": 11.2},
        {"word": "damn", "start": 15.0, "end": 15.4},
        {"word": "it", "start": 15.5, "end": 15.7},
    ]
    profanities = {"bloody", "damn"}
    cues = calculate_word_mute_cues(words, profanities, pad_start=0.1, pad_end=0.15)

    assert len(cues) == 2
    # "bloody": start=10.4-0.1=10.3, end=10.8+0.15=10.95
    assert pytest.approx(cues[0][0]) == 10.3
    assert pytest.approx(cues[0][1]) == 10.95
    assert cues[0][2] == "bloody"

    # "damn": start=15.0-0.1=14.9, end=15.4+0.15=15.55
    assert pytest.approx(cues[1][0]) == 14.9
    assert pytest.approx(cues[1][1]) == 15.55
    assert cues[1][2] == "damn"


def test_calculate_word_mute_cues_overlapping_merge():
    words = [
        {"word": "son", "start": 5.0, "end": 5.2},
        {"word": "of", "start": 5.25, "end": 5.35},
        {"word": "a", "start": 5.4, "end": 5.5},
        {"word": "bitch", "start": 5.55, "end": 5.9},
        {"word": "bastard", "start": 5.95, "end": 6.3},
    ]
    # If consecutive profanities occur close together with padding, they merge into one seamless cue
    profanities = {"bitch", "bastard"}
    cues = calculate_word_mute_cues(words, profanities, pad_start=0.1, pad_end=0.15)

    assert len(cues) == 1
    # bitch starts 5.55 - 0.1 = 5.45, ends 5.9 + 0.15 = 6.05
    # bastard starts 5.95 - 0.1 = 5.85 (<= 6.05 -> overlap!), ends 6.3 + 0.15 = 6.45
    assert pytest.approx(cues[0][0]) == 5.45
    assert pytest.approx(cues[0][1]) == 6.45
    assert "bitch" in cues[0][2] and "bastard" in cues[0][2]
