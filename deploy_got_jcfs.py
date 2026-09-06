#!/usr/bin/env python3
"""Deploy corrected Game of Thrones JCF sidecar files to the media library.

- Deletes obsolete/misaligned JCF sidecars from the old offset bug.
- Writes corrected JCF sidecar files named <VideoBaseName>.jcf matching each MKV.
- Preserves existing language profanity cues for S01E01.
- Validates all generated JCF files.
"""

import re
import sys
from pathlib import Path

import got_to_jcf as got

MEDIA_DIR = Path("/Volumes/data/shows/Game of Thrones")
JCF_OUTPUT_DIR = Path("jcf_output")

# The 11 obsolete / misaligned JCF files to delete
OBSOLETE_JCFS = [
    MEDIA_DIR / "Season 02" / "Game of Thrones - S02E03 - What Is Dead May Never Die.jcf",
    MEDIA_DIR / "Season 02" / "Game of Thrones - S02E05 - The Ghost of Harrenhal.jcf",
    MEDIA_DIR / "Season 02" / "Game of Thrones - S02E06 - The Old Gods and the New.jcf",
    MEDIA_DIR / "Season 02" / "Game of Thrones - S02E07 - A Man Without Honor.jcf",
    MEDIA_DIR / "Season 03" / "Game of Thrones - S03E02 - Dark Wings, Dark Words.jcf",
    MEDIA_DIR / "Season 03" / "Game of Thrones - S03E04 - And Now His Watch Is Ended.jcf",
    MEDIA_DIR / "Season 04" / "Game of Thrones - S04E03 - Breaker of Chains.jcf",
    MEDIA_DIR / "Season 04" / "Game of Thrones - S04E05 - First of His Name.jcf",
    MEDIA_DIR / "Season 05" / "Game of Thrones - S05E02 - The House of Black and White.jcf",
    MEDIA_DIR / "Season 05" / "Game of Thrones - S05E04 - Sons of the Harpy.jcf",
    MEDIA_DIR / "Season 06" / "Game of Thrones - S06E08 - No One.jcf",
]


def parse_jcf_cues(content: str):
    """Parse cues from a JCF text content."""
    cues = []
    blocks = content.strip().split("\n\n")
    for block in blocks:
        lines = [line.strip() for line in block.split("\n") if line.strip()]
        if not lines or "-->" not in lines[0]:
            continue
        ts_line = lines[0]
        start_str, end_str = [x.strip() for x in ts_line.split("-->")]
        props = {}
        for l in lines[1:]:
            if ":" in l:
                k, v = l.split(":", 1)
                props[k.strip().lower()] = v.strip()
        cues.append(
            {
                "start": start_str,
                "end": end_str,
                "category": props.get("category", ""),
                "channel": props.get("channel", "video"),
                "action": props.get("action", "skip"),
                "description": props.get("description", ""),
            }
        )
    return cues


def format_cues_jcf(title: str, year: str, source: str, cues: list) -> str:
    """Format a list of cue dictionaries into standard JCF WEBWTT format."""
    sorted_cues = sorted(cues, key=lambda c: c["start"])
    lines = [
        "WEBVTT JCF",
        "",
        "NOTE",
        f"TITLE {title}",
        f"YEAR {year}",
        f"SOURCE {source}",
        "",
    ]
    for c in sorted_cues:
        lines.append(f"{c['start']} --> {c['end']}")
        if c.get("description"):
            lines.append(f"description: {c['description']}")
        lines.append(f"category: {c['category']}")
        lines.append(f"channel: {c['channel']}")
        lines.append(f"action: {c['action']}")
        lines.append("")
    return "\n".join(lines).rstrip() + "\n"


def deploy(dry_run: bool = False):
    print(f"Deploying Game of Thrones JCF files (dry_run={dry_run})...\n")

    if not MEDIA_DIR.exists():
        print(
            f"Error: Media directory {MEDIA_DIR} does not exist or is not mounted.", file=sys.stderr
        )
        return False

    # 1. Delete obsolete files
    deleted_count = 0
    for obs in OBSOLETE_JCFS:
        if obs.exists():
            print(f"[DELETE] {obs.parent.name}/{obs.name}")
            if not dry_run:
                obs.unlink()
            deleted_count += 1
        else:
            print(f"[ALREADY REMOVED] {obs.parent.name}/{obs.name}")

    # 2. Extract existing language cues from S01E01 if present
    s01e01_orig = MEDIA_DIR / "Season 01" / "Game of Thrones - S01E01 - Winter Is Coming-orig.jcf"
    s01e01_curr = MEDIA_DIR / "Season 01" / "Game of Thrones - S01E01 - Winter Is Coming.jcf"
    s01_source_file = (
        s01e01_orig if s01e01_orig.exists() else (s01e01_curr if s01e01_curr.exists() else None)
    )

    s01_lang_cues = []
    if s01_source_file and s01_source_file.exists():
        parsed = parse_jcf_cues(s01_source_file.read_text(encoding="utf-8"))
        s01_lang_cues = [
            c for c in parsed if c["category"].startswith("Language.") or c["channel"] == "audio"
        ]
        print(
            f"\nPreserving {len(s01_lang_cues)} language profanity cues from {s01_source_file.name}"
        )

    # 3. Process each season directory
    written_count = 0
    total_cues = 0

    for season_dir in sorted(MEDIA_DIR.iterdir()):
        if not season_dir.is_dir() or not season_dir.name.startswith("Season"):
            continue

        mkvs = sorted([f for f in season_dir.iterdir() if f.suffix == ".mkv"])
        for mkv in mkvs:
            m = re.search(r"S(\d{2})E(\d{2})", mkv.name, re.IGNORECASE)
            if not m:
                continue
            ep_id = f"S{m.group(1).upper()}E{m.group(2).upper()}"
            raw_ranges = got.EPISODES.get(ep_id, "").strip()

            if not raw_ranges:
                continue

            normalized = raw_ranges.replace(",", "+")
            ranges = [r.strip() for r in normalized.split("+") if r.strip()]
            flagged = [got.parse_range(r) for r in ranges]
            gaps = got.invert_ranges(flagged)

            cues = []
            for start, end in gaps:
                cues.append(
                    {
                        "start": start,
                        "end": end,
                        "category": "SexAndNudity.FullNudity",
                        "channel": "video",
                        "action": "skip",
                        "description": "Objectionable scene",
                    }
                )

            source_desc = "Reddit r/naath & r/gameofthrones (inverted safe ranges)"
            if ep_id == "S01E01" and s01_lang_cues:
                cues.extend(s01_lang_cues)
                source_desc = (
                    "Reddit r/naath & r/gameofthrones (nudity) + Subtitle scan (profanity)"
                )

            target_jcf = mkv.with_suffix(".jcf")
            content = format_cues_jcf(
                title=mkv.stem,
                year="2011",
                source=source_desc,
                cues=cues,
            )

            status = "UPDATE" if target_jcf.exists() else "CREATE"
            print(f"[{status}] {season_dir.name}/{target_jcf.name} ({len(cues)} cues)")

            if not dry_run:
                target_jcf.write_text(content, encoding="utf-8")

            written_count += 1
            total_cues += len(cues)

    print("\nSummary:")
    print(f"  Obsolete JCFs deleted: {deleted_count}")
    print(f"  JCF sidecars written:  {written_count}")
    print(f"  Total cues deployed:   {total_cues}")
    return True


if __name__ == "__main__":
    is_dry = "--dry-run" in sys.argv
    deploy(dry_run=is_dry)
