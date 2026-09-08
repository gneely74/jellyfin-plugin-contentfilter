# Repository Rules: Release Tagging, GitHub Push, and Code Documentation

## Mandatory Comprehensive Code Documentation Protocol for All Agents

Whenever any agent creates, modifies, refactors, or fixes code in this repository, the agent **MUST** adhere to the following code documentation standards without exception:

1. **100% Documentation Coverage**:
   - **C# Code**:
     - Every class, interface, struct, enum, record, constructor, method (both public and private/internal), property, and event must have full XML doc comments (`/// <summary>`, `/// <param name="...">`, `/// <returns>`, `/// <exception cref="...">`, and `/// <remarks>` where applicable).
     - Parameter descriptions must clearly explain semantics, units, nullability, and expected ranges.
     - Return descriptions must clearly explain returned data types, default/null behavior, and error results.
   - **JavaScript Code (`Web/client.js`)**:
     - Every function and callback must have complete JSDoc comments (`/** ... */`) documenting purpose, `@param`, `@returns`, and side effects.
   - **Python Scripts (`tools/`, root scripts)**:
     - Every function, class, and module must have standard docstrings with `Args:`, `Returns:`, and `Raises:`.

2. **Continuous Documentation Maintenance on Code Changes**:
   - Whenever modifying an existing function or class:
     - The agent **MUST** inspect and update the existing documentation to reflect all signature changes, modified parameter semantics, altered return behavior, or newly thrown exceptions.
     - Never leave stale or outdated documentation comments after changing implementation details.
   - Whenever adding a new function, method, or helper:
     - The agent **MUST** write full XML doc / JSDoc / docstring comments immediately upon creation. No undocumented functions may be introduced.

3. **Inline Explanatory Comments for Complex Logic**:
   - Non-trivial logic must include concise, high-value inline comments explaining *why* decisions were made, including:
     - External process invocations (`ffmpeg`, `ffprobe`, process argument mappings, exit code interpretations).
     - Concurrency and lock synchronization (e.g. `_syncLock`, bounded channels, task cancellation tokens).
     - GPU arbitration protocols (`pause-preload` flags, settling loops, Ollama model eviction requests).
     - Subtitle timecode math and conversions (millisecond vs. second conversions, ISO vs. SRT timecode formats).
     - SQLite schema migrations, indexing, and transactional operations.
     - Regex parsing, boundary matching, and profanity masking rules.

---

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
