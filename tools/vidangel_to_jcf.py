#!/usr/bin/env python3
"""VidAngel to Jellyfin Content Filter (.jcf) Converter.

Searches VidAngel's public catalog API, retrieves filter timestamps and category tags
for movies and television episodes, maps them to canonical Jellyfin ContentFilter
categories, and exports standard WEBVTT JCF sidecar files.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

# Base API endpoint for VidAngel public REST services
VIDANGEL_BASE_API = "https://api.vidangel.com/api"
DEFAULT_USER_AGENT = (
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
    "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
)


# ==============================================================================
# Data Structures
# ==============================================================================


@dataclass
class ParsedCue:
    """Represents a single content filter cue formatted for Jellyfin JCF.

    Attributes:
        start_ms: Cue start timestamp in integer milliseconds.
        end_ms: Cue end timestamp in integer milliseconds.
        start_str: Formatted WebVTT timestamp (HH:MM:SS.mmm).
        end_str: Formatted WebVTT timestamp (HH:MM:SS.mmm).
        category: Canonical Jellyfin category identifier (e.g. Violence.Graphic).
        channel: Target stream channel ('video', 'audio', or 'both').
        action: Filter enforcement action ('skip' or 'mute').
        description: Brief explanatory text describing the scene or dialogue.
    """

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
    """Represents a structured .jcf sidecar document conforming to WEBVTT JCF.

    Attributes:
        title: Clean media title.
        year: Optional release year.
        imdb_id: Optional IMDb identifier (e.g. tt0172495).
        source: Provenance URL or descriptor.
        cues: List of filter cues ordered chronologically.
    """

    title: str
    year: Optional[str] = None
    imdb_id: Optional[str] = None
    source: Optional[str] = None
    cues: List[ParsedCue] = field(default_factory=list)

    def to_jcf(self) -> str:
        """Serializes the document into valid WEBVTT JCF formatted text.

        Returns:
            A string containing the complete WebVTT JCF file contents.
        """
        lines = ["WEBVTT JCF", "", "NOTE", f"TITLE {self.title}"]
        if self.year:
            lines.append(f"YEAR {self.year}")
        if self.imdb_id:
            lines.append(f"IMDB {self.imdb_id}")
        if self.source:
            lines.append(f"SOURCE {self.source}")
        lines.append("")

        sorted_cues = sorted(self.cues, key=lambda c: c.start_ms)
        for cue in sorted_cues:
            lines.append(f"{cue.start_str} --> {cue.end_str}")
            lines.append(f"category: {cue.category}")
            lines.append(f"channel: {cue.channel}")
            lines.append(f"action: {cue.action}")
            if cue.description:
                # Normalize newlines and excess whitespace in descriptions
                clean_desc = re.sub(r"\s+", " ", cue.description).strip()
                lines.append(f"description: {clean_desc}")
            lines.append("")

        return "\n".join(lines).rstrip() + "\n"


@dataclass
class VidAngelTag:
    """Represents an unmapped filter tag directly from the VidAngel API.

    Attributes:
        id: Unique tag identifier.
        tag_set_id: Parent tag set identifier.
        category_id: VidAngel internal category identifier.
        description: Scene description or profanity text.
        tag_type: Filter modality ('audio' or 'audiovisual').
        start_approx: Start timestamp in seconds.
        end_approx: End timestamp in seconds.
    """

    id: int
    tag_set_id: int
    category_id: int
    description: str
    tag_type: str
    start_approx: int
    end_approx: int


# ==============================================================================
# Timecode Helper Utilities
# ==============================================================================


def ms_to_timestamp(ms: int) -> str:
    """Converts a millisecond integer into an HH:MM:SS.mmm WebVTT timestamp string.

    Args:
        ms: Milliseconds from start of playback (clamped at 0).

    Returns:
        Formatted string in standard HH:MM:SS.mmm format.
    """
    if ms < 0:
        ms = 0
    total_seconds = ms // 1000
    milli = ms % 1000
    hours = total_seconds // 3600
    minutes = (total_seconds % 3600) // 60
    seconds = total_seconds % 60
    return f"{hours:02d}:{minutes:02d}:{seconds:02d}.{milli:03d}"


# ==============================================================================
# Category & Channel Mapping
# ==============================================================================

# Mapping from VidAngel primary category IDs to Jellyfin categories and defaults
PRIMARY_CATEGORY_MAP: Dict[int, Tuple[str, str, str]] = {
    18: ("Language.GeneralProfanity", "audio", "mute"),
    20: ("Language.Blasphemy", "audio", "mute"),
    23: ("Language.RacialAndBigotedSlurs", "audio", "mute"),
    22: ("SexualReferences.ExplicitWords", "audio", "mute"),
    2: ("SexAndNudity.PartialNudity", "video", "skip"),
    6: ("SexAndNudity.Graphic", "video", "skip"),
    9: ("SexAndNudity.PhysicalIntimacy", "video", "skip"),
    14: ("Violence.Moderate", "video", "skip"),
    15: ("Violence.Graphic", "video", "skip"),
    16: ("Violence.Gore", "video", "skip"),
    4: ("Substances.Alcohol", "video", "skip"),
    27: ("Medical.Events", "both", "skip"),
    24: ("Structural.Credits", "both", "skip"),
}


def map_vidangel_tag(
    tag: VidAngelTag,
    parent_category_id: Optional[int] = None,
) -> Tuple[str, str, str]:
    """Maps a VidAngel tag and its descriptive metadata to Jellyfin canonical taxonomy.

    Inspects both the category identifier and descriptive keywords to select the
    most accurate category in FilterDictionary.cs.

    Args:
        tag: The raw VidAngelTag instance.
        parent_category_id: Optional parent category ID resolved from filter-preview.

    Returns:
        A tuple of (category, channel, action).
    """
    effective_cat_id = parent_category_id or tag.category_id
    desc_lower = (tag.description or "").lower().strip()
    is_audio = tag.tag_type == "audio"

    # 1. Structural / Credits Handling
    if effective_cat_id == 24 or "closing credits" in desc_lower or "opening credits" in desc_lower:
        return "Structural.Credits", "both", "skip"
    if "recap" in desc_lower or "intro" in desc_lower or "previously on" in desc_lower:
        return "Structural.IntroRecap", "both", "skip"

    # 2. Profanity, Blasphemy, & Slurs
    if effective_cat_id == 20 or any(w in desc_lower for w in ["jesus", "christ", "god", "lord", "g*d", "j*sus"]):
        return "Language.Blasphemy", "audio", "mute"
    if effective_cat_id == 23 or any(w in desc_lower for w in ["slur", "racial", "racist"]):
        return "Language.RacialAndBigotedSlurs", "audio", "mute"
    if effective_cat_id == 18 or (is_audio and any(w in desc_lower for w in ["sh*t", "f*ck", "b*st*rd", "*ss", "d*mn", "b*tch", "hell", "crap"])):
        return "Language.GeneralProfanity", "audio", "mute"

    # 3. Sexual References (Dialogue vs Explicit Words)
    if effective_cat_id == 22:
        if is_audio:
            return "SexualReferences.ExplicitWords", "audio", "mute"
        if any(w in desc_lower for w in ["gesture", "finger"]):
            return "SexualReferences.Visuals", "video", "skip"
        return "SexualReferences.ContextualDialogue", "video", "skip"

    # 4. Sex & Nudity Nuances
    if effective_cat_id in (2, 6, 9):
        # Sexual Assault / Non-consensual
        if any(w in desc_lower for w in ["assault", "rape", "molest", "force"]):
            return "SexAndNudity.SexualAssault", "video", "skip"
        # Full Nudity
        if any(w in desc_lower for w in ["frontal nudity", "naked", "bare breast", "bare butt", "unclad"]):
            return "SexAndNudity.FullNudity", "video", "skip"
        # Physical Intimacy
        if effective_cat_id == 9 or any(w in desc_lower for w in ["kiss", "making out", "caress"]):
            return "SexAndNudity.PhysicalIntimacy", "video", "skip"
        # Implied Sex vs Graphic Sex
        if effective_cat_id == 6:
            if any(w in desc_lower for w in ["intercourse", "oral sex", "masturbat", "penetrat"]):
                return "SexAndNudity.Graphic", "video", "skip"
            return "SexAndNudity.ImpliedSex", "video", "skip"
        # Partial Nudity / Mild Immodesty
        if any(w in desc_lower for w in ["bikini", "swimwear", "beach", "shirtless", "cleavage", "underwear", "bra", "panties"]):
            return "SexAndNudity.PartialNudity", "video", "skip"
        if any(w in desc_lower for w in ["statue", "painting", "mild"]):
            return "SexAndNudity.Mild", "video", "skip"
        return "SexAndNudity.PartialNudity", "video", "skip"

    # 5. Violence Categories
    if effective_cat_id in (14, 15, 16):
        if effective_cat_id == 16 or any(w in desc_lower for w in ["gore", "decapitat", "severed", "dismember", "intestine", "organ"]):
            return "Violence.Gore", "video", "skip"
        if any(w in desc_lower for w in ["jump scare", "startle", "creature reveal"]):
            return "Violence.JumpScares", "video", "skip"
        if any(w in desc_lower for w in ["torture", "suicide", "corpse", "disturbing"]):
            return "Violence.Disturbing", "video", "skip"
        if effective_cat_id == 15 or any(w in desc_lower for w in ["graphic", "stabbing", "shot in the head", "visceral"]):
            return "Violence.Graphic", "video", "skip"
        if any(w in desc_lower for w in ["slap", "comic", "playful"]):
            return "Violence.Mild", "video", "skip"
        return "Violence.Moderate", "video", "skip"

    # 6. Substances
    if effective_cat_id == 4:
        if any(w in desc_lower for w in ["cigarette", "cigar", "tobacco", "smoke", "vape", "pipe"]):
            return "Substances.Tobacco", "video", "skip"
        if any(w in desc_lower for w in ["drug", "cocaine", "heroin", "marijuana", "weed", "narcotic", "pill", "inject"]):
            return "Substances.IllegalDrugs", "video", "skip"
        return "Substances.Alcohol", "video", "skip"

    # 7. Medical & Body Functions
    if effective_cat_id == 27:
        if any(w in desc_lower for w in ["vomit", "barf", "throw up", "fart", "urine", "poop", "toilet"]):
            return "Medical.BodilyFunctions", "both", "skip"
        return "Medical.Events", "both", "skip"

    # Default fallback according to dictionary or tag modality
    if effective_cat_id in PRIMARY_CATEGORY_MAP:
        return PRIMARY_CATEGORY_MAP[effective_cat_id]

    if is_audio:
        return "Language.GeneralProfanity", "audio", "mute"
    return "Violence.Moderate", "video", "skip"


# ==============================================================================
# VidAngel API Client
# ==============================================================================


class VidAngelClient:
    """HTTP Client for querying VidAngel's public catalog and filter APIs.

    Provides synchronous methods to search content titles, inspect platform offerings,
    and download tagged filter sets.
    """

    def __init__(self, base_url: str = VIDANGEL_BASE_API, user_agent: str = DEFAULT_USER_AGENT) -> None:
        """Initializes the API client with base URL and browser headers.

        Args:
            base_url: The root URL for the VidAngel REST API.
            user_agent: User-Agent string to include on all requests.
        """
        self.base_url = base_url.rstrip("/")
        self.user_agent = user_agent

    def _get_json(self, path: str, params: Optional[Dict[str, Any]] = None) -> Any:
        """Executes an HTTP GET request against the VidAngel API and parses JSON.

        Args:
            path: Relative API path (e.g. 'content/search/').
            params: Optional dictionary of query parameters.

        Returns:
            Parsed JSON data (dict or list).

        Raises:
            RuntimeError: If the HTTP request fails or returns an error status.
        """
        # Ensure path retains trailing slash to avoid 301 redirects
        clean_path = path.strip("/") + "/"
        url = f"{self.base_url}/{clean_path}"
        if params:
            query_string = urllib.parse.urlencode(params)
            url = f"{url}?{query_string}"

        req = urllib.request.Request(
            url,
            headers={
                "User-Agent": self.user_agent,
                "Accept": "application/json",
            },
        )

        try:
            with urllib.request.urlopen(req, timeout=15) as resp:
                data = resp.read().decode("utf-8")
                return json.loads(data)
        except urllib.error.HTTPError as e:
            raise RuntimeError(f"HTTP {e.code} querying {url}: {e.reason}") from e
        except urllib.error.URLError as e:
            raise RuntimeError(f"Network error connecting to {url}: {e.reason}") from e

    def search(self, query: str) -> List[Dict[str, Any]]:
        """Searches VidAngel for movies and TV series matching the search term.

        Args:
            query: The title or keywords to search.

        Returns:
            A list of dictionary objects representing title results.

        Raises:
            RuntimeError: If the search request fails.
        """
        data = self._get_json("content/search", {"q": query})
        return data.get("titles", [])

    def get_movie(self, slug: str) -> Dict[str, Any]:
        """Fetches detailed movie metadata including all available streaming offerings.

        Args:
            slug: The unique VidAngel title slug (e.g. 'gladiator-c2f2e').

        Returns:
            Dictionary containing movie information and offerings.

        Raises:
            ValueError: If no movie matches the slug.
            RuntimeError: If the network request fails.
        """
        data = self._get_json("content/v2/movies", {"slug": slug})
        results = data.get("results", [])
        if not results:
            raise ValueError(f"No movie found with slug '{slug}'")
        return results[0]

    def get_show(self, slug: str) -> Dict[str, Any]:
        """Fetches detailed TV series metadata including seasons and episodes.

        Args:
            slug: The unique VidAngel title slug (e.g. 'stranger-things-d221b').

        Returns:
            Dictionary containing show, season, and episode details.

        Raises:
            ValueError: If no television series matches the slug.
            RuntimeError: If the network request fails.
        """
        data = self._get_json("content/v2/shows", {"slug": slug})
        results = data.get("results", [])
        if not results:
            raise ValueError(f"No show found with slug '{slug}'")
        return results[0]

    def get_tag_set(self, tag_set_id: int) -> List[VidAngelTag]:
        """Retrieves raw filter tags and timestamps for a specific tag set ID.

        Args:
            tag_set_id: The numerical ID of the tag set.

        Returns:
            A list of parsed VidAngelTag instances.

        Raises:
            RuntimeError: If the request fails or tag set does not exist.
        """
        data = self._get_json(f"tag-sets/{tag_set_id}")
        raw_tags = data.get("tags", [])
        tags: List[VidAngelTag] = []
        for t in raw_tags:
            tags.append(
                VidAngelTag(
                    id=t.get("id", 0),
                    tag_set_id=t.get("tag_set_id", tag_set_id),
                    category_id=t.get("category_id", 0),
                    description=t.get("description", ""),
                    tag_type=t.get("type", "audiovisual"),
                    start_approx=int(t.get("start_approx", 0)),
                    end_approx=int(t.get("end_approx", 0)),
                )
            )
        return tags

    def get_category_hierarchy(self, tag_set_id: int) -> Dict[int, int]:
        """Fetches category hierarchy and maps each child category ID to its top-level parent ID.

        Args:
            tag_set_id: The numerical ID of the tag set.

        Returns:
            Dictionary mapping child_category_id -> parent_category_id.
        """
        child_to_parent: Dict[int, int] = {}
        try:
            data = self._get_json(f"bff/filter-preview/{tag_set_id}")
            for cat in data.get("categories", []):
                parent_id = cat.get("id")
                for child_id in cat.get("child_ids", []):
                    child_to_parent[child_id] = parent_id
        except Exception:
            # Fall back gracefully if filter-preview endpoint is unavailable
            pass
        return child_to_parent


# ==============================================================================
# Filter Conversion Logic
# ==============================================================================


def convert_vidangel_tags_to_cues(
    tags: List[VidAngelTag],
    child_to_parent_categories: Optional[Dict[int, int]] = None,
    time_offset_seconds: float = 0.0,
    min_cue_duration_seconds: float = 1.0,
    include_credits: bool = True,
) -> List[ParsedCue]:
    """Converts a collection of VidAngel tags into standardized ParsedCue objects.

    Applies time offsets, adjusts zero-duration cues to a configurable minimum duration,
    filters optional categories, and maps category IDs.

    Args:
        tags: Raw tags fetched from VidAngel.
        child_to_parent_categories: Category ID inheritance dictionary.
        time_offset_seconds: Optional offset to add/subtract (in seconds) for sync.
        min_cue_duration_seconds: Duration assigned when start_approx == end_approx.
        include_credits: Flag indicating whether opening/closing credits are included.

    Returns:
        List of formatted ParsedCue instances sorted by start time.
    """
    cues: List[ParsedCue] = []
    child_map = child_to_parent_categories or {}
    offset_ms = int(time_offset_seconds * 1000)
    min_dur_ms = int(min_cue_duration_seconds * 1000)

    for tag in tags:
        parent_id = child_map.get(tag.category_id, tag.category_id)
        category, channel, action = map_vidangel_tag(tag, parent_category_id=parent_id)

        # Skip credits if explicitly disabled
        if not include_credits and category in ("Structural.Credits", "Structural.IntroRecap"):
            continue

        # Calculate millisecond bounds with offset and zero-floor clamping
        raw_start_ms = tag.start_approx * 1000 + offset_ms
        raw_end_ms = tag.end_approx * 1000 + offset_ms

        start_ms = max(0, raw_start_ms)
        end_ms = max(0, raw_end_ms)

        # Ensure instantaneous / point markers span at least min_dur_ms
        if end_ms <= start_ms:
            end_ms = start_ms + min_dur_ms

        cues.append(
            ParsedCue(
                start_ms=start_ms,
                end_ms=end_ms,
                start_str=ms_to_timestamp(start_ms),
                end_str=ms_to_timestamp(end_ms),
                category=category,
                channel=channel,
                action=action,
                description=tag.description,
            )
        )

    # Sort cues chronologically by start timestamp
    return sorted(cues, key=lambda c: c.start_ms)


def select_best_offering(offerings: List[Dict[str, Any]], preferred_platform: Optional[str] = None) -> Dict[str, Any]:
    """Selects the most suitable streaming offering from a list of title offerings.

    Args:
        offerings: List of offering dictionaries.
        preferred_platform: Specific platform slug (e.g. 'amazon', 'netflix', 'paramount').

    Returns:
        The selected offering dictionary.

    Raises:
        ValueError: If no valid offering with a tag_set_id is found.
    """
    valid = [o for o in offerings if o.get("tag_set_id")]
    if not valid:
        raise ValueError("No offerings found containing valid tag_set_id")

    if preferred_platform:
        plat_lower = preferred_platform.lower().strip()
        # Direct catalog_id match (e.g. 'amazon', 'netflix')
        for o in valid:
            catalog = (o.get("catalog_id") or "").lower()
            if plat_lower in catalog:
                return o

    # Default fallback: prefer subscription services over rental/purchase if available
    for o in valid:
        if o.get("type") == "SUBSCRIPTION":
            return o

    return valid[0]


# ==============================================================================
# CLI Commands
# ==============================================================================


def cmd_search(client: VidAngelClient, args: argparse.Namespace) -> int:
    """Executes the search subcommand and displays formatted title results.

    Args:
        client: The VidAngelClient instance.
        args: Parsed command line arguments.

    Returns:
        Exit code (0 on success).
    """
    query = " ".join(args.query).strip()
    if not query:
        print("Error: Search query cannot be empty.", file=sys.stderr)
        return 1

    print(f"Searching VidAngel for '{query}'...")
    results = client.search(query)

    if not results:
        print(f"No results found matching '{query}'.")
        return 0

    print(f"\nFound {len(results)} title(s):\n")
    print(f"{'TYPE':<7} | {'YEAR':<5} | {'TAGS':<5} | {'TITLE':<32} | {'SLUG'}")
    print("-" * 85)

    for item in results:
        media_type = item.get("type", "unknown").upper()
        title = item.get("title", "Unknown")
        year = str(item.get("year", ""))
        slug = item.get("slug", "")
        tags = str(item.get("tag_count", 0))
        print(f"{media_type:<7} | {year:<5} | {tags:<5} | {title[:32]:<32} | {slug}")

    print("\nTo download, run:")
    print("  python tools/vidangel_to_jcf.py download <SLUG>")
    return 0


def cmd_info(client: VidAngelClient, args: argparse.Namespace) -> int:
    """Displays detailed metadata, available platform cuts, and seasons for a title.

    Args:
        client: The VidAngelClient instance.
        args: Parsed command line arguments.

    Returns:
        Exit code (0 on success).
    """
    slug = args.slug.strip()
    print(f"Fetching metadata for '{slug}'...")

    try:
        movie = client.get_movie(slug)
        title = movie.get("title")
        year = movie.get("year")
        rating = movie.get("rating")
        tag_count = movie.get("tag_count")
        offerings = movie.get("offerings", [])

        print(f"\nTitle:       {title} ({year})")
        print(f"Rating:      {rating}")
        print(f"Total Tags:  {tag_count}")
        print(f"Type:        Movie")
        print("\nAvailable Platform Offerings:")
        for off in offerings:
            cat = off.get("catalog_id")
            off_type = off.get("type")
            ts_id = off.get("tag_set_id")
            name = off.get("watching_with")
            print(f"  - [{cat}] {off_type} (tag_set_id: {ts_id}) -> {name}")
        return 0
    except ValueError:
        pass

    try:
        show = client.get_show(slug)
        title = show.get("title")
        year = show.get("year")
        seasons = show.get("seasons", [])
        print(f"\nTitle:       {title} ({year})")
        print(f"Type:        TV Series")
        print(f"Seasons:     {len(seasons)}")
        for s in seasons:
            s_num = s.get("number") if s.get("number") is not None else s.get("season_number")
            episodes = s.get("episodes", [])
            print(f"  Season {s_num}: {len(episodes)} episode(s)")
        return 0
    except ValueError:
        print(f"Error: Slug '{slug}' not found as movie or show.", file=sys.stderr)
        return 1


def cmd_download(client: VidAngelClient, args: argparse.Namespace) -> int:
    """Downloads filter tags from VidAngel and exports a valid .jcf sidecar file.

    Args:
        client: The VidAngelClient instance.
        args: Parsed command line arguments.

    Returns:
        Exit code (0 on success).
    """
    slug = args.slug.strip()
    is_show = args.season is not None or args.episode is not None

    if is_show:
        if args.season is None or args.episode is None:
            print("Error: For TV shows, both --season and --episode must be specified.", file=sys.stderr)
            return 1

    print(f"Resolving '{slug}' on VidAngel...")
    tag_set_id = None
    media_title = ""
    media_year = None
    imdb_id = None

    if is_show:
        show = client.get_show(slug)
        media_title = show.get("title", slug)
        media_year = str(show.get("year", "")) or None
        imdb_id = show.get("imdb_id")
        target_s = args.season
        target_e = args.episode

        # Locate specific season and episode
        ep_match = None
        for s in show.get("seasons", []):
            s_num = s.get("number") if s.get("number") is not None else s.get("season_number")
            if s_num == target_s:
                for ep in s.get("episodes", []):
                    ep_num = ep.get("episode_number") if ep.get("episode_number") is not None else ep.get("number")
                    if ep_num == target_e:
                        ep_match = ep
                        break

        if not ep_match:
            print(f"Error: Could not find Season {target_s} Episode {target_e} in '{slug}'.", file=sys.stderr)
            return 1

        ep_title = ep_match.get("title", f"Episode {target_e}")
        media_title = f"{media_title} - S{target_s:02d}E{target_e:02d} - {ep_title}"
        chosen_offering = select_best_offering(ep_match.get("offerings", []), args.platform)
        tag_set_id = chosen_offering.get("tag_set_id")
        print(f"Selected offering: {chosen_offering.get('watching_with')} (tag_set_id: {tag_set_id})")
    else:
        try:
            movie = client.get_movie(slug)
            media_title = movie.get("title", slug)
            media_year = str(movie.get("year", "")) or None
            imdb_id = movie.get("imdb_id")
            chosen_offering = select_best_offering(movie.get("offerings", []), args.platform)
            tag_set_id = chosen_offering.get("tag_set_id")
            print(f"Selected offering: {chosen_offering.get('watching_with')} (tag_set_id: {tag_set_id})")
        except ValueError:
            # If not a movie, try detecting if it's a show with no episode specified
            try:
                client.get_show(slug)
                print(f"Error: '{slug}' is a TV show. Please specify --season and --episode.", file=sys.stderr)
                return 1
            except ValueError:
                print(f"Error: Could not find title matching '{slug}'.", file=sys.stderr)
                return 1

    if not tag_set_id:
        print("Error: No valid tag_set_id found for this title/offering.", file=sys.stderr)
        return 1

    print(f"Downloading tags for tag_set_id {tag_set_id}...")
    tags = client.get_tag_set(tag_set_id)
    print(f"Retrieved {len(tags)} tag(s). Fetching category structure...")
    child_to_parent = client.get_category_hierarchy(tag_set_id)

    # Convert tags to cues
    cues = convert_vidangel_tags_to_cues(
        tags=tags,
        child_to_parent_categories=child_to_parent,
        time_offset_seconds=args.offset,
        min_cue_duration_seconds=args.min_duration,
        include_credits=not args.no_credits,
    )

    doc = JcfDocument(
        title=media_title,
        year=media_year,
        imdb_id=imdb_id,
        source=f"https://www.vidangel.com/ (tag_set_id: {tag_set_id})",
        cues=cues,
    )

    # Determine destination file path
    if args.out:
        out_path = Path(args.out)
        if out_path.is_dir():
            stem = f"{media_title} ({media_year})" if media_year else media_title
            clean_stem = re.sub(r'[\\/*?:"<>|]', "", stem)
            out_file = out_path / f"{clean_stem}.jcf"
        else:
            out_file = out_path
    else:
        stem = f"{media_title} ({media_year})" if media_year else media_title
        clean_stem = re.sub(r'[\\/*?:"<>|]', "", stem)
        out_file = Path(f"{clean_stem}.jcf")

    out_file.parent.mkdir(parents=True, exist_ok=True)
    out_file.write_text(doc.to_jcf(), encoding="utf-8")
    print(f"Successfully generated {len(cues)} filter cue(s) -> {out_file.resolve()}")
    return 0


# ==============================================================================
# Main Entry Point
# ==============================================================================


def main() -> int:
    """Parses command-line arguments and delegates to the appropriate subcommand.

    Returns:
        Process exit code (0 on success).
    """
    parser = argparse.ArgumentParser(
        description="Search VidAngel and download filter timestamps into Jellyfin .jcf sidecar files."
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    # Subcommand: search
    p_search = subparsers.add_parser("search", help="Search VidAngel catalog for movies or TV series")
    p_search.add_argument("query", nargs="+", help="Title to search for")

    # Subcommand: info
    p_info = subparsers.add_parser("info", help="View metadata, seasons, and available streaming cuts for a slug")
    p_info.add_argument("slug", help="VidAngel title slug (from search results)")

    # Subcommand: download
    p_dl = subparsers.add_parser("download", help="Download filter tags and export .jcf file")
    p_dl.add_argument("slug", help="VidAngel title slug")
    p_dl.add_argument(
        "--platform",
        "-p",
        help="Preferred streaming service (e.g. amazon, netflix, paramount, appletv)",
        default=None,
    )
    p_dl.add_argument("--season", "-s", type=int, help="Season number for TV shows", default=None)
    p_dl.add_argument("--episode", "-e", type=int, help="Episode number for TV shows", default=None)
    p_dl.add_argument(
        "--offset",
        type=float,
        default=0.0,
        help="Time offset in seconds (+/-) to shift timestamps for local video sync (e.g. +8.5 or -5.0)",
    )
    p_dl.add_argument(
        "--min-duration",
        type=float,
        default=1.0,
        help="Minimum duration in seconds for point-in-time tags (default: 1.0)",
    )
    p_dl.add_argument(
        "--no-credits",
        action="store_true",
        help="Exclude opening and closing credit cues from generated JCF",
    )
    p_dl.add_argument(
        "--out",
        "-o",
        help="Output .jcf file path or directory (defaults to '<Title> (<Year>).jcf')",
        default=None,
    )

    args = parser.parse_args()
    client = VidAngelClient()

    if args.command == "search":
        return cmd_search(client, args)
    if args.command == "info":
        return cmd_info(client, args)
    if args.command == "download":
        return cmd_download(client, args)

    return 0


if __name__ == "__main__":
    sys.exit(main())
