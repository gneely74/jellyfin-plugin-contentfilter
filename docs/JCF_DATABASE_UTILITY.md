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

#### 4. Upgrade Existing Libraries
Scan any folder or single `.jcf` file and upgrade legacy categories in-place to the new VidAngel/IMDb standard:
```bash
python tools/jcf_db.py upgrade /path/to/my/jcf_library
```

#### 5. Export to Media Folders
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
- `load_jcf_file(file_path)`: Loads any `.jcf` file into a `JcfDocument`.
- `upgrade_jcf_file(file_path)`: Upgrades legacy category tags in an existing file in-place.

---

## Category Mapping Standards (VidAngel & IMDb Parents Guide Compatible)

All cues map directly to the official `FilterDictionary.cs` categories recognized by the Jellyfin plugin, aligning 1-to-1 with VidAngel subcategories and IMDb Parents Guide severity tiers (*Mild*, *Moderate*, *Severe*):

| Category | VidAngel Equivalent | IMDb Severity Tier | Channel | Action | Description & Triggers |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **`Violence.Mild`** | Mild Violence | *Mild* | `video` | `skip` | Comic action, playful wrestling, slaps, bloodless fistfights |
| **`Violence.Moderate`** | Moderate Violence | *Moderate* | `video` | `skip` | Realistic combat, shootouts, blood on clothing, vehicle wrecks |
| **`Violence.Graphic`** | Graphic Violence | *Severe* | `video` | `skip` | Visceral wounds, severe trauma, fatal stabbings, close-range gunshots |
| **`Violence.Gore`** | Gore & Blood | *Severe* | `video` | `skip` | Dismemberment, decapitation, mutilation, severed limbs, exposed organs |
| **`Violence.JumpScares`** | Jump Scares | *Frightening* | `video` | `skip` | Sudden scare sequences, startling creature reveals, jump cuts |
| **`Violence.Disturbing`** | Disturbing Images | *Severe* | `video` | `skip` | Corpses, suicide themes, torture, psychological horror, eerie atmosphere |
| **`SexAndNudity.Mild`** | Mild Immodesty | *Mild* | `video` | `skip` | Swimwear, beach attire, brief non-sexual kissing, classic art |
| **`SexAndNudity.PhysicalIntimacy`** | Kissing / Intimacy | *Mild / Mod* | `video` | `skip` | Passionate kissing, prolonged making out, sensual caressing |
| **`SexAndNudity.PartialNudity`** | Partial Nudity | *Moderate* | `video` | `skip` | Underwear, lingerie, bra, panties, shirtless, revealing immodesty |
| **`SexAndNudity.FullNudity`** | Full Nudity | *Severe* | `video` | `skip` | Frontal nudity, bare breasts, bare buttocks, unclad exposure |
| **`SexAndNudity.ImpliedSex`** | Implied Sex | *Moderate* | `video` | `skip` | Bedroom scenes, sex without nudity, suggestive dancing, fooling in bed |
| **`SexAndNudity.Graphic`** | Graphic Sex | *Severe* | `video` | `skip` | Explicit intercourse, oral sex, masturbation, penetrative acts |
| **`SexAndNudity.SexualAssault`** | Sexual Assault | *Severe* | `video` | `skip` | Rape, sexual violence, non-consensual acts, molestation |
| **`SexualReferences.ExplicitWords`** | Explicit Words | *Severe* | `audio` | `mute` | Subtitle word-match for explicit sexual slang and terms |
| **`SexualReferences.ContextualDialogue`** | Innuendo / Dialogue | *Moderate* | `video` | `skip` | Sexual dialogue, suggestive remarks, propositions, innuendo |
| **`SexualReferences.Visuals`** | Vulgar Gestures | *Moderate* | `video` | `skip` | Middle finger, offensive/obscene hand gestures |
| **`Language.GeneralProfanity`** | General Profanity | *Moderate* | `audio` | `mute` | Common swear words (fuck, shit, ass, bitch, etc.) |
| **`Language.Blasphemy`** | Blasphemy | *Moderate* | `audio` | `mute` | Religious oaths and deity expletives (Jesus Christ, God damn) |
| **`Language.RacialAndBigotedSlurs`** | Hate Speech / Slurs | *Severe* | `audio` | `mute` | Racial, ethnic, or bigoted slurs |
| **`Language.ChildishLanguage`** | Crude Humor | *Mild* | `audio` | `mute` | Childish crude words (butt, fart, dumb, stupid) |
| **`Language.CaptionsWithProfanity`** | Captions Profanity | *Moderate* | `both` | `mute` | Subtitle text containing profanity |
| **`Substances.Tobacco`** | Tobacco | *Mild* | `video` | `skip` | Cigarettes, cigars, pipes, vaping, e-cigarettes |
| **`Substances.Alcohol`** | Alcohol | *Mild / Mod* | `video` | `skip` | Drinking, drunkenness, beer, wine, liquor, bar scenes |
| **`Substances.IllegalDrugs`** | Illegal Drugs | *Severe* | `video` | `skip` | Illicit narcotics, cocaine, marijuana, heroin, injections, overdose |
| **`Medical.Events`** | Medical Procedures | *Moderate* | `both` | `skip` | Surgery, invasive procedures, needles, hospital trauma |
| **`Medical.BodilyFunctions`** | Bodily Functions | *Mild / Mod* | `both` | `skip` | Vomiting, barfing, gross toilet humor, flatulence |
| **`Structural.Credits`** | Credits | N/A | `both` | `skip` | Opening credits and closing credits |
| **`Structural.IntroRecap`** | Intro / Recap | N/A | `both` | `skip` | Episode recap sequence, intro titles, outtakes and bloopers |
| *(Legacy Aliases)* | *(Compatibility)* | | | | `Violence.Tiers`, `SexAndNudity.NudityProfiles`, `Substances.Usage`, etc. |

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
