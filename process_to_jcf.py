#!/usr/bin/env python3
"""Process TheTimestampDudes posts into discrete Jellyfin Content Filter (.jcf) files.

This utility parses downloaded posts from TheTimestampDudes (from buymeacoffee.com),
extracts all timestamp ranges and objectionable content descriptions, maps them to
Jellyfin Content Filter categories, and exports discrete WEBVTT JCF sidecar files
ready for use with the Jellyfin Content Filter plugin.
"""

from __future__ import annotations

import argparse
import json
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

DEFAULT_INPUT_FILE = "thetimestampdudes_posts/posts_clean.json"
DEFAULT_OUTPUT_DIR = "jcf_thetimestampdudes"

# Known release years for movies where year was not in the title heading
KNOWN_MOVIE_YEARS: Dict[str, Tuple[str, int]] = {
    "donnie darko (theatrical cut)": ("Donnie Darko (Theatrical Cut)", 2001),
    "donnie darko (directors cut)": ("Donnie Darko (Director's Cut)", 2001),
    "the shining (theatrical cut)": ("The Shining (Theatrical Cut)", 1980),
    "blade runner (final cut)": ("Blade Runner (Final Cut)", 1982),
    "limitless (theatrical version)": ("Limitless (Theatrical Version)", 2011),
    "snowpiercer (2013_2014)": ("Snowpiercer", 2013),
    "dark city (directors cut) 1998": ("Dark City (Director's Cut)", 1998),
    "l.a confidential": ("L.A. Confidential", 1997),
}

# Informational posts that do not contain movie timestamps
NON_MOVIE_SLUGS_OR_TITLES = [
    "faq-for-our-page",
    "faq for our page",
    "requesting movies post",
    "requesting timestamps",
    "skip scenes in movies",
    "skip nudity",
    "clean up movies",
    "fast forward movie scenes",
]

# Regex for range extraction
RANGE_REGEX = re.compile(
    r"(?:(?:at|from)\s+)?(\d{1,2}:\d{2}(?::\d{2})?|\d+\s*(?:sec|second|seconds|s)|0)\s*(?:-|–|—|to|till|through)\s*(?:(?:at|till|to)\s+)?(\d{1,2}:\d{2}(?::\d{2})?)",
    re.IGNORECASE,
)

# Regex for standalone single timestamp
SINGLE_TS_REGEX = re.compile(
    r"(?:^|[\s(])(?:at\s+)?(\d{1,2}:\d{2}(?::\d{2})?)\b(?!\s*(?:-|–|—|to|till|through|\d))",
    re.IGNORECASE,
)


@dataclass
class Cue:
    start_seconds: int
    end_seconds: int
    category: str
    description: str
    raw_category: str
    channel: str = "video"
    action: str = "skip"

    @property
    def start_str(self) -> str:
        return format_time_seconds(self.start_seconds)

    @property
    def end_str(self) -> str:
        return format_time_seconds(self.end_seconds)


def format_time_seconds(sec: int) -> str:
    """Format integer seconds into JCF timestamp HH:MM:SS.000."""
    sec = max(0, sec)
    h = sec // 3600
    m = (sec % 3600) // 60
    s = sec % 60
    return f"{h:02}:{m:02}:{s:02}.000"


def parse_time_to_seconds(ts_str: str) -> Optional[int]:
    """Parse a time string (e.g. '1:18:21', '14:39', '4:13', '56 seconds', '0') to total seconds."""
    s = ts_str.strip().lower()
    if s in ("0", "00:00", "0:00", "00:00:00"):
        return 0

    m_sec = re.match(r"^(\d+)\s*(?:sec|second|seconds|s)$", s)
    if m_sec:
        return int(m_sec.group(1))

    parts = s.split(":")
    try:
        if len(parts) == 2:
            # MM:SS
            m, sec = int(parts[0]), int(parts[1])
            return m * 60 + sec
        elif len(parts) == 3:
            # H:MM:SS or HH:MM:SS
            h, m, sec = int(parts[0]), int(parts[1]), int(parts[2])
            return h * 3600 + m * 60 + sec
    except ValueError:
        return None
    return None


def clean_line_typos(line: str) -> str:
    """Fix common spacing or typographical glitches in timestamp lines."""
    # Fix "1:18: 21" -> "1:18:21"
    line = re.sub(r"(\d+:\d+:)\s+(\d+)", r"\1\2", line)
    # Fix "1:35 28" -> "1:35:28"
    line = re.sub(r"(?:(?<=[–—\-to\s])|^)(\d{1,2}:\d{2})\s+(\d{2})\b", r"\1:\2", line)
    # Fix "1:019:14" -> "1:09:14"
    line = re.sub(r"\b(\d+):01(\d):", r"\1:0\2:", line)
    return line


def map_category_and_channel(raw_cat: str, desc: str = "") -> Tuple[str, str, str]:
    """Map raw TheTimestampDudes category header/description to plugin standard category, channel, and action."""
    combined = f"{raw_cat} {desc}".lower()

    # 1. Profanity / Language / Inappropriate Talk
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
            "inappropriate talk",
            "inappropriate comment",
            "offensive",
        ]
    ):
        if any(k in combined for k in ["slur", "racial", "racist", "bigot"]):
            return "Language.RacialAndBigotedSlurs", "audio", "mute"
        if any(k in combined for k in ["god", "jesus", "christ", "lord", "blasphem"]):
            return "Language.Blasphemy", "audio", "mute"
        if any(k in combined for k in ["childish", "butt", "fart", "dumb", "stupid", "poop"]):
            return "Language.ChildishLanguage", "audio", "mute"
        return "Language.GeneralProfanity", "audio", "mute"

    # 2. Gestures
    if any(
        k in combined
        for k in ["middle finger", "flipping off", "vulgar gesture", "obscene gesture"]
    ):
        return "SexualReferences.Visuals", "video", "skip"

    # 3. Sexual Dialogue / Innuendo
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

    # 4. Sex & Nudity
    # 4a. Sexual Assault / Rape
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

    # 4b. Graphic Sex / Intercourse / Explicit / Erotic / Masturbation
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

    # 4c. Implied Sex / Bedroom / Suggestive Activity (without explicit intercourse or intimacy)
    if any(
        k in combined
        for k in [
            "implied sex",
            "fooling in bed",
            "sex without nudity",
            "sensual",
            "bedroom scene",
            "suggestive dancing",
            "adult scene",
            "touchy feely",
        ]
    ):
        return "SexAndNudity.ImpliedSex", "video", "skip"

    # 4d. Physical Intimacy (Kissing / Making out / Caress)
    if any(
        k in combined
        for k in ["kissing", "kiss", "make out", "making out", "caress", "passionate kiss"]
    ):
        return "SexAndNudity.PhysicalIntimacy", "video", "skip"

    # 4e. Full Nudity
    if any(
        k in combined
        for k in [
            "full nudity",
            "frontal nudity",
            "naked",
            "completely naked",
            "unclad",
            "bare breasts",
            "topless female",
            "topless woman",
            "topless",
            "bare backside",
            "bare butt",
            "buttocks",
            "abrupt nudity",
            "boob",
            "breast",
            "penis",
        ]
    ) or (
        any(k in combined for k in ["nudity", "nude"])
        and not any(
            k in combined
            for k in [
                "underwear",
                "lingerie",
                "bra",
                "panties",
                "boxer",
                "boxers",
                "shirtless",
                "cleavage",
                "swimwear",
                "bikini",
                "swimsuit",
                "crop top",
                "revealing",
            ]
        )
    ):
        return "SexAndNudity.FullNudity", "video", "skip"

    # 4f. Partial Nudity / Revealing Clothing / Lingerie / Underwear / Swimwear
    if any(
        k in combined
        for k in [
            "nudity",
            "nude",
            "underwear",
            "lingerie",
            "bra",
            "panties",
            "boxer",
            "boxers",
            "no shirt",
            "shirtless",
            "revealing",
            "cleavage",
            "immodest",
            "bikini",
            "swimsuit",
            "swimwear",
            "shower",
            "undressing",
            "undress",
            "strip",
            "stripping",
            "scantily",
            "crop top",
            "suggestive",
        ]
    ):
        if any(k in combined for k in ["bikini", "swimsuit", "swimwear", "beach"]):
            return "SexAndNudity.Mild", "video", "skip"
        return "SexAndNudity.PartialNudity", "video", "skip"

    # 5. Violence & Horror
    # 5a. Gore / Dismemberment / Organs / Decapitation
    if any(
        k in combined
        for k in [
            "gore",
            "decapitat",
            "dismember",
            "severed",
            "head rolling",
            "headless",
            "mutilat",
            "entrails",
            "organs",
            "bloody corpses",
            "bloody shoot",
            "cut out eye",
            "cuts out eye",
            "mangled",
            "intestine",
        ]
    ):
        return "Violence.Gore", "video", "skip"

    # 5b. Jump Scares
    if any(k in combined for k in ["jumpscare", "jump scare", "startle", "surprise scare"]):
        return "Violence.JumpScares", "video", "skip"

    # 5c. Disturbing / Psychological Horror / Corpses / Suicide
    if any(
        k in combined
        for k in [
            "disturbing",
            "corpse",
            "dead body",
            "skeletal",
            "suicide",
            "torture",
            "scary",
            "creepy",
            "fear",
            "unsettling",
            "psychological",
            "hanging",
        ]
    ):
        return "Violence.Disturbing", "video", "skip"

    # 5d. Graphic Violence / Severe / Fatal
    if any(
        k in combined
        for k in [
            "graphic violence",
            "fatal",
            "stabbed in eye",
            "torture death",
            "slashes his cheeks",
            "brutal",
            "visceral",
            "kill",
            "murder",
            "behead",
            "close range",
        ]
    ):
        return "Violence.Graphic", "video", "skip"

    # 5e. Mild Violence
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

    # 5f. Moderate Violence / General Combat / Shootouts / Fights
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
            "beat",
            "gun",
            "assault",
        ]
    ):
        return "Violence.Moderate", "video", "skip"

    # 6. Substance Use
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
                "drugs",
            ]
        ):
            return "Substances.IllegalDrugs", "video", "skip"
        if any(
            k in combined
            for k in ["tobacco", "smoke", "smoking", "cigarette", "cigar", "vape", "vaping"]
        ):
            return "Substances.Tobacco", "video", "skip"
        if any(
            k in combined
            for k in [
                "alcohol",
                "drink",
                "drinking",
                "drunk",
                "beer",
                "wine",
                "liquor",
                "sake",
                "bar",
            ]
        ):
            return "Substances.Alcohol", "video", "skip"
        return "Substances.Alcohol", "video", "skip"

    # 7. Medical & Biological
    if any(
        k in combined
        for k in [
            "seizure",
            "strobe",
            "flashing",
            "epilepsy",
            "medical",
            "hospital",
            "surgery",
            "procedure",
            "needle",
            "doctor",
        ]
    ):
        return "Medical.Events", "both", "skip"
    if any(
        k in combined
        for k in [
            "vomit",
            "barf",
            "puke",
            "throw up",
            "barfing",
            "bodily function",
            "flatulence",
            "toilet",
        ]
    ):
        return "Medical.BodilyFunctions", "both", "skip"

    # 8. Structural Timestamps
    if any(k in combined for k in ["credits", "opening credits", "closing credits"]):
        return "Structural.Credits", "both", "skip"
    if any(k in combined for k in ["intro", "outro", "recap", "outtake", "blooper"]):
        return "Structural.IntroRecap", "both", "skip"

    # Fallback default
    return "Violence.Moderate", "video", "skip"


def map_category(raw_cat: str, desc: str = "") -> str:
    """Map raw TheTimestampDudes category header/description to canonical category string."""
    cat, _, _ = map_category_and_channel(raw_cat, desc)
    return cat


def is_header_line(line: str) -> bool:
    """Detect whether a line is a category header rather than a timestamp cue line."""
    line = line.strip()
    if not line:
        return False
    if re.search(r"\d+:\d+", line):
        return False
    if len(line) > 100:
        return False

    # Check for keywords or uppercase
    if line.isupper() and len(line) >= 3:
        return True

    lower = line.lower()
    cat_keywords = [
        "nudity",
        "violence",
        "suggestive",
        "gore",
        "profanity",
        "alcohol",
        "blood",
        "kiss",
        "underwear",
        "shirt",
        "curse",
        "sex",
        "rude",
        "seizure",
        "drug",
        "smoking",
        "revealing",
        "talk",
        "warning",
        "disturbing",
        "scary",
        "jumpscare",
        "gross",
    ]
    return any(k in lower for k in cat_keywords)


def parse_post_into_cues(plain_text: str) -> List[Cue]:
    """Parse the text content of a post into structured JCF cues."""
    lines = plain_text.splitlines()
    cues: List[Cue] = []
    current_cat = "General"

    for raw_line in lines:
        line = raw_line.strip()
        if not line:
            continue

        # Skip headers / introductory metadata lines
        if (
            line.startswith("#")
            or line.startswith("-")
            or line.startswith("http")
            or line.startswith("If you have any questions")
        ):
            continue

        # Check if line is a category header
        if is_header_line(line):
            cleaned_h = re.sub(
                r"^(?:(?:directly|right)\s+after|in\s+between\s+scene\s+above|at\s+the\s+same\s+time[^\:]*):\s*",
                "",
                line,
                flags=re.IGNORECASE,
            ).strip(" :-–—")
            if cleaned_h:
                current_cat = cleaned_h
            continue

        clean_l = clean_line_typos(line)
        matches = list(RANGE_REGEX.finditer(clean_l))

        if matches:
            for m in matches:
                st_raw = m.group(1)
                et_raw = m.group(2)

                st_sec = parse_time_to_seconds(st_raw)
                et_sec = parse_time_to_seconds(et_raw)

                if st_sec is None or et_sec is None:
                    continue

                # Fix omitted hour in end time: e.g. 1:14:03 - 15:30 -> 1:14:03 - 1:15:30
                if ":" in st_raw and len(st_raw.split(":")) == 3:
                    # Start had hours
                    start_h = int(st_raw.split(":")[0])
                    if len(et_raw.split(":")) == 2:
                        # End had only MM:SS
                        candidate_et = start_h * 3600 + et_sec
                        if candidate_et >= st_sec:
                            et_sec = candidate_et

                # Fix swapped start/end if within reasonable threshold
                if et_sec < st_sec:
                    if st_sec - et_sec < 600:  # within 10 min typo
                        st_sec, et_sec = et_sec, st_sec
                    else:
                        # Skip corrupted timestamps
                        continue

                # Ensure minimum 1-second cue
                if et_sec == st_sec:
                    et_sec = st_sec + 3

                # Extract description for this cue
                # Strip out the timestamp substring
                desc = (clean_l[: m.start()] + " " + clean_l[m.end() :]).strip(" -:–—()")
                desc = re.sub(r"\s+", " ", desc).strip()
                if not desc or desc.lower() in ("same as above", "same"):
                    desc = f"Scene flagged under {current_cat}"

                full_desc = f"[{current_cat}] {desc}"
                mapped_cat, channel, action = map_category_and_channel(current_cat, desc)

                cues.append(
                    Cue(
                        start_seconds=st_sec,
                        end_seconds=et_sec,
                        category=mapped_cat,
                        description=full_desc,
                        raw_category=current_cat,
                        channel=channel,
                        action=action,
                    )
                )
        else:
            # Check for standalone single timestamp e.g. "At 1:15:42 (a woman starts to unbutton her shirt...)"
            s_match = SINGLE_TS_REGEX.search(clean_l)
            if s_match:
                ts_raw = s_match.group(1)
                st_sec = parse_time_to_seconds(ts_raw)
                if st_sec is not None:
                    et_sec = st_sec + 8  # Default 8s duration for point events
                    desc = (clean_l[: s_match.start()] + " " + clean_l[s_match.end() :]).strip(
                        " -:–—()"
                    )
                    desc = re.sub(r"\s+", " ", desc).strip()
                    if not desc:
                        desc = f"Scene flagged under {current_cat}"

                    full_desc = f"[{current_cat}] {desc}"
                    mapped_cat, channel, action = map_category_and_channel(current_cat, desc)

                    cues.append(
                        Cue(
                            start_seconds=st_sec,
                            end_seconds=et_sec,
                            category=mapped_cat,
                            description=full_desc,
                            raw_category=current_cat,
                            channel=channel,
                            action=action,
                        )
                    )

    # Sort cues chronologically
    cues.sort(key=lambda c: (c.start_seconds, c.end_seconds))
    return cues


def merge_cues(cues: List[Cue], max_gap_seconds: int = 1) -> List[Cue]:
    """Merge overlapping or adjacent cues with matching category and action."""
    if not cues:
        return []

    merged: List[Cue] = []
    for cue in cues:
        if not merged:
            merged.append(cue)
            continue

        prev = merged[-1]
        # Check if cues overlap or are immediately adjacent with compatible action
        if (
            cue.category == prev.category
            and cue.action == prev.action
            and cue.channel == prev.channel
        ):
            if cue.start_seconds <= prev.end_seconds + max_gap_seconds:
                # Extend end time and append description if distinct
                new_end = max(prev.end_seconds, cue.end_seconds)
                new_desc = prev.description
                if cue.description and cue.description not in prev.description:
                    new_desc = f"{prev.description}; {cue.description}"
                merged[-1] = Cue(
                    start_seconds=prev.start_seconds,
                    end_seconds=new_end,
                    category=prev.category,
                    description=new_desc,
                    raw_category=prev.raw_category,
                    channel=prev.channel,
                    action=prev.action,
                )
                continue

        merged.append(cue)
    return merged


def build_jcf_content(
    title: str,
    year: Optional[int | str],
    cues: List[Cue],
    source: str = "TheTimestampDudes (buymeacoffee.com/thetimestampdudes)",
) -> str:
    """Generate compliant WEBVTT JCF document string."""
    lines = [
        "WEBVTT JCF",
        "",
        "NOTE",
        f"TITLE {title}",
    ]
    if year:
        lines.append(f"YEAR {year}")
    lines.extend(
        [
            f"SOURCE {source}",
            "",
        ]
    )

    for cue in cues:
        lines.extend(
            [
                f"{cue.start_str} --> {cue.end_str}",
                f"category: {cue.category}",
                f"description: {cue.description}",
                f"channel: {cue.channel}",
                f"action: {cue.action}",
                "",
            ]
        )

    return "\n".join(lines).rstrip() + "\n"


def sanitize_filename(name: str) -> str:
    """Sanitize filename to prevent invalid filesystem characters."""
    s = re.sub(r'[\\/*?:"<>|]', "_", name)
    s = re.sub(r"\s+", " ", s).strip(". _")
    return s or "unnamed"


def process_lord_of_the_rings_trilogy(
    plain_text: str,
    output_dir: Path,
) -> List[Tuple[str, int]]:
    """Handle The Lord of the Rings multi-movie trilogy post by generating 3 discrete JCF files."""
    films = [
        (
            "The Lord of the Rings: The Fellowship of the Ring",
            2001,
            ["fellowship of the ring", "fellowship"],
        ),
        ("The Lord of the Rings: The Two Towers", 2002, ["the two towers", "two towers"]),
        (
            "The Lord of the Rings: The Return of the King",
            2003,
            ["the return of the king", "return of the king"],
        ),
    ]

    results = []
    lines = plain_text.splitlines()

    # Split text into sections per film
    sections: Dict[str, List[str]] = {f[0]: [] for f in films}
    current_film = None

    for line in lines:
        lower_line = line.lower().strip()
        matched = False
        for film_title, _, keywords in films:
            if any(kw in lower_line for kw in keywords) and len(line) < 60:
                current_film = film_title
                matched = True
                break
        if matched:
            continue
        if current_film:
            sections[current_film].append(line)

    for film_title, year, _ in films:
        film_text = "\n".join(sections[film_title])
        cues = parse_post_into_cues(film_text)
        cues = merge_cues(cues)
        if cues:
            jcf_text = build_jcf_content(film_title, year, cues)
            fname = f"{sanitize_filename(film_title)} ({year}).jcf"
            out_file = output_dir / fname
            out_file.write_text(jcf_text, encoding="utf-8")
            results.append((fname, len(cues)))

    return results


class JcfProcessor:
    """Processes posts into JCF sidecar files."""

    def __init__(
        self,
        input_file: str = DEFAULT_INPUT_FILE,
        output_dir: str = DEFAULT_OUTPUT_DIR,
        merge_adjacent: bool = True,
    ):
        self.input_file = Path(input_file)
        self.output_dir = Path(output_dir)
        self.merge_adjacent = merge_adjacent

    def run(self) -> Dict[str, Any]:
        """Execute JCF conversion on all posts."""
        if not self.input_file.exists():
            raise FileNotFoundError(f"Input file not found: {self.input_file}")

        self.output_dir.mkdir(parents=True, exist_ok=True)

        with open(self.input_file, "r", encoding="utf-8") as f:
            posts = json.load(f)

        print(f"[*] Loaded {len(posts)} posts from {self.input_file}...")

        total_files = 0
        total_cues = 0
        category_counts: Dict[str, int] = {}
        skipped_posts: List[str] = []

        for p in posts:
            title = p.get("title", "")
            media_title = p.get("media_title", "").strip()
            year = p.get("year")
            slug = p.get("slug", "").lower()
            plain_text = p.get("plain_text", "")

            # Check if informational post
            if any(term in title.lower() or term in slug for term in NON_MOVIE_SLUGS_OR_TITLES):
                skipped_posts.append(title)
                continue

            # Check for multi-movie LOTR trilogy post
            if "lord of the rings (all 3" in title.lower():
                lotr_results = process_lord_of_the_rings_trilogy(plain_text, self.output_dir)
                for fname, cue_count in lotr_results:
                    total_files += 1
                    total_cues += cue_count
                continue

            # Fallback for known titles without year
            if not year:
                clean_lookup = media_title.lower().strip()
                if clean_lookup in KNOWN_MOVIE_YEARS:
                    media_title, year = KNOWN_MOVIE_YEARS[clean_lookup]
                else:
                    # Check for year in title string: e.g. "Dark City 1998"
                    y_match = re.search(r"\b(19\d{2}|20\d{2})\b", title)
                    if y_match:
                        year = int(y_match.group(1))

            cues = parse_post_into_cues(plain_text)
            if self.merge_adjacent:
                cues = merge_cues(cues)

            if not cues:
                skipped_posts.append(f"{title} (no timestamps found)")
                continue

            for c in cues:
                category_counts[c.category] = category_counts.get(c.category, 0) + 1

            jcf_content = build_jcf_content(
                title=media_title,
                year=year,
                cues=cues,
            )

            if year:
                filename = f"{sanitize_filename(media_title)} ({year}).jcf"
            else:
                filename = f"{sanitize_filename(media_title)}.jcf"

            out_path = self.output_dir / filename
            out_path.write_text(jcf_content, encoding="utf-8")
            total_files += 1
            total_cues += len(cues)

        summary = {
            "source_file": str(self.input_file),
            "output_directory": str(self.output_dir.resolve()),
            "total_jcf_files_written": total_files,
            "total_cues_generated": total_cues,
            "category_distribution": category_counts,
            "skipped_posts_count": len(skipped_posts),
        }

        # Save summary JSON
        summary_path = self.output_dir / "jcf_summary.json"
        with open(summary_path, "w", encoding="utf-8") as sf:
            json.dump(summary, sf, indent=2)

        print("\n" + "=" * 60)
        print(f"[✓] Successfully generated {total_files} discrete JCF files!")
        print(f"    - Total filter cues: {total_cues}")
        print(f"    - Categories: {json.dumps(category_counts)}")
        print(f"    - Output directory: {self.output_dir.resolve()}")
        print(f"    - Summary report: {summary_path}")
        print("=" * 60 + "\n")

        return summary


def main():
    parser = argparse.ArgumentParser(
        description="Convert TheTimestampDudes posts into discrete Jellyfin Content Filter (.jcf) files."
    )
    parser.add_argument(
        "--input",
        default=DEFAULT_INPUT_FILE,
        help=f"Input JSON path (default: {DEFAULT_INPUT_FILE})",
    )
    parser.add_argument(
        "--output-dir",
        default=DEFAULT_OUTPUT_DIR,
        help=f"Output directory for .jcf files (default: {DEFAULT_OUTPUT_DIR})",
    )
    parser.add_argument(
        "--no-merge",
        action="store_true",
        help="Do not merge overlapping/adjacent cues with identical categories",
    )

    args = parser.parse_args()

    processor = JcfProcessor(
        input_file=args.input,
        output_dir=args.output_dir,
        merge_adjacent=not args.no_merge,
    )
    processor.run()


if __name__ == "__main__":
    main()
