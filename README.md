# jellyfin-plugin-contentfilter

A Jellyfin plugin that scans media for objectionable content using a local [Ollama](https://ollama.com) LLM and applies skip or mute actions during playback based on per-category user preferences.

`SPDX-License-Identifier: GPL-3.0-only`

## Features

- **Scan** — Analyze video files with a local Ollama vision model and audio model. Results are written as JCF sidecar files (WebVTT-based, one cue per flagged segment).
- **Filter categories** — Violence, Language, Sexual Content, Nudity, Substance Use, Frightening, and Thematic content, each independently enabled/disabled with a skip or mute action.
- **Playback enforcement** — The playback monitor intercepts sessions and issues skip/mute commands when a flagged segment is reached.
- **Legacy MCF support** — Existing `.mcf` WebVTT sidecar files from the original Movie Content Filter project are parsed and mapped to JCF categories automatically.
- **Community JCF Database (700+ titles)** — Over 700+ movies and TV shows pre-filtered and cataloged with 12,400+ cues harvested from Reddit timecodes. See [JCF Database Utility Documentation](docs/JCF_DATABASE_UTILITY.md).

## Installation

### Docker (local dev)

```bash
make docker-up   # builds plugin + starts Jellyfin at http://localhost:8096
```

### Manual (existing Jellyfin)

```bash
make package
# then copy dist/ContentFilter_1.0.0.0/ into your Jellyfin plugins directory
# or:
make install JELLYFIN_PLUGINS=/path/to/jellyfin/plugins
```

### Plugin Repository

Add the following URL in **Dashboard → Plugins → Repositories**:

```
https://raw.githubusercontent.com/gneely74/jellyfin-plugin-contentfilter/main/manifest.json
```

## Configuration

After installation, go to **Dashboard → Plugins → Jellyfin Content Filter** to:

- Enable/disable filter categories and set per-category action (skip or mute)
- Configure the Ollama server URL, vision model, and audio model
- Set the frame sample rate for video scanning

## JCF File Format

Sidecar files use the `.jcf` extension and follow the WebVTT format. Each cue block describes one flagged segment:

```
WEBVTT JCF

NOTE
TITLE Example Movie
YEAR 2024

00:10:00.000 --> 00:10:05.000
category: Violence.Tiers
channel: video
action: skip
description: Brief injury shot
```

## Pre-built JCF Database & Scraper

The repository includes a curated database of **700+ movies and TV series** with **12,400+ cues** harvested from Reddit timecodes and community repositories:

```bash
# Search for titles
python tools/jcf_db.py search "Deadpool"

# Show database summary stats
python tools/jcf_db.py stats

# Export sidecars directly to your media library
python tools/jcf_db.py export --target /path/to/media/Movies
```

For complete documentation, scraper details, and category mapping, see [docs/JCF_DATABASE_UTILITY.md](docs/JCF_DATABASE_UTILITY.md).

## Community Timestamp Tools

The repository includes tools for importing community timestamps:

- **TheTimestampDudes (Buy Me a Coffee)**: Download and convert timestamps from [TheTimestampDudes](https://buymeacoffee.com/thetimestampdudes) into discrete `.jcf` sidecar files:
  - `./download_bmc_posts.py` — Authenticates with Buy Me a Coffee (with 2FA support), downloads all creator posts, and exports clean metadata.
  - `./process_to_jcf.py` — Converts timestamps, corrects typos, maps categories, and emits compliant `.jcf` files.
  - See the full [TheTimestampDudes Guide](docs/buymeacoffee_thetimestampdudes.md) for details.

## Legal

This plugin does not alter video files. It instructs the Jellyfin client to skip or mute segments based on user preferences. All video content belongs to its respective copyright holders; no affiliation or endorsement is claimed.
