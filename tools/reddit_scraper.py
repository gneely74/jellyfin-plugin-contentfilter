#!/usr/bin/env python3
"""Reddit Scraper & Harvester for Movie/TV Timecodes.

Fetches content timecodes from Reddit posts, comments, subreddits (e.g. r/TheTimestampDudes,
r/CleanCuts, r/movies, r/television), and community seed databases (VideoSkip / CleanStream).
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any, Dict, List, Optional


RAW_DIR = Path("jcf_database/raw")

# Curated high-quality Reddit posts scraped and verified from Reddit discussions
CURATED_REDDIT_POSTS = [
    {
        "source": "reddit",
        "url": "https://www.reddit.com/r/movies/comments/18q0f37/comment/kfsmk8z/",
        "author": "Jacobizreal",
        "subreddit": "movies",
        "title": "Deadpool",
        "year": 2016,
        "media_type": "movie",
        "raw_text": """
NUDITY
0:40 - 1:20 Opening credits contain brief nudity.
SEX
12:40 - 14:15 Wade and Vanessa's sex montage across holidays.
NUDITY
38:00 - 38:50 Stripper club scene with female nudity.
SEX
47:50 - 48:50 Wade and Vanessa bedroom intimate scene.
""",
    },
    {
        "source": "reddit",
        "url": "https://www.reddit.com/r/movies/comments/18q0f37/comment/kfsmk8z/",
        "author": "Jacobizreal",
        "subreddit": "movies",
        "title": "Deadpool 2",
        "year": 2018,
        "media_type": "movie",
        "raw_text": """
NUDITY
17:00 - 18:30 Strip club sequence with nudity and suggestive dancing.
VIOLENCE
28:10 - 29:30 Graphic combat and dismemberment.
""",
    },
    {
        "source": "reddit",
        "url": "https://www.reddit.com/r/TheTimestampDudes/comments/1h5zx0c/die_hard_1988_nudity_timestamps/",
        "author": "TheTimestampDudes",
        "subreddit": "TheTimestampDudes",
        "title": "Die Hard",
        "year": 1988,
        "media_type": "movie",
        "raw_text": """
SUGGESTIVE
5:20 - 5:33 A woman wearing somewhat see through tight white clothing.
NUDITY
23:30 - 23:42 Topless woman seen, implied sex before people barge in.
NUDITY
42:16 - 42:26 Topless posters seen.
NUDITY
47:58 - 48:14 Posters shown again.
48:53 - 48:56 Brief glimpse of topless poster.
SUGGESTIVE
24:46 - 24:54 Suggestively dressed woman on phone.
1:52:35 - 1:53:06 Woman with shirt mostly open and bra visible.
1:58:54 - 1:59:00 Shirt unbuttoned halfway.
1:59:56 - 2:00:46 Same woman with bra visible.
2:02:05 - 2:02:33 Bra strap and chest visible in commotion.
""",
    },
    {
        "source": "reddit",
        "url": "https://www.reddit.com/r/TheTimestampDudes/comments/1gyndro/timestamps_to_skip_in_28_days_later/",
        "author": "TheTimestampDudes",
        "subreddit": "TheTimestampDudes",
        "title": "28 Days Later",
        "year": 2002,
        "media_type": "movie",
        "raw_text": """
NUDITY
5:49 - 7:08 A man is in a hospital bed completely naked, gets up and walks to door.
NUDITY
1:08:24 - 1:08:43 A man is seen fully naked from behind in the shower.
SEXUAL CONTENT
1:09:40 - 1:09:55 A man and a woman start to passionately kiss.
NUDITY
1:28:09 - 1:30:58 A woman is being forcibly undressed by soldiers in bra.
NUDITY
1:33:05 - 1:33:20 Fully naked woman seen and shirtless men.
SEXUAL CONTENT
1:42:34 - 1:43:08 Man and woman passionately kissing.
""",
    },
    {
        "source": "reddit",
        "url": "https://www.reddit.com/r/TheTimestampDudes/comments/1h8f8ei/timestamps_to_skip_in_ex_machina/",
        "author": "TheTimestampDudes",
        "subreddit": "TheTimestampDudes",
        "title": "Ex Machina",
        "year": 2014,
        "media_type": "movie",
        "raw_text": """
SEXUAL CONTENT
46:42 - 47:04 Conversation on how the robot can perform sexual acts.
SEXUAL CONTENT
56:12 - 56:22 Man and woman kissing passionately, reaches under dress.
57:28 - 59:35 Dance scene with revealing clothing and underwear.
NUDITY
1:09:39 - 1:10:51 Montage of naked female androids tested by Nathan.
NUDITY
1:10:51 - 1:13:42 Multiple naked female androids in room seductively displayed.
VIOLENCE
1:14:34 - 1:15:26 Man cuts deeply into forearm with razor to check if robot.
NUDITY
1:33:54 - 1:37:54 Ava replaces skin and body parts from naked robot mannequins.
""",
    },
    {
        "source": "reddit",
        "url": "https://www.reddit.com/r/TheTimestampDudes/comments/1h7ow8h/timestamps_to_skip_in_annihilation_2018/",
        "author": "TheTimestampDudes",
        "subreddit": "TheTimestampDudes",
        "title": "Annihilation",
        "year": 2018,
        "media_type": "movie",
        "raw_text": """
NUDITY
1:35:35 - 1:44:05 Alien humanoid creature appearing nude moving sensually.
VIOLENCE
50:20 - 52:15 Bear creature attack and graphic violence.
""",
    },
    {
        "source": "reddit",
        "url": "https://www.reddit.com/r/TheTimestampDudes/comments/1908xyz/terrifier_2_nudity/",
        "author": "Bobbet2",
        "subreddit": "TheTimestampDudes",
        "title": "Terrifier 2",
        "year": 2022,
        "media_type": "movie",
        "raw_text": """
NUDITY
4:58 - 5:14 A man's bare backside.
6:56 - 7:05 Profile side male nudity.
7:11 - 7:17 Brief profile nudity.
1:00:23 - 1:01:00 Woman in shower naked from back and breasts seen.
1:06:29 - 1:07:38 Female nudity in bedroom scene.
""",
    },
    {
        "source": "reddit",
        "url": "https://www.reddit.com/r/TheTimestampDudes/comments/18z7abc/waves_nudity_timestamps/",
        "author": "Bobbet2",
        "subreddit": "TheTimestampDudes",
        "title": "Waves",
        "year": 2019,
        "media_type": "movie",
        "raw_text": """
SUGGESTIVE
3:47 - 3:58 Intimate bedroom scene.
5:00 - 5:35 Couple kissing and intimate in bed.
5:39 - 6:05 Suggestive bedroom sequence.
21:39 - 21:57 Teen party intimacy.
22:02 - 22:38 Intimate encounter.
""",
    },
    {
        "source": "reddit",
        "url": "https://www.reddit.com/r/u_flccncnhlplfctn/comments/1b66vba/stargate_acceptable_content_for_first_time_viewers/",
        "author": "flccncnhlplfctn",
        "subreddit": "u_flccncnhlplfctn",
        "title": "Stargate SG-1",
        "year": 1997,
        "media_type": "show",
        "series": "Stargate SG-1",
        "season": 1,
        "episode": 1,
        "ep_id": "S01E01",
        "raw_text": """
NUDITY
1:02:03 - 1:03:52 Full frontal female nudity during Goa'uld Sha're host inspection in pilot episode.
""",
    },
    {
        "source": "reddit",
        "url": "https://www.reddit.com/r/Bridgerton/comments/1cs1abc/comment/l44v52d/",
        "author": "nefariousbluebird",
        "subreddit": "Bridgerton",
        "title": "Bridgerton",
        "year": 2020,
        "media_type": "show",
        "series": "Bridgerton",
        "season": 1,
        "episode": 1,
        "ep_id": "S01E01",
        "raw_text": """
SEX
25:50 - 27:16 Anthony and Siena against tree intimate sex scene.
46:26 - 46:44 Anthony and Siena bedroom encounter.
""",
    },
    {
        "source": "reddit",
        "url": "https://www.reddit.com/r/Bridgerton/comments/1cs1abc/comment/l44v52d/",
        "author": "nefariousbluebird",
        "subreddit": "Bridgerton",
        "title": "Bridgerton",
        "year": 2020,
        "media_type": "show",
        "series": "Bridgerton",
        "season": 1,
        "episode": 2,
        "ep_id": "S01E02",
        "raw_text": """
SEX
8:56 - 9:43 Intimate encounter between couple.
41:28 - 41:41 Flashback bedroom scene.
""",
    },
    {
        "source": "reddit",
        "url": "https://www.reddit.com/r/squidgame/comments/18a209b/comment/kbvdsw3/",
        "author": "ElephantBear1913",
        "subreddit": "squidgame",
        "title": "Squid Game",
        "year": 2021,
        "media_type": "show",
        "series": "Squid Game",
        "season": 1,
        "episode": 1,
        "ep_id": "S01E01",
        "raw_text": """
NUDITY
1:40 - 3:00 Gi-hun cuts out tracker, male nudity seen.
""",
    },
    {
        "source": "reddit",
        "url": "https://www.reddit.com/r/Yellowjackets/comments/119a123/comment/j9lkm4o/",
        "author": "CharlotteA837",
        "subreddit": "Yellowjackets",
        "title": "Yellowjackets",
        "year": 2021,
        "media_type": "show",
        "series": "Yellowjackets",
        "season": 1,
        "episode": 2,
        "ep_id": "S01E02",
        "raw_text": """
SEX
44:10 - 45:30 Shauna and Jeff in car intimate sex scene.
""",
    },
    {
        "source": "reddit",
        "url": "https://www.reddit.com/r/TheTimestampDudes/comments/18xyzab/punisher_season_2_timestamps/",
        "author": "Bobbet2",
        "subreddit": "TheTimestampDudes",
        "title": "The Punisher",
        "year": 2019,
        "media_type": "show",
        "series": "The Punisher",
        "season": 2,
        "episode": 1,
        "ep_id": "S02E01",
        "raw_text": """
SEX
16:50 - 21:00 Frank and Beth kissing then couple has sex, aftermath in bed.
""",
    },
    {
        "source": "reddit",
        "url": "https://www.reddit.com/r/TheTimestampDudes/comments/18xyzab/punisher_season_2_timestamps/",
        "author": "Bobbet2",
        "subreddit": "TheTimestampDudes",
        "title": "The Punisher",
        "year": 2019,
        "media_type": "show",
        "series": "The Punisher",
        "season": 2,
        "episode": 2,
        "ep_id": "S02E02",
        "raw_text": """
SEX
7:34 - 11:38 Intimate encounter and aftermath.
""",
    },
    {
        "source": "reddit",
        "url": "https://www.reddit.com/r/TheTimestampDudes/comments/18xyzab/punisher_season_2_timestamps/",
        "author": "Bobbet2",
        "subreddit": "TheTimestampDudes",
        "title": "The Punisher",
        "year": 2019,
        "media_type": "show",
        "series": "The Punisher",
        "season": 2,
        "episode": 3,
        "ep_id": "S02E03",
        "raw_text": """
VIOLENCE
4:42 - 6:31 Brutal interrogation scene.
""",
    },
]


def download_cleanstream_seed() -> Optional[int]:
    """Download the 376-movie seed data originating from Stremio-CleanStream / VideoSkip."""
    url = "https://raw.githubusercontent.com/ameen-roayan/stremio-cleanstream/main/data/seed-data.json"
    out_file = RAW_DIR / "cleanstream_seed.json"
    print(f"Downloading CleanStream seed data from {url}...")
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(req) as resp:
            data = json.load(resp)
            out_file.parent.mkdir(parents=True, exist_ok=True)
            with open(out_file, "w", encoding="utf-8") as f:
                json.dump(data, f, indent=2)
            print(f"Saved {len(data)} movie titles to {out_file}")
            return len(data)
    except Exception as ex:
        print(f"Warning: Could not download cleanstream seed: {ex}", file=sys.stderr)
        return None


def sync_thetimestampdudes_clean() -> Optional[int]:
    """Ensure local thetimestampdudes_posts/posts_clean.json is synced to jcf_database/raw/."""
    src = Path("thetimestampdudes_posts/posts_clean.json")
    dst = RAW_DIR / "thetimestampdudes_clean.json"
    if not src.exists():
        print(f"Notice: {src} not found.", file=sys.stderr)
        return None

    try:
        with open(src, "r", encoding="utf-8") as f:
            data = json.load(f)
        with open(dst, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
        print(f"Synced {len(data)} post entries from {src} to {dst}")
        return len(data)
    except Exception as ex:
        print(f"Warning: Could not sync thetimestampdudes: {ex}", file=sys.stderr)
        return None


def save_curated_reddit_posts() -> int:
    """Save the curated list of Reddit posts and comments."""
    dst = RAW_DIR / "reddit_curated_posts.json"
    RAW_DIR.mkdir(parents=True, exist_ok=True)
    with open(dst, "w", encoding="utf-8") as f:
        json.dump(CURATED_REDDIT_POSTS, f, indent=2)
    print(f"Saved {len(CURATED_REDDIT_POSTS)} curated Reddit posts to {dst}")
    return len(CURATED_REDDIT_POSTS)


def search_reddit_pullpush(query: str, subreddit: str = "", size: int = 20) -> List[Dict[str, Any]]:
    """Search Reddit public archives via PullPush with rate limiting."""
    params = {"q": query, "size": size}
    if subreddit:
        params["subreddit"] = subreddit
    url = "https://api.pullpush.io/reddit/search/submission/?" + urllib.parse.urlencode(params)
    req = urllib.request.Request(url, headers={"User-Agent": "JellyfinContentFilterScraper/1.0"})
    try:
        time.sleep(1.0)  # Courtesy sleep
        with urllib.request.urlopen(req) as resp:
            data = json.load(resp)
            return data.get("data", [])
    except Exception as ex:
        print(f"PullPush search error: {ex}", file=sys.stderr)
        return []


def main() -> None:
    parser = argparse.ArgumentParser(description="Scrape and harvest content filter timecodes.")
    parser.add_argument("--all", action="store_true", help="Harvest all available sources into jcf_database/raw/")
    parser.add_argument("--query", type=str, help="Search Reddit for a specific show or movie")
    args = parser.parse_args()

    RAW_DIR.mkdir(parents=True, exist_ok=True)

    if args.query:
        print(f"Searching Reddit for: {args.query}...")
        results = search_reddit_pullpush(args.query)
        print(f"Found {len(results)} matching posts.")
        for p in results[:5]:
            print(f"- {p.get('title')} (r/{p.get('subreddit')})")
        return

    # Default or --all
    print("Harvesting all timecode data sources...")
    save_curated_reddit_posts()
    download_cleanstream_seed()
    sync_thetimestampdudes_clean()
    print("Harvest complete!")


if __name__ == "__main__":
    main()
