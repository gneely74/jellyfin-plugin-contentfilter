# Repository Rules: Release Tagging and GitHub Push

## Mandatory Release Workflow on GitHub Push

Whenever pushing changes to GitHub (`origin main`), you **MUST** always update the release version, plugin package, `manifest.json`, and GitHub release tag so that Jellyfin servers can immediately detect and install the updated plugin.

---

### Step-by-Step Release Protocol

1. **Version Bump**:
   - Determine the next semantic version (e.g., `1.0.1.0` → `1.0.2.0`, corresponding to tag `v1.0.2`).
   - Update `VERSION` in `Makefile`.
   - Update `version`, `changelog`, and `timestamp` in `meta.json`.

2. **Package & Checksum Verification**:
   - Run `make clean && make package` to produce `dist/Jellyfin.Plugin.ContentFilter_<VERSION>.zip`.
   - Calculate the MD5 checksum:
     ```bash
     md5 -q dist/Jellyfin.Plugin.ContentFilter_<VERSION>.zip
     ```
   - Update `manifest.json`:
     - Insert or update the top entry in `versions` with the new version number.
     - Set `sourceUrl` to:
       `https://github.com/gneely74/jellyfin-plugin-contentfilter/releases/download/v<TAG_VERSION>/Jellyfin.Plugin.ContentFilter_<VERSION>.zip`
     - Set `checksum` to the verified MD5 hash.
     - Set `timestamp` to current UTC date (`YYYY-MM-DDT00:00:00Z`).

3. **Pre-Push Validation & Commit**:
   - Run tests and ensure clean builds:
     ```bash
     dotnet build Jellyfin.Plugin.ContentFilter/ContentFilter.csproj -c Release
     pytest
     ```
   - Commit all changes including `Makefile`, `meta.json`, and `manifest.json`.
   - Push commit to GitHub `main`:
     ```bash
     git push origin main
     ```

4. **Tag & Publish GitHub Release**:
   - Immediately publish the GitHub Release with the packaged zip asset:
     ```bash
     gh release create v<TAG_VERSION> dist/Jellyfin.Plugin.ContentFilter_<VERSION>.zip \
       --title "v<TAG_VERSION>" \
       --notes "<Changelog summary>"
     ```
   - Verify that the release asset MD5 matches `manifest.json`.

---

### Critical Constraints
- **Never push code modifications to `main` without updating the release tag and manifest.**
- Jellyfin clients poll `manifest.json` on the `main` branch to discover updates. If code is pushed without a corresponding tag and release asset, Jellyfin will not pull or install the changes.
