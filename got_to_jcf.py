#!/usr/bin/env python3
"""Convert GoT timestamp ranges into JCF sidecar files for the Jellyfin Content Filter plugin.

The input ranges in EPISODES represent safe (clean) playback segments. This script
inverts them to calculate the gaps between safe segments (i.e. the objectionable scenes)
and outputs JCF skip cues for those flagged scenes. When a user enables the filter in Jellyfin,
playback skips over the objectionable scenes and plays the safe portions.
"""

import os
import re
import sys


EPISODES = {
    # Season 1
    "S01E01": "00:00:00-00:30:30,+00:31:15-00:31:30,+00:31:55-00:32:10,+00:32:25-00:34:20,+00:35:40-00:39:10,+00:39:20-00:50:50,+00:51:00-00:51:15,+00:51:40-00:52:10,+00:52:15-00:54:10,+00:54:30-00:56:55,+00:57:20-00:59:25,+00:59:50-01:01:42",
    "S01E02": "",
    "S01E03": "00:00:00-00:19:40,+00:20:00-00:50:50,+00:51:40-00:57:18",
    "S01E04": "",
    "S01E05": "",
    "S01E06": "",
    "S01E07": "",
    "S01E08": "",
    "S01E09": "00:00:00-00:26:00,+00:26:10-00:26:40,+00:27:10-00:42:30,+00:42:40-00:56:19",
    "S01E10": "00:00:00-00:18:50,+00:19:00-00:35:05,+00:35:25-00:35:35,+00:35:50-00:50:15,+00:50:25-00:50:40,+00:51:05-00:51:15,+00:51:25-00:52:40",
    # Season 2
    "S02E01": "",
    "S02E02": "",
    "S02E03": "",
    "S02E04": "00:00:00-00:11:40,+00:14:20-00:14:30,+00:14:40-00:47:50,+00:48:50-00:49:00,+00:49:20-00:50:41",
    "S02E05": "",
    "S02E06": "",
    "S02E07": "",
    "S02E08": "00:00:00-00:39:50,+00:40:55-00:53:21",
    "S02E09": "00:00:00-00:09:10,+00:09:40-00:09:45,+00:11:20-00:11:25,+00:11:35-00:11:43,+00:11:50-00:54:27",
    "S02E10": "00:00:00-00:09:45,+00:09:51-00:09:53,+00:09:58-01:03:26",
    # Season 3
    "S03E01": "",
    "S03E02": "",
    "S03E03": "00:00:00-00:37:22,+00:37:32-00:37:43,+00:37:52-00:38:16,+00:39:16-00:39:19,+00:39:22-00:44:25,+00:44:35-00:52:49",
    "S03E04": "",
    "S03E05": "00:00:00-00:08:40,+00:08:57-00:09:17,+00:10:05-00:10:50,+00:11:23-00:34:25,+00:34:37-00:35:06,+00:35:13-00:35:35,+00:35:39-00:49:56,+00:50:29-00:57:25",
    "S03E06": "",
    "S03E07": "00:00:00-00:05:27,+00:06:55-00:07:03,+00:07:13-00:07:21,+00:07:54-00:09:15,+00:09:23-00:09:29,+00:09:45-00:36:20,+00:36:49-00:37:16,+00:38:57-00:39:08,+00:39:49-00:39:58,+00:40:05-00:57:37",
    "S03E08": "00:00:00-00:15:57,+00:16:09-00:16:17,+00:16:43-00:16:59,+00:17:54-00:28:00,+00:30:37-00:30:45,+00:30:48-00:30:57,+00:31:20-00:44:46,+00:44:49-00:44:59,+00:45:06-00:45:18,+00:45:23-00:45:36,+00:45:38-00:46:02,+00:46:11-00:46:16,+00:46:20-00:46:24,+00:46:29-00:56:21",
    "S03E09": "",
    "S03E10": "",
    # Season 4
    "S04E01": "00:00:00-00:10:00,+00:10:12-00:10:24,+00:10:27-00:10:29,+00:11:13-00:11:41,+00:11:57-00:12:01,+00:12:04-00:12:07,+00:12:13-00:24:04,+00:24:22-00:58:13",
    "S04E02": "",
    "S04E03": "",
    "S04E04": "00:00:00-00:40:28,+00:40:32-00:40:36,+00:41:16-00:55:04",
    "S04E05": "",
    "S04E06": "00:00:00-00:07:49,+00:08:04-00:08:07,+00:08:16-00:08:21,+00:08:26-00:08:28,+00:08:37-00:08:43,+00:08:57-00:09:01,+00:09:15-00:10:41,+00:10:47-00:10:53,+00:11:06-00:11:18,+00:11:23-00:11:29,+00:11:35-00:50:41",
    "S04E07": "00:00:00-00:20:22,+00:20:25-00:20:28,+00:20:43-00:20:45,+00:20:49-00:20:50,+00:20:53-00:20:54,+00:20:58-00:21:17,+00:21:25-00:21:35,+00:21:55-00:22:07,+00:22:11-00:22:18,+00:22:22-00:22:45,+00:22:54-00:22:58,+00:23:02-00:23:04,+00:23:08-00:23:17,+00:23:26-00:51:05",
    "S04E08": "00:00:00-00:02:27,+00:02:32-00:02:51,+00:03:08-00:07:58,+00:08:00-00:08:02,+00:08:08-00:08:10,+00:08:15-00:08:17,+00:08:25-00:08:26,+00:08:29-00:08:32,+00:08:37-00:52:22",
    "S04E09": "",
    "S04E10": "00:00:00-00:20:00,+00:20:07-01:05:19",
    # Season 5
    "S05E01": "00:00:00-00:13:47,+00:14:09-00:14:38,+00:15:38-00:29:43,+00:30:43-00:30:45,+00:30:58-00:31:06,+00:31:12-00:31:18,+00:31:23-00:31:28,+00:31:33-00:37:12,+00:37:46-00:52:11",
    "S05E02": "",
    "S05E03": "00:00:00-00:40:45,+00:41:06-00:41:10,+00:41:19-00:41:22,+00:41:30-00:41:32,+00:41:37-00:41:54,+00:41:59-00:42:09,+00:42:14-00:55:24,+00:55:31-00:55:37,+00:55:48-00:56:03,+00:56:06-00:56:38,+00:56:42-00:59:56",
    "S05E04": "",
    "S05E05": "00:00:00-00:18:41,+00:21:04-00:56:42",
    "S05E06": "00:00:00-00:51:42,+00:51:47-00:53:42",
    "S05E07": "00:00:00-00:24:03,+00:24:16-00:24:30,+00:24:52-00:28:14,+00:29:40-00:39:28,+00:39:38-00:39:45,+00:39:51-00:39:52,+00:39:55-00:39:57,+00:40:10-00:40:15,+00:40:19-00:40:23,+00:40:27-00:40:44,+00:40:49-00:41:06,+00:41:10-00:41:28,+00:41:30-00:41:38,+00:41:51-00:58:51",
    "S05E08": "",
    "S05E09": "",
    "S05E10": "",
    # Season 6
    "S06E01": "",
    "S06E02": "",
    "S06E03": "",
    "S06E04": "",
    "S06E05": "",
    "S06E06": "",
    "S06E07": "00:00:00-00:34:24,+00:34:28-00:34:30,+00:34:35-00:34:37,+00:34:45-00:34:48,+00:35:13-00:35:15,+00:35:25-00:35:35,+00:35:49-00:35:51,+00:35:53-00:36:09,+00:36:17-00:36:31,+00:36:36-00:37:33,+00:37:38-00:37:39,+00:37:41-00:50:20",
    "S06E08": "",
    "S06E09": "",
    "S06E10": "",
    # Season 7
    "S07E01": "",
    "S07E02": "",
    "S07E03": "",
    "S07E04": "",
    "S07E05": "",
    "S07E06": "",
    "S07E07": "00:00:00-01:09:49,+01:10:10-01:19:50",
}


def fmt_ts(ts: str) -> str:
    """Convert HH:MM:SS or H:MM:SS to JCF timestamp HH:MM:SS.000."""
    m = re.match(r"^(\d+):(\d{2}):(\d{2})$", ts.strip())
    if not m:
        raise ValueError(f"Invalid timestamp: {ts!r}")
    h, mn, s = m.groups()
    return f"{int(h):02}:{mn}:{s}.000"


def parse_range(r: str) -> tuple[str, str]:
    parts = r.split("-", 1)
    if len(parts) != 2:
        raise ValueError(f"Invalid range: {r!r}")
    return fmt_ts(parts[0]), fmt_ts(parts[1])


def build_jcf(
    ep_id: str,
    ranges_str: str,
    title: str,
    year: str,
    category: str = "SexAndNudity.FullNudity",
    action: str = "skip",
    channel: str = "video",
) -> str:
    if not ranges_str.strip():
        return ""
    # Ranges may be separated by either ',' or '+' — normalize.
    normalized = ranges_str.replace(",", "+")
    ranges = [r.strip() for r in normalized.split("+") if r.strip()]
    # Parse the flagged (input) ranges first.
    flagged = [parse_range(r) for r in ranges]
    # Invert: emit the *gaps* between flagged ranges as the skip cues.
    skip_ranges = invert_ranges(flagged)
    lines = [
        "WEBVTT JCF",
        "",
        "NOTE",
        f"TITLE {title}",
        f"YEAR {year}",
        "SOURCE Reddit r/naath & r/gameofthrones (inverted safe ranges)",
        "",
    ]
    for start, end in skip_ranges:
        lines.extend(
            [
                f"{start} --> {end}",
                f"category: {category}",
                f"channel: {channel}",
                f"action: {action}",
                "description: Objectionable scene",
                "",
            ]
        )
    return "\n".join(lines).rstrip() + "\n"


def invert_ranges(safe_ranges: list[tuple[str, str]]) -> list[tuple[str, str]]:
    """Return the gaps between consecutive safe ranges.

    The gaps correspond to the objectionable scenes that should be skipped.
    """
    if not safe_ranges:
        return []

    # Sort by start time
    sorted_ranges = sorted(safe_ranges, key=lambda r: r[0])

    # Merge overlapping or touching ranges
    merged: list[tuple[str, str]] = []
    for start, end in sorted_ranges:
        if not merged:
            merged.append((start, end))
        else:
            prev_start, prev_end = merged[-1]
            if start <= prev_end:
                merged[-1] = (prev_start, max(prev_end, end))
            else:
                merged.append((start, end))

    gaps: list[tuple[str, str]] = []
    for (prev_start, prev_end), (cur_start, _) in zip(merged, merged[1:]):
        if cur_start > prev_end:
            gaps.append((prev_end, cur_start))
    return gaps


def main() -> None:
    out_dir = sys.argv[1] if len(sys.argv) > 1 else "jcf_output"
    os.makedirs(out_dir, exist_ok=True)
    written = 0
    skipped = 0
    for ep_id, ranges in EPISODES.items():
        if not ranges.strip():
            skipped += 1
            continue
        content = build_jcf(ep_id, ranges, f"Game of Thrones {ep_id}", "2011")
        path = os.path.join(out_dir, f"{ep_id}.jcf")
        with open(path, "w", encoding="utf-8") as f:
            f.write(content)
        written += 1
        print(f"Wrote {path}")
    print(f"\nTotal: {written} files written, {skipped} empty episodes skipped")


if __name__ == "__main__":
    main()
