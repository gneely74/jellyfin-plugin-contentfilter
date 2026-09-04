#!/usr/bin/env python3
"""CLI utility and library to shift timestamps in SRT subtitle files by time offset in seconds.

Usage:
    python tools/shift_srt.py <file_or_dir> --offset <seconds> [--inplace] [--output <path>]

Examples:
    # Shift all subtitle cues later by +2.5 seconds in-place
    python tools/shift_srt.py movie.en.srt --offset 2.5 --inplace

    # Shift subtitle cues earlier by 1.2 seconds to a new file
    python tools/shift_srt.py movie.en.srt --offset -1.2 --output movie.en.shifted.srt

    # Shift all SRT files in a directory
    python tools/shift_srt.py /path/to/media/ --offset 0.8 --inplace
"""

from __future__ import annotations

import argparse
import os
import re
import sys
from pathlib import Path
from typing import List, Optional, Tuple

SRT_TIMECODE_RE = re.compile(
    r"^(?P<sh>\d{2}):(?P<sm>\d{2}):(?P<ss>\d{2})[.,](?P<sms>\d{3})\s+-->\s+(?P<eh>\d{2}):(?P<em>\d{2}):(?P<es>\d{2})[.,](?P<ems>\d{3})(?:[ \t].*)?$"
)


def parse_srt_timecode_to_ms(tc: str) -> int:
    """Convert HH:MM:SS,mmm or HH:MM:SS.mmm string to milliseconds."""
    m = SRT_TIMECODE_RE.match(tc.strip())
    if not m:
        # Fallback to general timecode match if single timecode passed
        m2 = re.match(r"^(?:(\d+):)?(\d{2}):(\d{2})[.,](\d{3})$", tc.strip())
        if not m2:
            raise ValueError(f"Invalid SRT timecode format: '{tc}'")
        h = int(m2.group(1) or 0)
        minutes = int(m2.group(2))
        s = int(m2.group(3))
        ms = int(m2.group(4))
        return ((h * 3600) + (minutes * 60) + s) * 1000 + ms

    sh = int(m.group("sh"))
    sm = int(m.group("sm"))
    ss = int(m.group("ss"))
    sms = int(m.group("sms"))
    return ((sh * 3600) + (sm * 60) + ss) * 1000 + sms


def format_ms_to_srt_timecode(ms: int) -> str:
    """Convert milliseconds to standard SRT timecode HH:MM:SS,mmm."""
    if ms < 0:
        ms = 0
    total_seconds, msec = divmod(ms, 1000)
    minutes, seconds = divmod(total_seconds, 60)
    hours, minutes = divmod(minutes, 60)
    return f"{hours:02d}:{minutes:02d}:{seconds:02d},{msec:03d}"


class SrtCue:
    def __init__(self, index: int, start_ms: int, end_ms: int, lines: List[str]):
        self.index = index
        self.start_ms = start_ms
        self.end_ms = end_ms
        self.lines = lines

    def shift(self, offset_ms: int) -> bool:
        """Shift start and end by offset_ms.

        Returns False if the cue ends at or before 0 (should be discarded).
        Clamps start_ms to 0 if it shifts negative.
        """
        new_start = self.start_ms + offset_ms
        new_end = self.end_ms + offset_ms

        if new_end <= 0:
            return False

        self.start_ms = max(0, new_start)
        self.end_ms = max(self.start_ms + 100, new_end)
        return True

    def serialize(self, new_index: int) -> str:
        tc = f"{format_ms_to_srt_timecode(self.start_ms)} --> {format_ms_to_srt_timecode(self.end_ms)}"
        content = "\n".join(self.lines)
        return f"{new_index}\n{tc}\n{content}"


def parse_srt_cues(content: str) -> List[SrtCue]:
    """Parse SRT formatted content into a list of SrtCue objects."""
    raw_blocks = re.split(r"\r?\n\r?\n+", content.strip())
    cues: List[SrtCue] = []

    for block in raw_blocks:
        lines = [line.rstrip("\r") for line in block.strip().split("\n") if line.strip()]
        if not lines:
            continue

        tc_idx = -1
        for idx, line in enumerate(lines):
            if "-->" in line and SRT_TIMECODE_RE.match(line.strip()):
                tc_idx = idx
                break

        if tc_idx == -1:
            continue

        tc_line = lines[tc_idx].strip()
        m = SRT_TIMECODE_RE.match(tc_line)
        if not m:
            continue

        sh, sm, ss, sms = int(m.group("sh")), int(m.group("sm")), int(m.group("ss")), int(m.group("sms"))
        eh, em, es, ems = int(m.group("eh")), int(m.group("em")), int(m.group("es")), int(m.group("ems"))

        start_ms = ((sh * 3600) + (sm * 60) + ss) * 1000 + sms
        end_ms = ((eh * 3600) + (em * 60) + es) * 1000 + ems

        cue_index = len(cues) + 1
        if tc_idx > 0 and lines[0].strip().isdigit():
            cue_index = int(lines[0].strip())

        dialogue_lines = lines[tc_idx + 1:]
        cues.append(SrtCue(index=cue_index, start_ms=start_ms, end_ms=end_ms, lines=dialogue_lines))

    return cues


def shift_srt(content: str, offset_seconds: float) -> Tuple[str, int, int]:
    """Shift timestamps in an SRT string by offset_seconds.

    Returns:
        (shifted_content, shifted_count, total_count)
    """
    if not content or not content.strip():
        return "", 0, 0

    cues = parse_srt_cues(content)
    total_count = len(cues)
    if total_count == 0:
        return content, 0, 0

    offset_ms = int(round(offset_seconds * 1000))
    surviving_cues: List[SrtCue] = []
    shifted_count = 0

    for cue in cues:
        if cue.shift(offset_ms):
            surviving_cues.append(cue)
            if offset_ms != 0:
                shifted_count += 1

    surviving_cues.sort(key=lambda c: c.start_ms)

    out_blocks: List[str] = []
    for new_idx, cue in enumerate(surviving_cues, start=1):
        out_blocks.append(cue.serialize(new_idx))

    return "\n\n".join(out_blocks).rstrip() + "\n", shifted_count, total_count


def process_srt_file(
    file_path: Path,
    offset_seconds: float,
    inplace: bool = False,
    output_path: Optional[Path] = None,
) -> bool:
    """Process a single SRT file."""
    try:
        content = file_path.read_text(encoding="utf-8", errors="replace")
    except Exception as ex:
        print(f"Error reading {file_path}: {ex}", file=sys.stderr)
        return False

    new_content, shifted, total = shift_srt(content, offset_seconds)
    sign = "+" if offset_seconds > 0 else ""
    print(f"{file_path.name}: Shifted {shifted}/{total} cues by {sign}{offset_seconds}s")

    if output_path:
        output_path.write_text(new_content, encoding="utf-8")
        print(f"  -> Written to {output_path}")
    elif inplace:
        file_path.write_text(new_content, encoding="utf-8")
        print("  -> Updated in-place")

    return True


def main() -> int:
    parser = argparse.ArgumentParser(description="Shift subtitle timestamps in SRT files by time offset.")
    parser.add_argument("target", help="Path to an .srt file or directory containing .srt files")
    parser.add_argument("--offset", type=float, required=True, help="Offset in seconds (positive or negative, e.g. +2.5 or -1.0)")
    parser.add_argument("--inplace", action="store_true", help="Modify file(s) in-place")
    parser.add_argument("--output", help="Output file path (single file mode only)")

    args = parser.parse_args()
    target = Path(args.target)

    if not target.exists():
        print(f"Target not found: {target}", file=sys.stderr)
        return 1

    if target.is_file():
        out = Path(args.output) if args.output else None
        ok = process_srt_file(target, args.offset, inplace=args.inplace, output_path=out)
        return 0 if ok else 1

    if target.is_dir():
        srt_files = sorted(target.rglob("*.srt"))
        if not srt_files:
            print(f"No .srt files found in {target}")
            return 0
        success = True
        for f in srt_files:
            if not process_srt_file(f, args.offset, inplace=args.inplace):
                success = False
        return 0 if success else 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
