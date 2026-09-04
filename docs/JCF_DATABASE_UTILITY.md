# JCF Content Filter Database & Reddit Timecode Harvester

A comprehensive scraping, parsing, and catalog management tool suite that scours Reddit and community archives for content timecodes (nudity, sex, violence, gore, substance use, profanity, medical procedures, etc.) and converts them into standardized `.jcf` (Jellyfin Content Filter) sidecar files.

---

## Table of Contents
1. [Overview](#overview)
2. [Data Sources & Provenance](#data-sources--provenance)
3. [Database Architecture & Layout](#database-architecture--layout)
4. [CLI Command Reference](#cli-command-reference)
   - [Database Manager (`tools/jcf_db.py`)](#database-manager-toolsjcf_dbpy)
   - [Reddit Scraper & Harvester (`tools/reddit_scraper.py`)](#reddit-scraper--harvester-toolsreddit_scraperpy)
   - [Timecode Parser & Converter (`tools/reddit_to_jcf.py`)](#timecode-parser--converter-toolsreddit_to_jcfpy)
5. [Category Mapping Standards](#category-mapping-standards)
6. [Using with Jellyfin](#using-with-jellyfin)
   - [Media Sidecars (Recommended)](#1-media-sidecars-recommended)
   - [Central Plugin Filter Store](#2-central-plugin-filter-store)
7. [Contributing & Ingesting New Titles](#contributing--ingesting-new-titles)

---

## Overview

The Jellyfin Content Filter plugin enables viewers to automatically skip or mute objectionable scenes during playback. Rather than relying solely on local vision model scans (which require significant GPU compute), this utility taps into community-curated timecodes shared across Reddit communities and converts them into ready-to-use `.jcf` files.

### Key Metrics
- **701 Movie Titles** indexed
- **6 TV Series** (*Game of Thrones*, *Bridgerton*, *Squid Game*, *The Punisher*, *Stargate SG-1*, *Yellowjackets*) across **33 episodes**
- **12,428 Filter Cues** tagged with millisecond precision
- Strict conformance to the WEBVTT JCF format and `FilterDictionary.cs`

---

## Data Sources & Provenance

The database is aggregated from multiple community initiatives:

1. **VideoSkip / Stremio-CleanStream Open Database**
   - 376 movies pre-tagged with categorized segments (IMDb IDs, millisecond ranges, categories, and channels).
   - Originates from Reddit discussions on `r/StremioAddons` and the open VideoSkip project.

2. **TheTimestampDudes Community Archives**
   - 330+ movie and show posts harvested from Reddit (`r/TheTimestampDudes`) and creator feeds.
   - Comprehensive timecodes for classic, 80s/90s, cult, horror, and modern films.

3. **Curated Reddit Community Guides**
   - Scene skip guides and parental timecodes from show-specific communities:
     - `r/Bridgerton` (Anthony/Siena sex scenes, season 1 episodes)
     - `r/Yellowjackets` (intimate scene timecodes)
     - `r/squidgame` (nudity and violence markers)
     - `r/movies` (Deadpool 1 & 2 nudity and strip club sequences)
     - `r/u_flccncnhlplfctn` (Stargate SG-1 pilot nudity cue)
     - `r/naath` & `r/gameofthrones` (Game of Thrones S01–S07 clean segment inversions)

4. **Raw Provenance Store**
   - All source payloads are preserved in `jcf_database/raw/` (`cleanstream_seed.json`, `thetimestampdudes_clean.json`, `reddit_curated_posts.json`) to allow full reproducibility and auditing.

---

## Database Architecture & Layout

```
jcf_database/
├── catalog.db                   # SQLite database indexing all titles, metadata, and cues
├── catalog.json                 # Machine-readable JSON catalog for apps and API lookups
├── movies/                      # 701 Movie JCF files
│   ├── 28 Days Later (2002).jcf
│   ├── A Clockwork Orange (1971) [imdb-tt0066921].jcf
│   ├── Annihilation (2018).jcf
│   ├── Deadpool (2016) [imdb-tt1431045].jcf
│   ├── Die Hard (1988).jcf
│   ├── Ex Machina (2014).jcf
│   ├── Logan (2017) [imdb-tt3315342].jcf
│   └── ...
├── shows/                       # TV Show JCF files organized by series
│   ├── Bridgerton/
│   │   ├── S01E01.jcf
│   │   └── S01E02.jcf
│   ├── Game of Thrones/
│   │   ├── S01E01.jcf
│   │   └── ... (25 episodes)
│   ├── Squid Game/
│   │   └── S01E01.jcf
│   ├── Stargate SG-1/
│   │   └── S01E01.jcf
│   ├── The Punisher/
│   │   ├── S02E01.jcf
│   │   └── ...
│   └── Yellowjackets/
│       └── S01E02.jcf
└── raw/                         # Raw harvested sources
    ├── cleanstream_seed.json
    ├── reddit_curated_posts.json
    └── thetimestampdudes_clean.json
```

### SQLite Schema (`catalog.db`)
- **`titles`**:
  `id`, `title`, `year`, `imdb_id`, `media_type`, `series_name`, `season`, `episode`, `jcf_path`, `cue_count`, `categories`, `source`, `created_at`
- **`cues`**:
  `id`, `title_id`, `start_time`, `end_time`, `start_ms`, `end_ms`, `category`, `channel`, `action`, `description`

---

## CLI Command Reference

### Database Manager (`tools/jcf_db.py`)

The primary command-line tool for building, searching, querying, and exporting the database.

#### 1. Search Titles
Search for any title, IMDb ID, series name, or category:
```bash
python tools/jcf_db.py search "Deadpool"
python tools/jcf_db.py search "Bridgerton"
python tools/jcf_db.py search "tt0066921"
python tools/jcf_db.py search "NudityProfiles"
```

*Example Output:*
```
Found 3 matching results for 'Deadpool':

TYPE    TITLE                                YEAR   CUES   FILE PATH
------------------------------------------------------------------------------------------
MOVIE   Deadpool                             2016   6      jcf_database/movies/Deadpool (2016) [imdb-tt1431045].jcf
MOVIE   Deadpool                             2016   4      jcf_database/movies/Deadpool (2016).jcf
MOVIE   Deadpool 2                           2018   2      jcf_database/movies/Deadpool 2 (2018).jcf
```

#### 2. Display Statistics
Print a live summary of all indexed movies, shows, episodes, and category distributions:
```bash
python tools/jcf_db.py stats
```

#### 3. Rebuild Database
Parses all raw harvested payloads, updates `.jcf` files, and re-indexes `catalog.db` and `catalog.json`:
```bash
python tools/jcf_db.py build
```

#### 4. Export to Media Folders
Copy `.jcf` sidecar files into your target media directory:
```bash
python tools/jcf_db.py export --target /path/to/jellyfin/movies
```

---

### Reddit Scraper & Harvester (`tools/reddit_scraper.py`)

Fetches updates from Reddit public archives (via PullPush and RSS) and community feeds.

#### Harvest all sources:
```bash
python tools/reddit_scraper.py --all
```

#### Query Reddit archives for a specific movie or show:
```bash
python tools/reddit_scraper.py --query "Euphoria"
```

---

### Timecode Parser & Converter (`tools/reddit_to_jcf.py`)

Python library providing core timecode and category parsing functions:

- `parse_timestamp_to_ms(ts_str)`: Parses `M:SS`, `MM:SS`, `H:MM:SS`, `HH:MM:SS`, or `HH:MM:SS.mmm` into integer milliseconds.
- `ms_to_timestamp(ms)`: Formats millisecond integers into `HH:MM:SS.mmm`.
- `clean_line_typos(line)`: Fixes common typo patterns found in Reddit comments (e.g. `1:18: 21` -> `1:18:21`).
- `parse_reddit_post_text(text)`: Parses freeform post text, detects section headers, extracts ranges, and returns a list of `ParsedCue` objects.
- `invert_safe_ranges(safe_ranges)`: Converts safe playback interval lists into skip cues for the objectionable gaps.
- `map_category_and_channel(label, context)`: Maps freeform tags to official Jellyfin plugin categories.

---

## Category Mapping Standards

All cues map directly to the official `FilterDictionary.cs` categories recognized by the Jellyfin plugin:

| Category | Default Channel | Default Action | Keywords / Content Triggers |
| :--- | :--- | :--- | :--- |
| `SexAndNudity.NudityProfiles` | `video` | `skip` | Nudity, topless, bare backside, buttocks, breasts, cleavage, bra, underwear, shirtless, shower |
| `SexAndNudity.OnscreenActivity` | `video` | `skip` | Sex scene, intercourse, oral sex, intimate acts, masturbation, fooling in bed |
| `SexAndNudity.PhysicalIntimacy` | `video` | `skip` | Passionate kissing, romantic intimate contact without sex |
| `SexualReferences.ContextualDialogue` | `video` | `skip` | Sexual dialogue, suggestive conversation, explicit remarks |
| `SexualReferences.Visuals` | `video` | `skip` | Vulgar gestures, flipping off, middle finger |
| `Violence.Tiers` | `video` | `skip` | Violence, gore, blood, killings, knife fights, gunfights, horror, jumpscares, torture |
| `Substances.Usage` | `video` | `skip` | Drugs, cocaine, weed, alcohol, drunkenness, smoking, pills, injections |
| `Medical.Events` | `both` | `skip` | Surgery, medical procedures, needles, vomiting, bodily functions |
| `Language.GeneralProfanity` | `audio` | `mute` | Swearing, cursing, profanity, f-word |
| `Language.Blasphemy` | `audio` | `mute` | Blasphemous oaths, religious profanity |
| `Language.RacialAndBigotedSlurs` | `audio` | `mute` | Racial, ethnic, or bigoted slurs |
| `Structural.Timestamps` | `both` | `skip` | Opening credits, closing credits, episode recap, outtakes |

---

## Using with Jellyfin

### 1. Media Sidecars (Recommended)
Place the `.jcf` file directly alongside the video file in your library matching the video's filename stem:
```
/media/Movies/
  ├── Deadpool (2016).mkv
  └── Deadpool (2016).jcf

/media/Shows/Game of Thrones/Season 01/
  ├── Game of Thrones - S01E01.mkv
  └── Game of Thrones - S01E01.jcf
```

When Jellyfin starts playback, `FilterStore.cs` automatically detects the adjacent `.jcf` file, registers the cues, and enforces skip/mute actions according to the current user's profile settings.

### 2. Central Plugin Filter Store
Alternatively, copy the `.jcf` file into the plugin's data folder using the media item's Jellyfin GUID:
```
/var/lib/jellyfin/data/plugins/configurations/Jellyfin.Plugin.ContentFilter/filters/
  └── <ItemGuid>.jcf
```

---

## Contributing & Ingesting New Titles

To add a new movie or TV show from Reddit:
1. Copy the Reddit post URL and text containing timecodes.
2. Add the record into `tools/reddit_scraper.py` under `CURATED_REDDIT_POSTS` (or save in `jcf_database/raw/`).
3. Run the database builder:
   ```bash
   python tools/jcf_db.py build
   ```
4. Verify with the test suite:
   ```bash
   pytest tests/test_jcf_database.py
   ```
