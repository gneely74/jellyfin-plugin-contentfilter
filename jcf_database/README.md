# Jellyfin Content Filter (.jcf) Database

This directory contains pre-built, standardized `.jcf` sidecar filter files harvested from Reddit communities and open databases.

## Summary
- **Movies:** 701 titles (`movies/`)
- **TV Shows:** 6 series / 33 episodes (`shows/`)
- **Cues:** 12,671 tagged filter cues (VidAngel & IMDb Parents Guide compatible)
- **Catalog Index:** `catalog.db` (SQLite) and `catalog.json` (JSON)

## Quick Start
Search the catalog:
```bash
python tools/jcf_db.py search "Matrix"
```

Export sidecars to your media library:
```bash
python tools/jcf_db.py export --target /path/to/jellyfin/movies
```

For full documentation and technical details, see [docs/JCF_DATABASE_UTILITY.md](../docs/JCF_DATABASE_UTILITY.md).
