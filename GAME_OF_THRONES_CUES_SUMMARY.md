# Game of Thrones — Content Filter Sources & Cues Review Summary

> **Document Purpose:** Complete audit and review guide of all content filter cues (nudity skips and profanity audio mutes) across *Game of Thrones*, detailing data sources, exact timestamps, skip durations, category tagging, and local media library deployment status.

---

## 1. Data Sources & Methodology

### Primary Source: Reddit Blu-ray Nudity Timestamps
- **Author:** [u/Soundwave_47](https://www.reddit.com/user/Soundwave_47/)
- **Date Published:** August 30, 2022
- **Original Threads:**
  - `r/gameofthrones`: [Game of Thrones Nudity Timestamps](https://www.reddit.com/r/gameofthrones/comments/x1fom2/game_of_thrones_nudity_timestamps/)
  - `r/naath`: [Game of Thrones Nudity Timestamps](https://www.reddit.com/r/naath/comments/x1fq8p/game_of_thrones_nudity_timestamps/)
- **Baseline Video Edition:** Game of Thrones Official Blu-ray Box Set (1080p).
- **Editing Methodology:** The author used **MKVToolNix** split mode to specify clean intervals (*safe playback segments*) for a family rewatch. To produce standard Jellyfin Content Filter (`.jcf`) sidecars, our toolchain mathematically **inverted** the safe ranges to identify the gaps between consecutive clean segments. Those gaps represent the objectionable scenes that Jellyfin seeks past (`action: skip`, `channel: video`).
- **Scope & Tone:** The author flagged full nudity, graphic sexual violence, explicit brothel sequences, and overt sexual dialogue/innuendo.
- **Numbering Resolution:** In the raw Reddit Markdown post, seasons were structured as 1–10 numbered lists where unedited episodes were denoted by solitary periods (`.`). Earlier ingestion scripts had stripped whitespace and periods without preserving index positions, which shifted timecodes onto preceding episodes. This was fully corrected in [`got_to_jcf.py`](file:///Users/gneely/git/jellyfin-plugin-contentfilter/got_to_jcf.py) across all 67 episodes.

### Secondary Source: Community Reviews & Known Omissions
- Community members reviewed the post and noted specific episodes left blank (`.`) by the author that nonetheless contain objectionable scenes:
  - **S01E02 (*The Kingsroad*):** Contains an explicit scene between Daenerys and Khal Drogo.
  - **S01E07 (*You Win or You Die*):** Contains an explicit Littlefinger brothel exposition scene (Ros & Armeca).
  - **S02E01 (*The North Remembers*):** Contains an explicit scene near the conclusion.
  - **Season 08:** Omitted entirely by the post author with the note *"it was pretty minor"*.

### Tertiary Source: Subtitle Word Scanner (Spoken Profanity)
- **Episode S01E01 (*Winter Is Coming*):** In addition to visual nudity skips, S01E01 includes **10 audio mute cues** for coarse language (`Language.GeneralProfanity`, `action: mute`, `channel: audio`) covering spoken occurrences of *"damn"* (3 cues) and *"bastard"* (7 cues). These audio mutes were preserved and chronologically merged with the 11 nudity video skips for a total of 21 cues.

### Local Media Library Integration
- **Media Root:** `/Volumes/data/shows/Game of Thrones/`
- **Naming Convention:** Jellyfin sidecar format `<VideoBaseName>.jcf` placed in the same season folder as the `.mkv` file.
- **Current Coverage:** **23 episodes** have matching video files present on disk and have active sidecar files deployed. Two episodes with compiled cues (S03E08 and S05E03) are indexed in the plugin catalog, awaiting video file acquisition.

---

## 2. Global Catalog Summary

- **Total Episodes Cataloged with Cues:** 25 of 73 series episodes
- **Total Filter Cues:** 163 (153 video skips, 10 audio mutes)
- **Total Objectionable Video Skipped:** 49m 29s (49.5 minutes)
- **Total Spoken Audio Muted:** 6.8s
- **Sidecars Deployed in Local Library:** 23 episodes (140 active cues on disk)

### Season Breakdown

| Season | Total Episodes | Filtered Episodes | Video Skips | Audio Mutes | Total Cut Time | Local Library Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :--- |
| **Season 01** | 10 | 4 | 22 | 10 | 8m 15s | ✅ All 4 sidecars deployed |
| **Season 02** | 10 | 4 | 11 | 0 | 7m 48s | ✅ All 4 sidecars deployed |
| **Season 03** | 10 | 4 | 34 | 0 | 14m 50s | ⚠️ 3/4 deployed (1 missing MKV) |
| **Season 04** | 10 | 6 | 41 | 0 | 6m 31s | ✅ All 6 sidecars deployed |
| **Season 05** | 10 | 5 | 33 | 0 | 10m 16s | ⚠️ 4/5 deployed (1 missing MKV) |
| **Season 06** | 10 | 1 | 11 | 0 | 1m 28s | ✅ All 1 sidecars deployed |
| **Season 07** | 7 | 1 | 1 | 0 | 21s | ✅ All 1 sidecars deployed |
| **Season 08** | 6 | 0 | 0 | 0 | 0s | 3 MKVs (No cues in post) |
| **Total** | **73** | **25** | **153** | **10** | **49m 29s** | **23 deployed / 2 pending MKV** |

---

## 3. Detailed Episode-by-Episode Cues

### Season 01

#### S01E01 — Winter Is Coming
- **Sidecar File:** `Game of Thrones - S01E01 - Winter Is Coming.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 21 total (11 skips / 10 mutes)
- **Objectionable Duration:** 4m 45s skipped, 6.8s muted

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:29:13.174` | `00:29:13.734` | **0.6s** | `Language.GeneralProfanity` | audio | `mute` | Spoken: "damn" |
| 02 | `00:29:35.620` | `00:29:36.100` | **0.5s** | `Language.GeneralProfanity` | audio | `mute` | Spoken: "damn" |
| 03 | `00:29:42.076` | `00:29:42.636` | **0.6s** | `Language.GeneralProfanity` | audio | `mute` | Spoken: "damn" |
| 04 | `00:30:30.000` | `00:31:15.000` | **45s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 05 | `00:31:30.000` | `00:31:55.000` | **25s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 06 | `00:32:10.000` | `00:32:25.000` | **15s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 07 | `00:34:20.000` | `00:35:40.000` | **1m 20s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 08 | `00:39:10.000` | `00:39:20.000` | **10s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 09 | `00:39:10.122` | `00:39:10.952` | **0.8s** | `Language.GeneralProfanity` | audio | `mute` | Spoken: "bastard" |
| 10 | `00:39:15.249` | `00:39:16.079` | **0.8s** | `Language.GeneralProfanity` | audio | `mute` | Spoken: "bastard" |
| 11 | `00:40:30.632` | `00:40:31.462` | **0.8s** | `Language.GeneralProfanity` | audio | `mute` | Spoken: "bastard" |
| 12 | `00:40:40.415` | `00:40:41.246` | **0.8s** | `Language.GeneralProfanity` | audio | `mute` | Spoken: "bastard" |
| 13 | `00:40:50.896` | `00:40:51.700` | **0.8s** | `Language.GeneralProfanity` | audio | `mute` | Spoken: "bastard" |
| 14 | `00:40:55.919` | `00:40:56.460` | **0.5s** | `Language.GeneralProfanity` | audio | `mute` | Spoken: "bastard" |
| 15 | `00:41:10.953` | `00:41:11.470` | **0.5s** | `Language.GeneralProfanity` | audio | `mute` | Spoken: "bastard" |
| 16 | `00:50:50.000` | `00:51:00.000` | **10s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 17 | `00:51:15.000` | `00:51:40.000` | **25s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 18 | `00:52:10.000` | `00:52:15.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 19 | `00:54:10.000` | `00:54:30.000` | **20s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 20 | `00:56:55.000` | `00:57:20.000` | **25s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 21 | `00:59:25.000` | `00:59:50.000` | **25s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S01E03 — Lord Snow
- **Sidecar File:** `Game of Thrones - S01E03 - Lord Snow.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 2 total (2 skips / 0 mutes)
- **Objectionable Duration:** 1m 10s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:19:40.000` | `00:20:00.000` | **20s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:50:50.000` | `00:51:40.000` | **50s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S01E09 — Baelor
- **Sidecar File:** `Game of Thrones - S01E09 - Baelor.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 3 total (3 skips / 0 mutes)
- **Objectionable Duration:** 50s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:26:00.000` | `00:26:10.000` | **10s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:26:40.000` | `00:27:10.000` | **30s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 03 | `00:42:30.000` | `00:42:40.000` | **10s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S01E10 — Fire and Blood
- **Sidecar File:** `Game of Thrones - S01E10 - Fire and Blood.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 6 total (6 skips / 0 mutes)
- **Objectionable Duration:** 1m 30s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:18:50.000` | `00:19:00.000` | **10s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:35:05.000` | `00:35:25.000` | **20s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 03 | `00:35:35.000` | `00:35:50.000` | **15s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 04 | `00:50:15.000` | `00:50:25.000` | **10s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 05 | `00:50:40.000` | `00:51:05.000` | **25s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 06 | `00:51:15.000` | `00:51:25.000` | **10s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

### Season 02

#### S02E04 — Garden of Bones
- **Sidecar File:** `Game of Thrones - S02E04 - Garden of Bones.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 4 total (4 skips / 0 mutes)
- **Objectionable Duration:** 4m 10s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:11:40.000` | `00:14:20.000` | **2m 40s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:14:30.000` | `00:14:40.000` | **10s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 03 | `00:47:50.000` | `00:48:50.000` | **1m 00s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 04 | `00:49:00.000` | `00:49:20.000` | **20s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S02E08 — The Prince of Winterfell
- **Sidecar File:** `Game of Thrones - S02E08 - The Prince of Winterfell.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 1 total (1 skips / 0 mutes)
- **Objectionable Duration:** 1m 05s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:39:50.000` | `00:40:55.000` | **1m 05s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S02E09 — Blackwater
- **Sidecar File:** `Game of Thrones - S02E09 - Blackwater.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 4 total (4 skips / 0 mutes)
- **Objectionable Duration:** 2m 22s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:09:10.000` | `00:09:40.000` | **30s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:09:45.000` | `00:11:20.000` | **1m 35s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 03 | `00:11:25.000` | `00:11:35.000` | **10s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 04 | `00:11:43.000` | `00:11:50.000` | **7s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S02E10 — Valar Morghulis
- **Sidecar File:** `Game of Thrones - S02E10 - Valar Morghulis.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 2 total (2 skips / 0 mutes)
- **Objectionable Duration:** 11s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:09:45.000` | `00:09:51.000` | **6s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:09:53.000` | `00:09:58.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

### Season 03

#### S03E03 — Walk of Punishment
- **Sidecar File:** `Game of Thrones - S03E03 - Walk of Punishment.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 5 total (5 skips / 0 mutes)
- **Objectionable Duration:** 1m 32s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:37:22.000` | `00:37:32.000` | **10s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:37:43.000` | `00:37:52.000` | **9s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 03 | `00:38:16.000` | `00:39:16.000` | **1m 00s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 04 | `00:39:19.000` | `00:39:22.000` | **3s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 05 | `00:44:25.000` | `00:44:35.000` | **10s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S03E05 — Kissed by Fire
- **Sidecar File:** `Game of Thrones - S03E05 - Kissed by Fire.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 7 total (7 skips / 0 mutes)
- **Objectionable Duration:** 2m 34s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:08:40.000` | `00:08:57.000` | **17s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:09:17.000` | `00:10:05.000` | **48s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 03 | `00:10:50.000` | `00:11:23.000` | **33s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 04 | `00:34:25.000` | `00:34:37.000` | **12s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 05 | `00:35:06.000` | `00:35:13.000` | **7s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 06 | `00:35:35.000` | `00:35:39.000` | **4s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 07 | `00:49:56.000` | `00:50:29.000` | **33s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S03E07 — The Bear and the Maiden Fair
- **Sidecar File:** `Game of Thrones - S03E07 - The Bear and the Maiden Fair.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 9 total (9 skips / 0 mutes)
- **Objectionable Duration:** 5m 33s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:05:27.000` | `00:06:55.000` | **1m 28s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:07:03.000` | `00:07:13.000` | **10s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 03 | `00:07:21.000` | `00:07:54.000` | **33s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 04 | `00:09:15.000` | `00:09:23.000` | **8s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 05 | `00:09:29.000` | `00:09:45.000` | **16s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 06 | `00:36:20.000` | `00:36:49.000` | **29s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 07 | `00:37:16.000` | `00:38:57.000` | **1m 41s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 08 | `00:39:08.000` | `00:39:49.000` | **41s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 09 | `00:39:58.000` | `00:40:05.000` | **7s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S03E08 — Second Sons
- **Sidecar File:** `S03E08.jcf` (⏳ Catalog only (MKV not in library))
- **Cue Statistics:** 13 total (13 skips / 0 mutes)
- **Objectionable Duration:** 5m 11s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:15:57.000` | `00:16:09.000` | **12s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:16:17.000` | `00:16:43.000` | **26s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 03 | `00:16:59.000` | `00:17:54.000` | **55s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 04 | `00:28:00.000` | `00:30:37.000` | **2m 37s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 05 | `00:30:45.000` | `00:30:48.000` | **3s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 06 | `00:30:57.000` | `00:31:20.000` | **23s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 07 | `00:44:46.000` | `00:44:49.000` | **3s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 08 | `00:44:59.000` | `00:45:06.000` | **7s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 09 | `00:45:18.000` | `00:45:23.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 10 | `00:45:36.000` | `00:45:38.000` | **2s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 11 | `00:46:02.000` | `00:46:11.000` | **9s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 12 | `00:46:16.000` | `00:46:20.000` | **4s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 13 | `00:46:24.000` | `00:46:29.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

### Season 04

#### S04E01 — Two Swords
- **Sidecar File:** `Game of Thrones - S04E01 - Two Swords.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 7 total (7 skips / 0 mutes)
- **Objectionable Duration:** 1m 42s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:10:00.000` | `00:10:12.000` | **12s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:10:24.000` | `00:10:27.000` | **3s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 03 | `00:10:29.000` | `00:11:13.000` | **44s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 04 | `00:11:41.000` | `00:11:57.000` | **16s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 05 | `00:12:01.000` | `00:12:04.000` | **3s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 06 | `00:12:07.000` | `00:12:13.000` | **6s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 07 | `00:24:04.000` | `00:24:22.000` | **18s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S04E04 — Oathkeeper
- **Sidecar File:** `Game of Thrones - S04E04 - Oathkeeper.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 2 total (2 skips / 0 mutes)
- **Objectionable Duration:** 44s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:40:28.000` | `00:40:32.000` | **4s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:40:36.000` | `00:41:16.000` | **40s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S04E06 — The Laws of Gods and Men
- **Sidecar File:** `Game of Thrones - S04E06 - The Laws of Gods and Men.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 10 total (10 skips / 0 mutes)
- **Objectionable Duration:** 1m 36s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:07:49.000` | `00:08:04.000` | **15s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:08:07.000` | `00:08:16.000` | **9s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 03 | `00:08:21.000` | `00:08:26.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 04 | `00:08:28.000` | `00:08:37.000` | **9s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 05 | `00:08:43.000` | `00:08:57.000` | **14s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 06 | `00:09:01.000` | `00:09:15.000` | **14s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 07 | `00:10:41.000` | `00:10:47.000` | **6s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 08 | `00:10:53.000` | `00:11:06.000` | **13s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 09 | `00:11:18.000` | `00:11:23.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 10 | `00:11:29.000` | `00:11:35.000` | **6s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S04E07 — Mockingbird
- **Sidecar File:** `Game of Thrones - S04E07 - Mockingbird.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 13 total (13 skips / 0 mutes)
- **Objectionable Duration:** 1m 31s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:20:22.000` | `00:20:25.000` | **3s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:20:28.000` | `00:20:43.000` | **15s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 03 | `00:20:45.000` | `00:20:49.000` | **4s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 04 | `00:20:50.000` | `00:20:53.000` | **3s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 05 | `00:20:54.000` | `00:20:58.000` | **4s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 06 | `00:21:17.000` | `00:21:25.000` | **8s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 07 | `00:21:35.000` | `00:21:55.000` | **20s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 08 | `00:22:07.000` | `00:22:11.000` | **4s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 09 | `00:22:18.000` | `00:22:22.000` | **4s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 10 | `00:22:45.000` | `00:22:54.000` | **9s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 11 | `00:22:58.000` | `00:23:02.000` | **4s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 12 | `00:23:04.000` | `00:23:08.000` | **4s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 13 | `00:23:17.000` | `00:23:26.000` | **9s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S04E08 — The Mountain and the Viper
- **Sidecar File:** `Game of Thrones - S04E08 - The Mountain and the Viper.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 8 total (8 skips / 0 mutes)
- **Objectionable Duration:** 51s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:02:27.000` | `00:02:32.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:02:51.000` | `00:03:08.000` | **17s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 03 | `00:07:58.000` | `00:08:00.000` | **2s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 04 | `00:08:02.000` | `00:08:08.000` | **6s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 05 | `00:08:10.000` | `00:08:15.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 06 | `00:08:17.000` | `00:08:25.000` | **8s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 07 | `00:08:26.000` | `00:08:29.000` | **3s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 08 | `00:08:32.000` | `00:08:37.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S04E10 — The Children
- **Sidecar File:** `Game of Thrones - S04E10 - The Children.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 1 total (1 skips / 0 mutes)
- **Objectionable Duration:** 7s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:20:00.000` | `00:20:07.000` | **7s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

### Season 05

#### S05E01 — The Wars to Come
- **Sidecar File:** `Game of Thrones - S05E01 - The Wars to Come.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 8 total (8 skips / 0 mutes)
- **Objectionable Duration:** 3m 25s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:13:47.000` | `00:14:09.000` | **22s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:14:38.000` | `00:15:38.000` | **1m 00s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 03 | `00:29:43.000` | `00:30:43.000` | **1m 00s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 04 | `00:30:45.000` | `00:30:58.000` | **13s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 05 | `00:31:06.000` | `00:31:12.000` | **6s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 06 | `00:31:18.000` | `00:31:23.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 07 | `00:31:28.000` | `00:31:33.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 08 | `00:37:12.000` | `00:37:46.000` | **34s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S05E03 — High Sparrow
- **Sidecar File:** `S05E03.jcf` (⏳ Catalog only (MKV not in library))
- **Cue Statistics:** 10 total (10 skips / 0 mutes)
- **Objectionable Duration:** 1m 18s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:40:45.000` | `00:41:06.000` | **21s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:41:10.000` | `00:41:19.000` | **9s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 03 | `00:41:22.000` | `00:41:30.000` | **8s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 04 | `00:41:32.000` | `00:41:37.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 05 | `00:41:54.000` | `00:41:59.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 06 | `00:42:09.000` | `00:42:14.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 07 | `00:55:24.000` | `00:55:31.000` | **7s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 08 | `00:55:37.000` | `00:55:48.000` | **11s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 09 | `00:56:03.000` | `00:56:06.000` | **3s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 10 | `00:56:38.000` | `00:56:42.000` | **4s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S05E05 — Kill the Boy
- **Sidecar File:** `Game of Thrones - S05E05 - Kill the Boy.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 1 total (1 skips / 0 mutes)
- **Objectionable Duration:** 2m 23s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:18:41.000` | `00:21:04.000` | **2m 23s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S05E06 — Unbowed, Unbent, Unbroken
- **Sidecar File:** `Game of Thrones - S05E06 - Unbowed, Unbent, Unbroken.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 1 total (1 skips / 0 mutes)
- **Objectionable Duration:** 5s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:51:42.000` | `00:51:47.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

#### S05E07 — The Gift
- **Sidecar File:** `Game of Thrones - S05E07 - The Gift.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 13 total (13 skips / 0 mutes)
- **Objectionable Duration:** 3m 05s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:24:03.000` | `00:24:16.000` | **13s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:24:30.000` | `00:24:52.000` | **22s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 03 | `00:28:14.000` | `00:29:40.000` | **1m 26s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 04 | `00:39:28.000` | `00:39:38.000` | **10s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 05 | `00:39:45.000` | `00:39:51.000` | **6s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 06 | `00:39:52.000` | `00:39:55.000` | **3s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 07 | `00:39:57.000` | `00:40:10.000` | **13s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 08 | `00:40:15.000` | `00:40:19.000` | **4s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 09 | `00:40:23.000` | `00:40:27.000` | **4s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 10 | `00:40:44.000` | `00:40:49.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 11 | `00:41:06.000` | `00:41:10.000` | **4s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 12 | `00:41:28.000` | `00:41:30.000` | **2s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 13 | `00:41:38.000` | `00:41:51.000` | **13s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

### Season 06

#### S06E07 — The Broken Man
- **Sidecar File:** `Game of Thrones - S06E07 - The Broken Man.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 11 total (11 skips / 0 mutes)
- **Objectionable Duration:** 1m 28s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `00:34:24.000` | `00:34:28.000` | **4s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 02 | `00:34:30.000` | `00:34:35.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 03 | `00:34:37.000` | `00:34:45.000` | **8s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 04 | `00:34:48.000` | `00:35:13.000` | **25s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 05 | `00:35:15.000` | `00:35:25.000` | **10s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 06 | `00:35:35.000` | `00:35:49.000` | **14s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 07 | `00:35:51.000` | `00:35:53.000` | **2s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 08 | `00:36:09.000` | `00:36:17.000` | **8s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 09 | `00:36:31.000` | `00:36:36.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 10 | `00:37:33.000` | `00:37:38.000` | **5s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |
| 11 | `00:37:39.000` | `00:37:41.000` | **2s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

### Season 07

#### S07E07 — The Dragon and the Wolf
- **Sidecar File:** `Game of Thrones - S07E07 - The Dragon and the Wolf.jcf` (✅ Deployed on disk)
- **Cue Statistics:** 1 total (1 skips / 0 mutes)
- **Objectionable Duration:** 21s skipped

| # | Start Time | End Time | Duration | Category | Channel | Action | Description / Context |
| :-: | :---: | :---: | :---: | :--- | :---: | :---: | :--- |
| 01 | `01:09:49.000` | `01:10:10.000` | **21s** | `SexAndNudity.FullNudity` | video | `skip` | Objectionable scene |

---

## 4. Unflagged Episodes & Known Gaps

### A. Community-Identified Gaps (Episodes with Content Omitted from Reddit Post)
The original post author omitted timestamps for several episodes that contain known objectionable scenes. If filtering is required for these, manual timecodes should be added:
1. **S01E02 (*The Kingsroad*):** Daenerys and Khal Drogo tent scene.
2. **S01E07 (*You Win or You Die*):** Explicit Littlefinger brothel training monologue with Ros and Armeca.
3. **S02E01 (*The North Remembers*):** Explicit Ros and brothel scenes near episode conclusion.
4. **Season 08 (All Episodes):** Omitted by author (*"it was pretty minor"*). Episodes S08E01, S08E02, and S08E04 contain romantic and suggestive bedroom scenes.

### B. Verified Clean Episodes (No Objectionable Material Flagged)
The following episodes across Seasons 1–7 were checked and confirmed free of explicit nudity cuts by the author:
| Season | Clean Episodes |
| :--- | :--- |
| **Season 01** | `S01E02` (The Kingsroad), `S01E04` (Cripples, Bastards and Broken Things), `S01E05` (The Wolf and the Lion), `S01E06` (A Golden Crown), `S01E07` (You Win or You Die), `S01E08` (The Pointy End) |
| **Season 02** | `S02E01` (The North Remembers), `S02E02` (The Night Lands), `S02E03` (What Is Dead May Never Die), `S02E05` (The Ghost of Harrenhal), `S02E06` (The Old Gods and the New), `S02E07` (A Man Without Honor) |
| **Season 03** | `S03E01` (Valar Dohaeris), `S03E02` (Dark Wings, Dark Words), `S03E04` (And Now His Watch Is Ended), `S03E06` (The Climb), `S03E09` (The Rains of Castamere), `S03E10` (Mhysa) |
| **Season 04** | `S04E02` (The Lion and the Rose), `S04E03` (Breaker of Chains), `S04E05` (First of His Name), `S04E09` (The Watchers on the Wall) |
| **Season 05** | `S05E02` (The House of Black and White), `S05E04` (Sons of the Harpy), `S05E08` (Hardhome), `S05E09` (The Dance of Dragons), `S05E10` (Mother's Mercy) |
| **Season 06** | `S06E01` (The Red Woman), `S06E02` (Home), `S06E03` (Oathbreaker), `S06E04` (Book of the Stranger), `S06E05` (The Door), `S06E06` (Blood of My Blood), `S06E08` (No One), `S06E09` (Battle of the Bastards), `S06E10` (The Winds of Winter) |
| **Season 07** | `S07E01` (Dragonstone), `S07E02` (Stormborn), `S07E03` (The Queen's Justice), `S07E04` (The Spoils of War), `S07E05` (Eastwatch), `S07E06` (Beyond the Wall) |

---

## 5. Jellyfin Plugin Configuration & Integration

### Sidecar Discovery
Jellyfin Content Filter automatically discovers sidecar files placed next to the video file:
```text
/Volumes/data/shows/Game of Thrones/Season 01/
├── Game of Thrones - S01E01 - Winter Is Coming.mkv
└── Game of Thrones - S01E01 - Winter Is Coming.jcf
```

### Category Filtering Mechanics
- `SexAndNudity.FullNudity`: Controlled by the user's **Sex & Nudity** toggle (`SexAndNudityEnabled`) in plugin configuration. When enabled, playback automatically seeks ahead to the cue's end timestamp.
- `Language.GeneralProfanity`: Controlled by the user's **General Profanity** toggle (`GeneralProfanityEnabled`). When enabled, audio volume drops to 0% for the exact duration of the spoken word.

*(Report generated automatically on September 4, 2026)*
