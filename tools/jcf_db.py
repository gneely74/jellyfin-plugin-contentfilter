#!/usr/bin/env python3
"""Unified JCF Content Filter Database Builder and CLI.

Builds, indexes, searches, and exports Jellyfin Content Filter (.jcf) files
harvested from Reddit and community sources.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sqlite3
import sys
from dataclasses import asdict
from pathlib import Path
from typing import Any, Dict, List, Optional, Set, Tuple

# Enable running directly from tools/ or root
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import tools.reddit_to_jcf as rj


DATABASE_DIR = Path("jcf_database")
MOVIES_DIR = DATABASE_DIR / "movies"
SHOWS_DIR = DATABASE_DIR / "shows"
RAW_DIR = DATABASE_DIR / "raw"
CATALOG_DB = DATABASE_DIR / "catalog.db"
CATALOG_JSON = DATABASE_DIR / "catalog.json"

# Known release years for movie titles where year was omitted from heading
KNOWN_MOVIE_YEARS: Dict[str, Tuple[str, int]] = {
    "donnie darko (theatrical cut)": ("Donnie Darko (Theatrical Cut)", 2001),
    "donnie darko (directors cut)": ("Donnie Darko (Director's Cut)", 2001),
    "the shining (theatrical cut)": ("The Shining (Theatrical Cut)", 1980),
    "blade runner (final cut)": ("Blade Runner (Final Cut)", 1982),
    "limitless (theatrical version)": ("Limitless (Theatrical Version)", 2011),
    "snowpiercer (2013_2014)": ("Snowpiercer", 2013),
    "dark city (directors cut) 1998": ("Dark City (Director's Cut)", 1998),
    "l.a confidential": ("L.A. Confidential", 1997),
    "the lord of the rings (all 3 trilogy movies)": ("The Lord of the Rings", 2001),
}


def sanitize_filename(name: str) -> str:
    """Sanitize string for safe filenames."""
    s = re.sub(r'[\\/*?:"<>|]', "_", name)
    s = re.sub(r"\s+", " ", s).strip(". _")
    return s or "unnamed"


def init_sqlite(db_path: Path) -> sqlite3.Connection:
    """Initialize SQLite catalog schema."""
    conn = sqlite3.connect(db_path)
    cur = conn.cursor()
    cur.execute("""
    CREATE TABLE IF NOT EXISTS titles (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        title TEXT NOT NULL,
        year INTEGER,
        imdb_id TEXT,
        media_type TEXT NOT NULL,
        series_name TEXT,
        season INTEGER,
        episode INTEGER,
        jcf_path TEXT NOT NULL UNIQUE,
        cue_count INTEGER NOT NULL,
        categories TEXT NOT NULL,
        source TEXT,
        created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
    );
    """)
    cur.execute("""
    CREATE TABLE IF NOT EXISTS cues (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        title_id INTEGER NOT NULL REFERENCES titles(id) ON DELETE CASCADE,
        start_time TEXT NOT NULL,
        end_time TEXT NOT NULL,
        start_ms INTEGER NOT NULL,
        end_ms INTEGER NOT NULL,
        category TEXT NOT NULL,
        channel TEXT NOT NULL,
        action TEXT NOT NULL,
        description TEXT
    );
    """)
    cur.execute("CREATE INDEX IF NOT EXISTS idx_titles_title ON titles(title);")
    cur.execute("CREATE INDEX IF NOT EXISTS idx_titles_imdb ON titles(imdb_id);")
    cur.execute("CREATE INDEX IF NOT EXISTS idx_cues_title_id ON cues(title_id);")
    conn.commit()
    return conn


def save_jcf_and_index(
    conn: sqlite3.Connection,
    jcf_doc: rj.JcfDocument,
    out_path: Path,
    media_type: str = "movie",
    series_name: Optional[str] = None,
    season: Optional[int] = None,
    episode: Optional[int] = None,
) -> bool:
    """Write .jcf file to disk and record into SQLite catalog."""
    if not jcf_doc.cues:
        return False

    out_path.parent.mkdir(parents=True, exist_ok=True)
    jcf_content = jcf_doc.to_jcf()
    out_path.write_text(jcf_content, encoding="utf-8")

    categories = sorted(list({cue.category for cue in jcf_doc.cues}))
    cat_json = json.dumps(categories)
    year_int = int(jcf_doc.year) if jcf_doc.year and jcf_doc.year.isdigit() else None

    cur = conn.cursor()
    # Upsert title
    cur.execute("SELECT id FROM titles WHERE jcf_path = ?", (str(out_path),))
    row = cur.fetchone()
    if row:
        title_id = row[0]
        cur.execute(
            """
            UPDATE titles SET
                title = ?, year = ?, imdb_id = ?, media_type = ?, series_name = ?,
                season = ?, episode = ?, cue_count = ?, categories = ?, source = ?
            WHERE id = ?
            """,
            (
                jcf_doc.title,
                year_int,
                jcf_doc.imdb_id,
                media_type,
                series_name,
                season,
                episode,
                len(jcf_doc.cues),
                cat_json,
                jcf_doc.source,
                title_id,
            ),
        )
        cur.execute("DELETE FROM cues WHERE title_id = ?", (title_id,))
    else:
        cur.execute(
            """
            INSERT INTO titles (
                title, year, imdb_id, media_type, series_name, season, episode,
                jcf_path, cue_count, categories, source
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                jcf_doc.title,
                year_int,
                jcf_doc.imdb_id,
                media_type,
                series_name,
                season,
                episode,
                str(out_path),
                len(jcf_doc.cues),
                cat_json,
                jcf_doc.source,
            ),
        )
        title_id = cur.lastrowid

    # Insert cues
    for cue in jcf_doc.cues:
        cur.execute(
            """
            INSERT INTO cues (
                title_id, start_time, end_time, start_ms, end_ms,
                category, channel, action, description
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                title_id,
                cue.start_str,
                cue.end_str,
                cue.start_ms,
                cue.end_ms,
                cue.category,
                cue.channel,
                cue.action,
                cue.description,
            ),
        )

    conn.commit()
    return True


def build_from_cleanstream(conn: sqlite3.Connection) -> int:
    """Ingest cleanstream seed data (376 movies) into JCF files and database."""
    seed_file = RAW_DIR / "cleanstream_seed.json"
    if not seed_file.exists():
        print(f"File {seed_file} not found.", file=sys.stderr)
        return 0

    with open(seed_file, "r", encoding="utf-8") as f:
        data = json.load(f)

    count = 0
    for item in data:
        title = item.get("title")
        year = str(item.get("year")) if item.get("year") else None
        imdb_id = item.get("imdbId")
        raw_segments = item.get("segments", [])

        if not title or not raw_segments:
            continue

        cues: List[rj.ParsedCue] = []
        for seg in raw_segments:
            start_ms = seg.get("startMs", 0)
            end_ms = seg.get("endMs", 0)
            if end_ms <= start_ms:
                continue

            category_raw = seg.get("category", "")
            subcategory_raw = seg.get("subcategory", "")
            comment = seg.get("comment", "")
            channel_raw = seg.get("channel", "video")

            category, default_channel, action = rj.map_category_and_channel(
                f"{category_raw} {subcategory_raw}", comment
            )
            channel = channel_raw if channel_raw in ["audio", "both", "video"] else default_channel

            cues.append(
                rj.ParsedCue(
                    start_ms=start_ms,
                    end_ms=end_ms,
                    start_str=rj.ms_to_timestamp(start_ms),
                    end_str=rj.ms_to_timestamp(end_ms),
                    category=category,
                    channel=channel,
                    action="skip",
                    description=comment if comment else None,
                )
            )

        merged_cues = rj.merge_cues(cues)
        if not merged_cues:
            continue

        doc = rj.JcfDocument(
            title=title,
            year=year,
            imdb_id=imdb_id,
            source="cleanstream / videoskip open database (reddit)",
            cues=merged_cues,
        )

        safe_title = sanitize_filename(title)
        year_str = f" ({year})" if year else ""
        imdb_str = f" [imdb-{imdb_id}]" if imdb_id else ""
        file_path = MOVIES_DIR / f"{safe_title}{year_str}{imdb_str}.jcf"

        if save_jcf_and_index(conn, doc, file_path, media_type="movie"):
            count += 1

    print(f"CleanStream: Generated and indexed {count} movie JCF files.")
    return count


def build_from_thetimestampdudes(conn: sqlite3.Connection) -> int:
    """Ingest TheTimestampDudes posts (300+ titles) into JCF files and database."""
    clean_file = RAW_DIR / "thetimestampdudes_clean.json"
    if not clean_file.exists():
        print(f"File {clean_file} not found.", file=sys.stderr)
        return 0

    with open(clean_file, "r", encoding="utf-8") as f:
        data = json.load(f)

    count = 0
    for post in data:
        media_title = post.get("media_title") or post.get("title", "")
        media_title = re.sub(r"^Timestamps\s+to\s+skip(?:\s+in)?\s+", "", media_title, flags=re.I).strip()
        year = str(post.get("year")) if post.get("year") else None

        # Check known years lookup
        clean_key = media_title.lower().strip()
        if not year and clean_key in KNOWN_MOVIE_YEARS:
            norm_title, known_year = KNOWN_MOVIE_YEARS[clean_key]
            media_title = norm_title
            year = str(known_year)

        plain_text = post.get("plain_text", "")
        slug = post.get("slug", "")
        source_url = post.get("url") or (f"https://buymeacoffee.com/thetimestampdudes/{slug}" if slug else "TheTimestampDudes Reddit/BMC")

        if not media_title or not plain_text:
            continue

        cues = rj.parse_reddit_post_text(plain_text)
        if not cues:
            continue

        doc = rj.JcfDocument(
            title=media_title,
            year=year,
            source=source_url,
            cues=cues,
        )

        safe_title = sanitize_filename(media_title)
        year_str = f" ({year})" if year else ""
        file_path = MOVIES_DIR / f"{safe_title}{year_str}.jcf"

        if save_jcf_and_index(conn, doc, file_path, media_type="movie"):
            count += 1

    print(f"TheTimestampDudes: Generated and indexed {count} JCF files.")
    return count


def build_from_curated_reddit(conn: sqlite3.Connection) -> int:
    """Ingest curated Reddit community posts (movies and TV series)."""
    curated_file = RAW_DIR / "reddit_curated_posts.json"
    if not curated_file.exists():
        return 0

    with open(curated_file, "r", encoding="utf-8") as f:
        posts = json.load(f)

    count = 0
    for post in posts:
        title = post.get("title", "")
        year = str(post.get("year")) if post.get("year") else None
        media_type = post.get("media_type", "movie")
        raw_text = post.get("raw_text", "")
        source_url = post.get("url", "Reddit")

        cues = rj.parse_reddit_post_text(raw_text)
        if not cues:
            continue

        doc = rj.JcfDocument(
            title=title,
            year=year,
            source=source_url,
            cues=cues,
        )

        if media_type == "show":
            series_name = post.get("series", title)
            season = post.get("season", 1)
            episode = post.get("episode", 1)
            ep_id = post.get("ep_id", f"S{season:02d}E{episode:02d}")
            show_folder = SHOWS_DIR / sanitize_filename(series_name)
            file_path = show_folder / f"{ep_id}.jcf"
            if save_jcf_and_index(
                conn,
                doc,
                file_path,
                media_type="show",
                series_name=series_name,
                season=season,
                episode=episode,
            ):
                count += 1
        else:
            safe_title = sanitize_filename(title)
            year_str = f" ({year})" if year else ""
            file_path = MOVIES_DIR / f"{safe_title}{year_str}.jcf"
            if save_jcf_and_index(conn, doc, file_path, media_type="movie"):
                count += 1

    print(f"Curated Reddit: Generated and indexed {count} JCF files.")
    return count


def build_from_game_of_thrones(conn: sqlite3.Connection) -> int:
    """Ingest Game of Thrones episode safe timecode gaps from got_to_jcf.py."""
    got_script = Path("got_to_jcf.py")
    if not got_script.exists():
        return 0

    try:
        import got_to_jcf as got
        episodes = got.EPISODES
    except Exception as ex:
        print(f"Warning: could not import got_to_jcf: {ex}", file=sys.stderr)
        return 0

    count = 0
    show_dir = SHOWS_DIR / "Game of Thrones"
    show_dir.mkdir(parents=True, exist_ok=True)

    for ep_id, ranges_str in episodes.items():
        if not ranges_str.strip():
            continue

        normalized = ranges_str.replace(",", "+")
        ranges = [r.strip() for r in normalized.split("+") if r.strip()]
        safe_ranges: List[Tuple[int, int]] = []
        for r in ranges:
            parts = r.split("-", 1)
            if len(parts) == 2:
                s_ms = rj.parse_timestamp_to_ms(parts[0])
                e_ms = rj.parse_timestamp_to_ms(parts[1])
                safe_ranges.append((s_ms, e_ms))

        skip_cues = rj.invert_safe_ranges(
            safe_ranges, category="SexAndNudity.FullNudity", channel="video", action="skip"
        )
        if not skip_cues:
            continue

        season_match = re.match(r"S(\d+)E(\d+)", ep_id)
        season = int(season_match.group(1)) if season_match else 1
        episode = int(season_match.group(2)) if season_match else 1

        doc = rj.JcfDocument(
            title=f"Game of Thrones {ep_id}",
            year="2011",
            source="Reddit r/naath & r/gameofthrones (inverted safe ranges)",
            cues=skip_cues,
        )

        file_path = show_dir / f"{ep_id}.jcf"
        if save_jcf_and_index(
            conn,
            doc,
            file_path,
            media_type="show",
            series_name="Game of Thrones",
            season=season,
            episode=episode,
        ):
            count += 1

    print(f"Game of Thrones: Generated and indexed {count} episode JCF files.")
    return count


def export_catalog_json(conn: sqlite3.Connection) -> None:
    """Export the SQLite catalog to a portable catalog.json document."""
    cur = conn.cursor()
    cur.execute(
        """
        SELECT id, title, year, imdb_id, media_type, series_name, season, episode,
               jcf_path, cue_count, categories, source
        FROM titles ORDER BY media_type, title
        """
    )
    rows = cur.fetchall()

    catalog_data = []
    for r in rows:
        catalog_data.append(
            {
                "id": r[0],
                "title": r[1],
                "year": r[2],
                "imdb_id": r[3],
                "media_type": r[4],
                "series_name": r[5],
                "season": r[6],
                "episode": r[7],
                "jcf_path": os.path.relpath(r[8], start=DATABASE_DIR.parent),
                "cue_count": r[9],
                "categories": json.loads(r[10]) if r[10] else [],
                "source": r[11],
            }
        )

    with open(CATALOG_JSON, "w", encoding="utf-8") as f:
        json.dump(catalog_data, f, indent=2)

    print(f"Wrote catalog JSON index with {len(catalog_data)} titles to {CATALOG_JSON}")


def cmd_build() -> None:
    """Build the entire JCF database from all raw sources."""
    print("Building comprehensive JCF content filter database...")
    MOVIES_DIR.mkdir(parents=True, exist_ok=True)
    SHOWS_DIR.mkdir(parents=True, exist_ok=True)

    conn = init_sqlite(CATALOG_DB)

    build_from_cleanstream(conn)
    build_from_thetimestampdudes(conn)
    build_from_curated_reddit(conn)
    build_from_game_of_thrones(conn)
    export_catalog_json(conn)

    conn.close()
    print("\nDatabase build complete!")
    cmd_stats()


def cmd_search(query: str) -> None:
    """Search catalog by title, series, imdb id, or category."""
    if not CATALOG_DB.exists():
        print(f"Database {CATALOG_DB} does not exist. Run 'python tools/jcf_db.py build' first.", file=sys.stderr)
        return

    conn = sqlite3.connect(CATALOG_DB)
    cur = conn.cursor()
    like_q = f"%{query}%"
    cur.execute(
        """
        SELECT id, title, year, media_type, series_name, cue_count, categories, jcf_path
        FROM titles
        WHERE title LIKE ? OR series_name LIKE ? OR imdb_id LIKE ? OR categories LIKE ?
        ORDER BY media_type, title
        LIMIT 50
        """,
        (like_q, like_q, like_q, like_q),
    )
    rows = cur.fetchall()
    conn.close()

    if not rows:
        print(f"No matching titles found for query: '{query}'")
        return

    print(f"\nFound {len(rows)} matching results for '{query}':\n")
    print(f"{'TYPE':<7} {'TITLE':<36} {'YEAR':<6} {'CUES':<6} {'FILE PATH'}")
    print("-" * 90)
    for r in rows:
        media_type = r[3].upper()
        name = r[1]
        year = str(r[2]) if r[2] else "-"
        cues = str(r[5])
        path = os.path.relpath(r[7], start=DATABASE_DIR.parent)
        print(f"{media_type:<7} {name[:35]:<36} {year:<6} {cues:<6} {path}")


def cmd_stats() -> None:
    """Print summary statistics of the database."""
    if not CATALOG_DB.exists():
        print(f"Database {CATALOG_DB} does not exist. Run 'python tools/jcf_db.py build' first.", file=sys.stderr)
        return

    conn = sqlite3.connect(CATALOG_DB)
    cur = conn.cursor()

    cur.execute("SELECT COUNT(*) FROM titles WHERE media_type = 'movie'")
    movies_count = cur.fetchone()[0]

    cur.execute("SELECT COUNT(DISTINCT series_name) FROM titles WHERE media_type = 'show'")
    series_count = cur.fetchone()[0]

    cur.execute("SELECT COUNT(*) FROM titles WHERE media_type = 'show'")
    episodes_count = cur.fetchone()[0]

    cur.execute("SELECT COUNT(*) FROM cues")
    total_cues = cur.fetchone()[0]

    cur.execute("SELECT category, COUNT(*) as cnt FROM cues GROUP BY category ORDER BY cnt DESC")
    cat_counts = cur.fetchall()
    conn.close()

    print("\n========================================================")
    print("   JELLYFIN CONTENT FILTER (.JCF) DATABASE SUMMARY")
    print("========================================================")
    print(f" Total Movie Titles:      {movies_count:,}")
    print(f" Total TV Series:         {series_count:,}")
    print(f" Total TV Episodes:       {episodes_count:,}")
    print(f" Total Filter Cues:       {total_cues:,}")
    print("\n Cues by Category:")
    for cat, cnt in cat_counts:
        print(f"   - {cat:<35}: {cnt:>5,} cues")
    print("========================================================\n")


def cmd_export(target_dir: str) -> None:
    """Export JCF files to a target directory (e.g. Jellyfin media folder or plugin filter folder)."""
    target = Path(target_dir)
    target.mkdir(parents=True, exist_ok=True)

    if not CATALOG_DB.exists():
        print("Catalog database does not exist. Run 'build' first.", file=sys.stderr)
        return

    conn = sqlite3.connect(CATALOG_DB)
    cur = conn.cursor()
    cur.execute("SELECT jcf_path FROM titles")
    rows = cur.fetchall()
    conn.close()

    exported = 0
    for (src_path_str,) in rows:
        src = Path(src_path_str)
        if src.exists():
            dest = target / src.name
            dest.write_bytes(src.read_bytes())
            exported += 1

    print(f"Exported {exported} JCF files to {target}")


def cmd_upgrade(target_path: str) -> None:
    """Upgrade existing JCF files in a directory or single file in-place to VidAngel/IMDb standards."""
    target = Path(target_path)
    if not target.exists():
        print(f"Error: Path {target} does not exist.", file=sys.stderr)
        return

    if target.is_file():
        files = [target]
    else:
        files = list(target.rglob("*.jcf"))

    print(f"Scanning {len(files)} JCF file(s) for legacy categories in {target}...")
    upgraded_count = 0
    for jcf_path in files:
        if rj.upgrade_jcf_file(jcf_path):
            upgraded_count += 1

    print(f"Upgrade complete: {upgraded_count} of {len(files)} file(s) updated.")


def main() -> None:
    parser = argparse.ArgumentParser(description="JCF Content Filter Database Manager")
    subparsers = parser.add_subparsers(dest="command", help="Command to run")

    subparsers.add_parser("build", help="Build and index JCF database from raw sources")
    subparsers.add_parser("stats", help="Show database summary statistics")

    upgrade_parser = subparsers.add_parser("upgrade", help="Upgrade existing JCF files in-place to VidAngel/IMDb standards")
    upgrade_parser.add_argument("path", type=str, help="Path to JCF file or directory containing .jcf files")

    search_parser = subparsers.add_parser("search", help="Search titles in database")
    search_parser.add_argument("query", type=str, help="Search term (title, imdb id, or category)")

    export_parser = subparsers.add_parser("export", help="Export JCF files to target folder")
    export_parser.add_argument("--target", required=True, type=str, help="Destination directory")

    args = parser.parse_args()

    if args.command == "build":
        cmd_build()
    elif args.command == "upgrade":
        cmd_upgrade(args.path)
    elif args.command == "search":
        cmd_search(args.query)
    elif args.command == "stats":
        cmd_stats()
    elif args.command == "export":
        cmd_export(args.target)
    else:
        parser.print_help()


if __name__ == "__main__":
    main()
