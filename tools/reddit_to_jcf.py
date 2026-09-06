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


def _match_language(combined: str) -> Optional[Tuple[str, str, str]]:
    if any(
        k in combined
        for k in [
            "profanity",
            "language",
            "swear",
            "curse",
            "f-word",
            "slur",
            "blasphem",
            "crude language",
        ]
    ):
        if any(k in combined for k in ["slur", "racial", "racist", "bigot"]):
            return "Language.RacialAndBigotedSlurs", "audio", "mute"
        if any(k in combined for k in ["god", "jesus", "christ", "lord", "blasphem"]):
            return "Language.Blasphemy", "audio", "mute"
        if any(k in combined for k in ["childish", "butt", "fart", "dumb", "stupid", "poop"]):
            return "Language.ChildishLanguage", "audio", "mute"
        if "caption" in combined or "subtitle" in combined:
            return "Language.CaptionsWithProfanity", "both", "mute"
        return "Language.GeneralProfanity", "audio", "mute"
    return None


def _match_sexual_assault_or_graphic(combined: str) -> Optional[Tuple[str, str, str]]:
    if any(
        k in combined
        for k in ["middle finger", "flipping off", "vulgar gesture", "obscene gesture"]
    ):
        return "SexualReferences.Visuals", "video", "skip"

    if any(
        k in combined
        for k in [
            "sexual dialogue",
            "sexual conversation",
            "sexual remark",
            "sexual comment",
            "innuendo",
            "dirty talk",
            "prostitution talk",
        ]
    ):
        return "SexualReferences.ContextualDialogue", "video", "skip"

    if any(
        k in combined
        for k in [
            "sexual assault",
            "rape",
            "molest",
            "non-consensual",
            "groping",
            "forces herself",
            "forces himself",
        ]
    ):
        return "SexAndNudity.SexualAssault", "video", "skip"

    if any(
        k in combined
        for k in [
            "intercourse",
            "sex scene",
            "explicit sex",
            "oral sex",
            "blowjob",
            "handjob",
            "masturbat",
            "orgasm",
            "penetrat",
            "erotic",
            "sex with nudity",
        ]
    ) or (
        re.search(r"\bsex\b", combined)
        and any(k in combined for k in ["explicit", "scene", "act", "bed", "having"])
    ):
        return "SexAndNudity.Graphic", "video", "skip"

    return None


def _match_nudity(combined: str) -> Optional[Tuple[str, str, str]]:
    if any(
        k in combined
        for k in [
            "full nudity", "frontal nudity", "naked", "completely naked", "unclad",
            "bare breasts", "topless female", "topless woman", "topless",
            "bare backside", "bare butt", "buttocks", "abrupt nudity",
        ]
    ):
        return "SexAndNudity.FullNudity", "video", "skip"

    if any(
        k in combined
        for k in [
            "nudity", "nude", "underwear", "lingerie", "bra", "panties", "boxers",
            "no shirt", "shirtless", "revealing", "cleavage", "immodest", "bikini",
            "swimsuit", "swimwear", "shower", "undressing", "undress", "strip", "scantily",
        ]
    ):
        if any(k in combined for k in ["bikini", "swimsuit", "swimwear", "beach"]):
            return "SexAndNudity.Mild", "video", "skip"
        return "SexAndNudity.PartialNudity", "video", "skip"

    return None


def _match_intimacy_or_nudity(combined: str) -> Optional[Tuple[str, str, str]]:
    if any(
        k in combined
        for k in [
            "implied sex", "suggestive", "fooling in bed", "sex without nudity",
            "sensual", "bedroom scene", "suggestive dancing", "adult scene",
            "touchy feely", "inappropriate content",
        ]
    ):
        return "SexAndNudity.ImpliedSex", "video", "skip"

    if any(
        k in combined
        for k in ["kissing", "kiss", "make out", "making out", "caress", "passionate kiss"]
    ):
        return "SexAndNudity.PhysicalIntimacy", "video", "skip"

    return _match_nudity(combined)


def _match_sexual(combined: str) -> Optional[Tuple[str, str, str]]:
    return _match_sexual_assault_or_graphic(combined) or _match_intimacy_or_nudity(combined)


def _match_severe_violence(combined: str) -> Optional[Tuple[str, str, str]]:
    if any(
        k in combined
        for k in [
            "gore", "decapitat", "dismember", "severed", "head rolling", "headless",
            "mutilat", "entrails", "organs", "bloody corpses", "bloody shoot",
            "cut out eye", "cuts out eye",
        ]
    ):
        return "Violence.Gore", "video", "skip"

    if any(k in combined for k in ["jumpscare", "jump scare", "startle", "surprise scare"]):
        return "Violence.JumpScares", "video", "skip"

    if any(
        k in combined
        for k in [
            "disturbing", "corpse", "dead body", "skeletal", "suicide", "torture",
            "scary", "creepy", "fear", "unsettling", "psychological", "hanging",
        ]
    ):
        return "Violence.Disturbing", "video", "skip"

    if any(
        k in combined
        for k in [
            "graphic violence", "fatal", "stabbed in eye", "torture death", "slashes his cheeks",
            "brutal", "visceral", "kill", "murder", "behead", "close range",
        ]
    ):
        return "Violence.Graphic", "video", "skip"

    return None


def _match_combat_or_mild(combined: str) -> Optional[Tuple[str, str, str]]:
    if any(
        k in combined
        for k in [
            "slap",
            "slapping",
            "comic",
            "playful",
            "wrestling",
            "punch",
            "punches",
            "hit with melon",
            "shoving",
            "bloodless",
            "fistfight",
        ]
    ) and not any(
        k in combined for k in ["blood", "bloody", "stab", "shot", "gun", "knife", "wound"]
    ):
        return "Violence.Mild", "video", "skip"

    if any(
        k in combined
        for k in [
            "violence",
            "bloody",
            "blood",
            "stab",
            "shooting",
            "shoot",
            "shot",
            "gunfight",
            "knife",
            "sword",
            "fight",
            "wound",
            "attack",
            "bullet",
            "explosion",
            "choke",
            "car flips",
            "wreck",
        ]
    ):
        return "Violence.Moderate", "video", "skip"

    return None


def _match_violence(combined: str) -> Optional[Tuple[str, str, str]]:
    return _match_severe_violence(combined) or _match_combat_or_mild(combined)


def _match_substances_and_other(combined: str) -> Optional[Tuple[str, str, str]]:
    if any(
        k in combined
        for k in ["substance", "drug", "alcohol", "smoke", "smoking", "drink", "drunk"]
    ):
        if any(
            k in combined
            for k in [
                "illegal drug",
                "narcotic",
                "marijuana",
                "weed",
                "cocaine",
                "heroin",
                "meth",
                "pills",
                "high",
                "overdose",
                "snort",
                "injection",
                "catnip",
            ]
        ):
            return "Substances.IllegalDrugs", "video", "skip"
        if any(
            k in combined
            for k in ["tobacco", "smoke", "smoking", "cigarette", "cigar", "vape", "vaping"]
        ):
            return "Substances.Tobacco", "video", "skip"
        return "Substances.Alcohol", "video", "skip"

    if any(
        k in combined
        for k in [
            "medical",
            "hospital",
            "surgery",
            "procedure",
            "needle",
            "tattoo needle",
            "doctor",
        ]
    ):
        return "Medical.Events", "both", "skip"
    if any(
        k in combined for k in ["vomit", "barf", "puke", "throw up", "barfing", "bodily function"]
    ):
        return "Medical.BodilyFunctions", "both", "skip"

    if any(k in combined for k in ["credits", "opening credits", "closing credits"]):
        return "Structural.Credits", "both", "skip"
    if any(k in combined for k in ["intro", "outro", "recap", "outtake", "blooper"]):
        return "Structural.IntroRecap", "both", "skip"

    return None


def map_category_and_channel(category_label: str, text_context: str = "") -> Tuple[str, str, str]:
    """Map arbitrary Reddit/community category labels to plugin standard category, channel, and action."""
    combined = f"{category_label} {text_context}".lower()
    return (
        _match_language(combined)
        or _match_sexual(combined)
        or _match_violence(combined)
        or _match_substances_and_other(combined)
        or ("Violence.Moderate", "video", "skip")
    )


def _parse_range_cue_match(
    match: re.Match, trimmed: str, header: str
) -> Optional[ParsedCue]:
    try:
        start_raw = match.group("start")
        end_raw = match.group("end")
        start_ms, start_str = format_timestamp_string(start_raw)
        end_ms, end_str = format_timestamp_string(end_raw)

        if end_ms <= start_ms:
            return None

        desc = trimmed[match.end() :].strip(" -:;,()")
        if not desc:
            desc = trimmed[: match.start()].strip(" -:;,()")

        category, channel, action = map_category_and_channel(header, desc)
        return ParsedCue(
            start_ms=start_ms,
            end_ms=end_ms,
            start_str=start_str,
            end_str=end_str,
            category=category,
            channel=channel,
            action=action,
            description=desc[:150] if desc else None,
        )
    except Exception:
        return None


def _parse_single_cue_match(
    m_single: re.Match, trimmed: str, header: str
) -> Optional[ParsedCue]:
    try:
        ts_raw = m_single.group("ts")
        start_ms, start_str = format_timestamp_string(ts_raw)
        end_ms = start_ms + 5000
        end_str = ms_to_timestamp(end_ms)
        desc = trimmed[m_single.end() :].strip(" -:;,()")
        category, channel, action = map_category_and_channel(header, desc)
        return ParsedCue(
            start_ms=start_ms,
            end_ms=end_ms,
            start_str=start_str,
            end_str=end_str,
            category=category,
            channel=channel,
            action=action,
            description=desc[:150] if desc else None,
        )
    except Exception:
        return None


def _extract_reddit_header(trimmed: str) -> Optional[str]:
    header_candidate = re.sub(r"[*#_]", "", trimmed).strip()
    if (
        header_candidate.isupper()
        and len(header_candidate) < 60
        and not TIMESTAMP_RANGE_REGEX.search(header_candidate)
    ):
        return header_candidate
    return None


def _parse_reddit_line_cues(trimmed: str, header: str) -> List[ParsedCue]:
    matches = list(TIMESTAMP_RANGE_REGEX.finditer(trimmed))
    if matches:
        res = []
        for m in matches:
            c = _parse_range_cue_match(m, trimmed, header)
            if c:
                res.append(c)
        return res

    if re.search(r"^(?:at\s+|around\s+)?(?:\d{1,2}:)?\d{1,2}:\d{2}\b", trimmed, re.I):
        m_single = SINGLE_TIMESTAMP_REGEX.search(trimmed)
        if m_single:
            c = _parse_single_cue_match(m_single, trimmed, header)
            if c:
                return [c]
    return []


def parse_reddit_post_text(text: str, default_category: Optional[str] = None) -> List[ParsedCue]:
    """Extract all timecode ranges and accompanying descriptions/categories from freeform text."""
    cues: List[ParsedCue] = []
    current_category_header = default_category or "SexAndNudity.FullNudity"

    for line in text.splitlines():
        trimmed = clean_line_typos(line.strip())
        if not trimmed:
            continue

        header = _extract_reddit_header(trimmed)
        if header:
            current_category_header = header
            continue

        cues.extend(_parse_reddit_line_cues(trimmed, current_category_header))

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
    category: str = "Violence.Graphic",
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


LEGACY_CATEGORIES = {
    "Violence.Tiers",
    "SexAndNudity.NudityProfiles",
    "SexAndNudity.OnscreenActivity",
    "Substances.Usage",
    "Structural.Timestamps",
}


def _parse_jcf_note_block(lines: List[str], i: int) -> Tuple[Dict[str, Optional[str]], int]:
    meta: Dict[str, Optional[str]] = {}
    i += 1
    while i < len(lines) and lines[i].strip():
        line = lines[i].strip()
        if line.startswith("TITLE "):
            meta["title"] = line[6:].strip()
        elif line.startswith("YEAR "):
            meta["year"] = line[5:].strip()
        elif line.startswith("IMDB "):
            meta["imdb_id"] = line[5:].strip()
        elif line.startswith("SOURCE "):
            meta["source"] = line[7:].strip()
        i += 1
    return meta, i


def _parse_jcf_cue_block(lines: List[str], i: int) -> Tuple[ParsedCue, int]:
    parts = lines[i].strip().split("-->")
    start_str = parts[0].strip()
    end_str = parts[1].strip()
    start_ms = parse_timestamp_to_ms(start_str)
    end_ms = parse_timestamp_to_ms(end_str)

    cat = "Violence.Moderate"
    chan = "video"
    act = "skip"
    desc = None

    i += 1
    while i < len(lines) and lines[i].strip():
        attr_line = lines[i].strip()
        if attr_line.startswith("category:"):
            cat = attr_line.split(":", 1)[1].strip()
        elif attr_line.startswith("channel:"):
            chan = attr_line.split(":", 1)[1].strip()
        elif attr_line.startswith("action:"):
            act = attr_line.split(":", 1)[1].strip()
        elif attr_line.startswith("description:"):
            desc = attr_line.split(":", 1)[1].strip()
        i += 1

    cue = ParsedCue(
        start_ms=start_ms,
        end_ms=end_ms,
        start_str=start_str,
        end_str=end_str,
        category=cat,
        channel=chan,
        action=act,
        description=desc,
    )
    return cue, i


def load_jcf_file(file_path: "Path | str") -> JcfDocument:
    """Load an existing .jcf file into a JcfDocument."""
    from pathlib import Path

    path = Path(file_path)
    content = path.read_text(encoding="utf-8")
    lines = content.splitlines()

    title = path.stem
    year = None
    imdb_id = None
    source = None
    cues: List[ParsedCue] = []

    i = 0
    while i < len(lines):
        line = lines[i].strip()
        if line == "NOTE":
            meta, i = _parse_jcf_note_block(lines, i)
            title = meta.get("title", title)
            year = meta.get("year", year)
            imdb_id = meta.get("imdb_id", imdb_id)
            source = meta.get("source", source)
            continue
        if "-->" in line:
            cue, i = _parse_jcf_cue_block(lines, i)
            cues.append(cue)
            continue
        i += 1

    return JcfDocument(
        title=title,
        year=year,
        imdb_id=imdb_id,
        source=source,
        cues=cues,
    )


def upgrade_cue_category(cue: ParsedCue) -> Tuple[ParsedCue, bool]:
    """Upgrade a cue from legacy category to VidAngel/IMDb standard if applicable."""
    if cue.category in LEGACY_CATEGORIES or not cue.category:
        new_cat, new_chan, new_act = map_category_and_channel(cue.category, cue.description or "")
        if new_cat != cue.category or new_chan != cue.channel:
            return (
                ParsedCue(
                    start_ms=cue.start_ms,
                    end_ms=cue.end_ms,
                    start_str=cue.start_str,
                    end_str=cue.end_str,
                    category=new_cat,
                    channel=new_chan if cue.channel in ("video", "audio", "both") else new_chan,
                    action=cue.action or new_act,
                    description=cue.description,
                ),
                True,
            )
    return cue, False


def upgrade_jcf_file(file_path: "Path | str") -> bool:
    """Read a JCF file, upgrade legacy categories in-place, and return True if modified."""
    from pathlib import Path

    path = Path(file_path)
    if not path.exists():
        return False
    doc = load_jcf_file(path)
    modified = False
    new_cues = []
    for c in doc.cues:
        upgraded, did_change = upgrade_cue_category(c)
        if did_change:
            modified = True
        new_cues.append(upgraded)

    if modified:
        doc.cues = new_cues
        path.write_text(doc.to_jcf(), encoding="utf-8")
        return True
    return False
