#!/usr/bin/env python3
"""CLI utility and library to shift cue timestamps in JCF files by time offset and channel.

Usage:
    python tools/shift_cues.py <file_or_dir> --offset <seconds> [--channel all|video|audio] [--inplace] [--output <path>]

Examples:
    # Shift all cues by +2.5 seconds in-place
    python tools/shift_cues.py episode.jcf --offset 2.5 --inplace

    # Shift only video cues earlier by 1.2 seconds
    python tools/shift_cues.py episode.jcf --offset -1.2 --channel video --inplace

    # Shift only audio cues in all JCF files in a folder
    python tools/shift_cues.py /path/to/season/ --offset 0.8 --channel audio --inplace
"""

from __future__ import annotations

import argparse
import os
import re
import sys
from pathlib import Path
from typing import List, Optional, Tuple

TIMECODE_RE = re.compile(
    r"^(?:(?P<sh>\d+):)?(?P<sm>\d{2}):(?P<ss>\d{2})[.,](?P<sms>\d{3})\s+-->\s+(?:(?P<eh>\d+):)?(?P<em>\d{2}):(?P<es>\d{2})[.,](?P<ems>\d{3})(?:[ \t].*)?$"
)


def parse_timecode_to_ms(tc: str) -> int:
    """Convert HH:MM:SS.mmm or MM:SS.mmm string to milliseconds."""
    m = re.match(r"^(?:(\d+):)?(\d{2}):(\d{2})[.,](\d{3})$", tc.strip())
    if not m:
        raise ValueError(f"Invalid timecode format: '{tc}'")
    h = int(m.group(1) or 0)
    minutes = int(m.group(2))
    s = int(m.group(3))
    ms = int(m.group(4))
    return ((h * 3600) + (minutes * 60) + s) * 1000 + ms


def format_ms_to_timecode(ms: int) -> str:
    """Convert milliseconds to HH:MM:SS.mmm string."""
    if ms < 0:
        ms = 0
    total_seconds, msec = divmod(ms, 1000)
    minutes, seconds = divmod(total_seconds, 60)
    hours, minutes = divmod(minutes, 60)
    return f"{hours:02d}:{minutes:02d}:{seconds:02d}.{msec:03d}"


class JcfCue:
    def __init__(
        self,
        start_ms: int,
        end_ms: int,
        category: str = "",
        channel: str = "both",
        action: str = "none",
        description: Optional[str] = None,
        extra_lines: Optional[List[str]] = None,
    ):
        self.start_ms = start_ms
        self.end_ms = end_ms
        self.category = category
        self.channel = channel
        self.action = action
        self.description = description
        self.extra_lines = extra_lines or []

    def matches_channel(self, target: str) -> bool:
        """Check if this cue matches the target channel filter ('all', 'video', 'audio')."""
        if not target or target.lower() == "all":
            return True

        ch = (self.channel or "").strip().lower()
        act = (self.action or "").strip().lower()
        target = target.strip().lower()

        if target == "video":
            return ch == "video" or (ch == "both" and act != "mute") or act == "skip"
        if target == "audio":
            return ch == "audio" or (ch == "both" and act == "mute") or act == "mute"
        return ch == target

    def shift(self, offset_ms: int) -> None:
        """Shift start and end by offset_ms, ensuring non-negative start and valid duration."""
        new_start = max(0, self.start_ms + offset_ms)
        duration = max(500, self.end_ms - self.start_ms)
        new_end = max(new_start + 500, self.end_ms + offset_ms)
        self.start_ms = new_start
        self.end_ms = new_end

    def serialize(self) -> str:
        lines = [f"{format_ms_to_timecode(self.start_ms)} --> {format_ms_to_timecode(self.end_ms)}"]
        if self.category:
            lines.append(f"category: {self.category}")
        if self.channel:
            lines.append(f"channel: {self.channel}")
        if self.action:
            lines.append(f"action: {self.action}")
        if self.description:
            lines.append(f"description: {self.description}")
        for extra in self.extra_lines:
            lines.append(extra)
        return "\n".join(lines)


def parse_jcf_cues(content: str) -> Tuple[List[str], List[JcfCue]]:
    """Parse JCF content into header/note lines and a list of JcfCue objects."""
    lines = content.splitlines()
    header_lines: List[str] = []
    cues: List[JcfCue] = []

    i = 0
    in_header = True

    while i < len(lines):
        line = lines[i]
        stripped = line.strip()

        # Check for timecode line
        tc_match = TIMECODE_RE.match(stripped)
        if tc_match:
            in_header = False
            sh = int(tc_match.group("sh") or 0)
            sm = int(tc_match.group("sm"))
            ss = int(tc_match.group("ss"))
            sms = int(tc_match.group("sms"))
            start_ms = ((sh * 3600) + (sm * 60) + ss) * 1000 + sms

            eh = int(tc_match.group("eh") or 0)
            em = int(tc_match.group("em"))
            es = int(tc_match.group("es"))
            ems = int(tc_match.group("ems"))
            end_ms = ((eh * 3600) + (em * 60) + es) * 1000 + ems

            category = ""
            channel = "both"
            action = "none"
            description = None
            extra_lines: List[str] = []

            i += 1
            while i < len(lines) and lines[i].strip():
                payload_line = lines[i].strip()
                if ":" in payload_line:
                    k, v = payload_line.split(":", 1)
                    k = k.strip().lower()
                    v = v.strip()
                    if k == "category":
                        category = v
                    elif k == "channel":
                        channel = v
                    elif k == "action":
                        action = v
                    elif k == "description":
                        description = v
                    else:
                        extra_lines.append(payload_line)
                elif "=" in payload_line:
                    # Legacy MCF format: category=action=channel
                    parts = [p.strip() for p in payload_line.split("=")]
                    if len(parts) > 0:
                        category = parts[0]
                    if len(parts) > 1:
                        action = parts[1]
                    if len(parts) > 2:
                        channel = parts[2]
                else:
                    extra_lines.append(payload_line)
                i += 1

            cues.append(
                JcfCue(
                    start_ms=start_ms,
                    end_ms=end_ms,
                    category=category,
                    channel=channel,
                    action=action,
                    description=description,
                    extra_lines=extra_lines,
                )
            )
        else:
            if in_header:
                header_lines.append(line)
            i += 1

    return header_lines, cues


def shift_jcf(content: str, offset_seconds: float, channel: str = "all") -> Tuple[str, int, int]:
    """Shift cues in JCF string content by offset_seconds for the specified channel.

    Returns:
        (updated_content, shifted_count, total_count)
    """
    header_lines, cues = parse_jcf_cues(content)
    offset_ms = int(round(offset_seconds * 1000))
    shifted_count = 0

    for cue in cues:
        if cue.matches_channel(channel):
            cue.shift(offset_ms)
            shifted_count += 1

    # Sort cues by start time
    cues.sort(key=lambda c: c.start_ms)

    out_lines: List[str] = []
    # Clean up trailing blank lines from header
    while header_lines and not header_lines[-1].strip():
        header_lines.pop()

    out_lines.extend(header_lines)
    out_lines.append("")

    for cue in cues:
        out_lines.append(cue.serialize())
        out_lines.append("")

    return "\n".join(out_lines).rstrip() + "\n", shifted_count, len(cues)


def process_file(
    file_path: Path,
    offset_seconds: float,
    channel: str = "all",
    inplace: bool = False,
    output_path: Optional[Path] = None,
) -> bool:
    """Process a single JCF file."""
    try:
        content = file_path.read_text(encoding="utf-8")
    except Exception as ex:
        print(f"Error reading {file_path}: {ex}", file=sys.stderr)
        return False

    new_content, shifted, total = shift_jcf(content, offset_seconds, channel)
    sign = "+" if offset_seconds > 0 else ""
    print(f"{file_path.name}: Shifted {shifted}/{total} cues by {sign}{offset_seconds}s (channel={channel})")

    if output_path:
        output_path.write_text(new_content, encoding="utf-8")
        print(f"  -> Written to {output_path}")
    elif inplace:
        file_path.write_text(new_content, encoding="utf-8")
        print("  -> Updated in-place")

    return True


def main() -> int:
    parser = argparse.ArgumentParser(description="Shift cue points in JCF files by time offset and channel filter.")
    parser.add_argument("target", help="Path to a .jcf file or directory containing .jcf files")
    parser.add_argument("--offset", type=float, required=True, help="Offset in seconds (positive or negative, e.g. +2.5 or -1.0)")
    parser.add_argument(
        "--channel",
        choices=["all", "video", "audio"],
        default="all",
        help="Target channel: 'all' (default), 'video' (visual skips), or 'audio' (mutes/dialogue)",
    )
    parser.add_argument("--inplace", "-i", action="store_true", help="Modify files in-place")
    parser.add_argument("--output", "-o", type=Path, default=None, help="Output file path (only valid when target is a single file)")

    args = parser.parse_args()
    target_path = Path(args.target)

    if not target_path.exists():
        print(f"Error: Target '{target_path}' does not exist.", file=sys.stderr)
        return 1

    if target_path.is_file():
        success = process_file(target_path, args.offset, args.channel, args.inplace, args.output)
        return 0 if success else 1

    if target_path.is_dir():
        if args.output:
            print("Error: --output cannot be used when targeting a directory.", file=sys.stderr)
            return 1

        jcf_files = list(target_path.glob("**/*.jcf"))
        if not jcf_files:
            print(f"No .jcf files found in {target_path}.")
            return 0

        print(f"Found {len(jcf_files)} .jcf files in {target_path}")
        total_shifted = 0
        for f in jcf_files:
            if process_file(f, args.offset, args.channel, args.inplace):
                total_shifted += 1

        print(f"Completed processing {total_shifted} files.")
        return 0

    return 1


if __name__ == "__main__":
    sys.exit(main())
