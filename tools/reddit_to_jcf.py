#!/usr/bin/env python3
"""Timecode parser and JCF (Jellyfin Content Filter) generator.

Parses timecode strings and ranges from Reddit posts, comments, and community
dumps, maps content categories to Jellyfin ContentFilter categories, and formats
valid WEBVTT JCF files.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import List, Optional, Tuple


# Regex to match timestamps like M:SS, MM:SS, H:MM:SS, HH:MM:SS (optional milliseconds)
TIMESTAMP_TOKEN_REGEX = re.compile(
    r"\b(?:(?P<h>\d{1,2}):)?(?P<m>\d{1,2}):(?P<s>\d{2})(?:[.,](?P<ms>\d{1,3}))?\b"
)

# Regex to match timestamp ranges separated by -, to, –, —, etc.
TIMESTAMP_RANGE_REGEX = re.compile(
    r"(?P<start>(?:\d{1,2}:)?\d{1,2}:\d{2}(?:[.,]\d{1,3})?)\s*(?:-|to|–|—|\.\.\.)\s*(?P<end>(?:\d{1,2}:)?\d{1,2}:\d{2}(?:[.,]\d{1,3})?)",
    re.IGNORECASE,
)

# Regex to match single standalone timestamps like "at 1:02:15" or "(12:34)"
SINGLE_TIMESTAMP_REGEX = re.compile(
    r"(?:^|[\s(])(?:at\s+)?(?P<ts>(?:\d{1,2}:)?\d{1,2}:\d{2}(?:[.,]\d{1,3})?)\b(?!\s*(?:-|to|–|—|\.\.\.))",
    re.IGNORECASE,
)


@dataclass
class ParsedCue:
    start_ms: int
    end_ms: int
    start_str: str
    end_str: str
    category: str
    channel: str = "video"
    action: str = "skip"
    description: Optional[str] = None


@dataclass
class JcfDocument:
    title: str
    year: Optional[str] = None
    imdb_id: Optional[str] = None
    source: Optional[str] = None
    cues: List[ParsedCue] = field(default_factory=list)

    def to_jcf(self) -> str:
        """Serialize into valid WEBVTT JCF format."""
        lines = ["WEBVTT JCF", "", "NOTE", f"TITLE {self.title}"]
        if self.year:
            lines.append(f"YEAR {self.year}")
        if self.imdb_id:
            lines.append(f"IMDB {self.imdb_id}")
        if self.source:
            lines.append(f"SOURCE {self.source}")
        lines.append("")

        # Sort cues by start time
        sorted_cues = sorted(self.cues, key=lambda c: c.start_ms)
        for cue in sorted_cues:
            lines.append(f"{cue.start_str} --> {cue.end_str}")
            lines.append(f"category: {cue.category}")
            lines.append(f"channel: {cue.channel}")
            lines.append(f"action: {cue.action}")
            if cue.description:
                # Sanitize description to single line
                clean_desc = cue.description.replace("\n", " ").strip()
                lines.append(f"description: {clean_desc}")
            lines.append("")

        return "\n".join(lines).rstrip() + "\n"


def ms_to_timestamp(ms: int) -> str:
    """Convert millisecond integer to HH:MM:SS.mmm string."""
    if ms < 0:
        ms = 0
    total_seconds = ms // 1000
    milli = ms % 1000
    hours = total_seconds // 3600
    minutes = (total_seconds % 3600) // 60
    seconds = total_seconds % 60
    return f"{hours:02d}:{minutes:02d}:{seconds:02d}.{milli:03d}"


def parse_timestamp_to_ms(ts_str: str) -> int:
    """Parse time string (e.g. 1:23, 01:23, 1:23:45, 01:23:45.500) into milliseconds."""
    ts_str = ts_str.strip()
    m = re.match(
        r"^(?:(?P<h>\d{1,2}):)?(?P<m>\d{1,2}):(?P<s>\d{2})(?:[.,](?P<ms>\d{1,3}))?$",
        ts_str,
    )
    if not m:
        raise ValueError(f"Invalid timestamp: '{ts_str}'")

    h = int(m.group("h")) if m.group("h") is not None else 0
    minute = int(m.group("m"))
    s = int(m.group("s"))
    ms_str = m.group("ms")
    if ms_str:
        ms = int(ms_str.ljust(3, "0")[:3])
    else:
        ms = 0

    return (h * 3600 + minute * 60 + s) * 1000 + ms


def format_timestamp_string(ts_str: str) -> Tuple[int, str]:
    """Return (ms, HH:MM:SS.mmm) for any valid timestamp string."""
    ms = parse_timestamp_to_ms(ts_str)
    return ms, ms_to_timestamp(ms)


def clean_line_typos(line: str) -> str:
    """Fix common spacing or typographical glitches in Reddit timestamp lines."""
    # Fix "1:18: 21" -> "1:18:21"
    line = re.sub(r"(\d+:\d+:)\s+(\d+)", r"\1\2", line)
    # Fix "1:35 28" -> "1:35:28" when preceded by delimiter
    line = re.sub(r"([–—\-to]\s*\d+:\d{2})\s+(\d{2})\b", r"\1:\2", line)
    # Fix "1:019:14" -> "1:09:14"
    line = re.sub(r"\b(\d+):01(\d):", r"\1:0\2:", line)
    return line


def map_category_and_channel(
    category_label: str, text_context: str = ""
) -> Tuple[str, str, str]:
    """Map arbitrary Reddit/community category labels to plugin standard category, channel, and action."""
    combined = f"{category_label} {text_context}".lower()

    # 1. Profanity / Language
    if any(k in combined for k in ["profanity", "language", "swear", "curse", "f-word", "slur"]):
        if any(k in combined for k in ["slur", "racial"]):
            return "Language.RacialAndBigotedSlurs", "audio", "mute"
        if any(k in combined for k in ["god", "jesus", "blasphem"]):
            return "Language.Blasphemy", "audio", "mute"
        return "Language.GeneralProfanity", "audio", "mute"

    # 2. Gestures
    if any(k in combined for k in ["middle finger", "flipping off", "vulgar gesture"]):
        return "SexualReferences.Visuals", "video", "skip"

    # 3. Dialogue / Remarks
    if any(k in combined for k in ["dialogue", "convo", "conversation", "sexual remark", "sexual comment"]):
        return "SexualReferences.ContextualDialogue", "video", "skip"

    # 4. Sex & Onscreen Activity
    if re.search(r"\bsex(?:ual)?\b", combined) or any(
        k in combined
        for k in [
            "intercourse",
            "masturbat",
            "orgasm",
            "blowjob",
            "handjob",
            "oral sex",
            "intimate",
            "sensual",
            "make out",
            "fooling in bed",
            "kissing",
        ]
    ):
        if "kissing" in combined and not re.search(r"\bsex\b", combined):
            return "SexAndNudity.PhysicalIntimacy", "video", "skip"
        return "SexAndNudity.OnscreenActivity", "video", "skip"

    # 5. Nudity Profiles (very common in Reddit posts)
    if re.search(r"\bnud(?:e|ity)\b", combined) or any(
        k in combined
        for k in [
            "naked",
            "topless",
            "bare butt",
            "bare backside",
            "buttocks",
            "boobs",
            "breasts",
            "cleavage",
            "bra",
            "underwear",
            "boxers",
            "panties",
            "no shirt",
            "shirtless",
            "shower",
            "revealing clothing",
            "immodest",
        ]
    ):
        return "SexAndNudity.NudityProfiles", "video", "skip"

    # 6. Violence & Horror & Disturbing
    if any(
        k in combined
        for k in [
            "violence",
            "gore",
            "bloody",
            "blood",
            "kill",
            "murder",
            "behead",
            "stab",
            "shot",
            "shooting",
            "gunfight",
            "fight",
            "scary",
            "disturbing",
            "fear",
            "jumpscare",
            "jump scare",
            "suicide",
            "torture",
            "corpse",
            "decapitat",
        ]
    ):
        return "Violence.Tiers", "video", "skip"

    # 7. Substance Use
    if any(
        k in combined
        for k in [
            "drugs",
            "drug",
            "alcohol",
            "smoking",
            "smoke",
            "drink",
            "drunk",
            "beer",
            "wine",
            "cocaine",
            "weed",
            "marijuana",
            "heroin",
            "meth",
            "mescaline",
            "pills",
            "substance",
        ]
    ):
        return "Substances.Usage", "video", "skip"

    # 8. Medical & Biological
    if any(
        k in combined
        for k in [
            "medical",
            "hospital",
            "surgery",
            "procedure",
            "needle",
            "puke",
            "vomit",
            "bodily function",
            "fart",
        ]
    ):
        return "Medical.Events", "both", "skip"

    # 9. Structural Timestamps
    if any(k in combined for k in ["intro", "outro", "credits", "recap", "outtakes"]):
        return "Structural.Timestamps", "both", "skip"

    # Fallback default
    return "Violence.Tiers", "video", "skip"


def parse_reddit_post_text(
    text: str, default_category: Optional[str] = None
) -> List[ParsedCue]:
    """Extract all timecode ranges and accompanying descriptions/categories from freeform text."""
    cues: List[ParsedCue] = []
    lines = text.splitlines()

    current_category_header = default_category or "Violence.Tiers"

    for i, line in enumerate(lines):
        trimmed = clean_line_typos(line.strip())
        if not trimmed:
            continue

        # Check if line looks like a category header, e.g. **NUDITY**, SEXUAL CONTENT, GORE
        header_candidate = re.sub(r"[*#_]", "", trimmed).strip()
        if (
            header_candidate.isupper()
            and len(header_candidate) < 60
            and not TIMESTAMP_RANGE_REGEX.search(header_candidate)
        ):
            current_category_header = header_candidate
            continue

        # 1. Look for range matches in line
        found_range = False
        for match in TIMESTAMP_RANGE_REGEX.finditer(trimmed):
            try:
                start_raw = match.group("start")
                end_raw = match.group("end")
                start_ms, start_str = format_timestamp_string(start_raw)
                end_ms, end_str = format_timestamp_string(end_raw)

                # Ensure valid chronological range
                if end_ms <= start_ms:
                    continue

                found_range = True
                # The rest of the line (or trailing text) is context/description
                desc = trimmed[match.end() :].strip(" -:;,()")
                # Also capture preceding text on line if description is empty
                if not desc:
                    desc = trimmed[: match.start()].strip(" -:;,()")

                category, channel, action = map_category_and_channel(
                    current_category_header, desc
                )

                cues.append(
                    ParsedCue(
                        start_ms=start_ms,
                        end_ms=end_ms,
                        start_str=start_str,
                        end_str=end_str,
                        category=category,
                        channel=channel,
                        action=action,
                        description=desc[:150] if desc else None,
                    )
                )
            except Exception:
                continue

        # 2. Fallback: Check for standalone single timestamp e.g. "At 1:01:57 (Cypher tells Neo...)"
        if not found_range and re.search(r"^(?:at\s+|around\s+)?(?:\d{1,2}:)?\d{1,2}:\d{2}\b", trimmed, re.I):
            m_single = SINGLE_TIMESTAMP_REGEX.search(trimmed)
            if m_single:
                try:
                    ts_raw = m_single.group("ts")
                    start_ms, start_str = format_timestamp_string(ts_raw)
                    # Default duration: 5 seconds
                    end_ms = start_ms + 5000
                    end_str = ms_to_timestamp(end_ms)
                    desc = trimmed[m_single.end() :].strip(" -:;,()")
                    category, channel, action = map_category_and_channel(
                        current_category_header, desc
                    )
                    cues.append(
                        ParsedCue(
                            start_ms=start_ms,
                            end_ms=end_ms,
                            start_str=start_str,
                            end_str=end_str,
                            category=category,
                            channel=channel,
                            action=action,
                            description=desc[:150] if desc else None,
                        )
                    )
                except Exception:
                    pass

    # Merge overlapping cues of the same category
    return merge_cues(cues)


def merge_cues(cues: List[ParsedCue]) -> List[ParsedCue]:
    """Merge overlapping or adjacent cues with identical category and action."""
    if not cues:
        return []

    sorted_cues = sorted(cues, key=lambda c: c.start_ms)
    merged: List[ParsedCue] = []

    for cue in sorted_cues:
        if not merged:
            merged.append(cue)
            continue

        prev = merged[-1]
        # Check if same category, channel, and action and overlapping or touching
        if (
            prev.category == cue.category
            and prev.channel == cue.channel
            and prev.action == cue.action
            and cue.start_ms <= prev.end_ms
        ):
            new_end_ms = max(prev.end_ms, cue.end_ms)
            desc_parts = [p for p in [prev.description, cue.description] if p]
            combined_desc = " / ".join(desc_parts) if desc_parts else None
            merged[-1] = ParsedCue(
                start_ms=prev.start_ms,
                end_ms=new_end_ms,
                start_str=prev.start_str,
                end_str=ms_to_timestamp(new_end_ms),
                category=prev.category,
                channel=prev.channel,
                action=prev.action,
                description=combined_desc[:200] if combined_desc else None,
            )
        else:
            merged.append(cue)

    return merged


def invert_safe_ranges(
    safe_ranges: List[Tuple[int, int]],
    category: str = "Violence.Tiers",
    channel: str = "video",
    action: str = "skip",
) -> List[ParsedCue]:
    """Invert safe playback intervals to produce skip cues for the gaps."""
    if not safe_ranges:
        return []

    sorted_ranges = sorted(safe_ranges, key=lambda r: r[0])
    # Merge safe ranges
    merged_safe: List[Tuple[int, int]] = []
    for start, end in sorted_ranges:
        if not merged_safe:
            merged_safe.append((start, end))
        else:
            p_start, p_end = merged_safe[-1]
            if start <= p_end:
                merged_safe[-1] = (p_start, max(p_end, end))
            else:
                merged_safe.append((start, end))

    cues: List[ParsedCue] = []
    for (_, prev_end), (cur_start, _) in zip(merged_safe, merged_safe[1:]):
        if cur_start > prev_end:
            cues.append(
                ParsedCue(
                    start_ms=prev_end,
                    end_ms=cur_start,
                    start_str=ms_to_timestamp(prev_end),
                    end_str=ms_to_timestamp(cur_start),
                    category=category,
                    channel=channel,
                    action=action,
                    description="Objectionable scene",
                )
            )

    return cues
