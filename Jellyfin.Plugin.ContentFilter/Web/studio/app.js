(() => {
  "use strict";

  const FRAME_STEP = 1 / 24;

  const studioPathIdx = window.location.pathname.indexOf("/ContentFilter/studio");
  const isHostedOnJcf = studioPathIdx !== -1;
  const hostedBaseUrl = isHostedOnJcf
    ? `${window.location.origin}${window.location.pathname.slice(0, studioPathIdx)}`
    : "";

  function readJellyfinWebToken() {
    try {
      const raw = localStorage.getItem("jellyfin_credentials");
      if (!raw) return "";
      const creds = JSON.parse(raw);
      const servers = creds?.Servers || [];
      const match = servers.find((s) => s && s.AccessToken);
      return match?.AccessToken || "";
    } catch {
      return "";
    }
  }

  const state = {
    serverUrl: localStorage.getItem("jcf_serverUrl") || (isHostedOnJcf ? hostedBaseUrl : "http://localhost:8096"),
    apiKey: localStorage.getItem("jcf_apiKey") || readJellyfinWebToken() || "",
    useProxy: isHostedOnJcf ? false : localStorage.getItem("jcf_useProxy") !== "false",
    itemId: localStorage.getItem("jcf_itemId") || "",
    title: "",
    year: "",
    imdbUrl: "",
    cues: [],
    srtLines: [],
    jcfWords: [],
    editingCueKey: null,
    inTime: null,
    outTime: null,
    zoom: 1,
    popoverContext: null,
    triedTranscodeFallback: false,
    hlsInstance: null,
    pendingSeek: null,
    virtualTime: 0,
  };

  // DOM helpers
  const $ = (id) => document.getElementById(id);
  const video = $("videoPlayer");

  function init() {
    $("serverUrl").value = state.serverUrl;
    $("apiKey").value = state.apiKey;
    $("useProxy").checked = state.useProxy;
    $("itemIdInput").value = state.itemId;

    bindEvents();
    renderTimeline();
    renderCuesList();
    renderTranscript();
  }

  function bindEvents() {
    $("serverUrl").addEventListener("change", (e) => {
      state.serverUrl = e.target.value.trim().replace(/\/+$/, "");
      localStorage.setItem("jcf_serverUrl", state.serverUrl);
    });
    $("apiKey").addEventListener("change", (e) => {
      state.apiKey = e.target.value.trim();
      localStorage.setItem("jcf_apiKey", state.apiKey);
    });
    $("useProxy").addEventListener("change", (e) => {
      state.useProxy = e.target.checked;
      localStorage.setItem("jcf_useProxy", String(state.useProxy));
    });
    $("itemIdInput").addEventListener("change", (e) => {
      state.itemId = e.target.value.trim();
      localStorage.setItem("jcf_itemId", state.itemId);
    });

    $("btnCloseStatus").addEventListener("click", () => $("statusBanner").classList.add("hidden"));
    $("btnPullAll").addEventListener("click", () => pullAllFromJcf());
    $("btnBrowseLibrary").addEventListener("click", openLibraryModal);
    $("btnCloseLibraryModal").addEventListener("click", closeLibraryModal);
    document.querySelector("#libraryModal .modal-backdrop").addEventListener("click", closeLibraryModal);

    $("btnSignIn").addEventListener("click", openLoginModal);
    $("btnCloseLoginModal").addEventListener("click", closeLoginModal);
    document.querySelector("#loginModal .modal-backdrop").addEventListener("click", closeLoginModal);
    $("loginForm").addEventListener("submit", onLoginSubmit);

    $("btnSearchLibrary").addEventListener("click", () => searchLibrary($("librarySearchInput").value.trim()));
    $("librarySearchInput").addEventListener("keydown", (e) => {
      if (e.key === "Enter") searchLibrary($("librarySearchInput").value.trim());
    });
    $("btnLoadShows").addEventListener("click", loadShows);
    $("btnLoadMovies").addEventListener("click", loadMovies);

    // Local file loaders
    $("localVideoInput").addEventListener("change", handleLocalVideo);
    $("localSrtInput").addEventListener("change", handleLocalSrt);
    $("localJcfInput").addEventListener("change", handleLocalJcf);
    $("btnExportJcf").addEventListener("click", exportJcfFile);

    // Transport controls
    $("btnPlayPause").addEventListener("click", togglePlay);
    $("btnStepBackSec").addEventListener("click", () => seekBy(-1));
    $("btnStepFwdSec").addEventListener("click", () => seekBy(1));
    $("btnStepBackFrame").addEventListener("click", () => seekBy(-FRAME_STEP));
    $("btnStepFwdFrame").addEventListener("click", () => seekBy(FRAME_STEP));
    $("playbackRate").addEventListener("change", (e) => {
      video.playbackRate = parseFloat(e.target.value) || 1;
    });
    $("btnSetIn").addEventListener("click", markIn);
    $("btnSetOut").addEventListener("click", markOut);

    // Video events
    video.addEventListener("timeupdate", onVideoTimeUpdate);
    video.addEventListener("loadedmetadata", () => {
      $("durationTimecode").textContent = formatTimecode(video.duration || 0);
      if (state.pendingSeek !== null) {
        const target = state.pendingSeek;
        state.pendingSeek = null;
        video.currentTime = target;
      }
      renderTimeline();
    });
    video.addEventListener("play", () => { $("btnPlayPause").textContent = "Pause"; });
    video.addEventListener("pause", () => { $("btnPlayPause").textContent = "Play"; });
    video.addEventListener("error", onVideoError);

    // Timeline interactions
    $("timelineZoom").addEventListener("input", (e) => {
      state.zoom = parseFloat(e.target.value) || 1;
      $("timelineZoomVal").textContent = `${state.zoom}x`;
      renderTimeline();
    });
    $("timelineCanvas").addEventListener("click", onTimelineClick);

    // NudeNet & Whisper
    $("btnRunNudeNet").addEventListener("click", runNudeNetScan);
    $("btnTranscribeSrt").addEventListener("click", runWhisperTranscribe);
    $("btnRefreshWords").addEventListener("click", () => state.itemId && fetchJcfWords(state.itemId));

    // Cue Form
    $("cueCategory").addEventListener("change", onCategoryChange);
    $("cueForm").addEventListener("submit", onCueFormSubmit);
    $("btnClearForm").addEventListener("click", resetCueForm);
    $("btnDeleteEditingCue").addEventListener("click", deleteEditingCue);
    document.querySelectorAll(".btn-nudge").forEach((btn) => {
      btn.addEventListener("click", () => {
        const input = $(btn.dataset.target);
        const delta = parseFloat(btn.dataset.delta);
        const next = Math.max(0, parseTimecode(input.value) + delta);
        input.value = formatTimecode(next);
        syncInOutFromForm();
      });
    });

    // Sidebar tabs
    document.querySelectorAll(".tab-btn").forEach((btn) => {
      btn.addEventListener("click", () => {
        document.querySelectorAll(".tab-btn").forEach((b) => b.classList.remove("active"));
        document.querySelectorAll(".tab-panel").forEach((p) => p.classList.remove("active"));
        btn.classList.add("active");
        $(btn.dataset.tab).classList.add("active");
      });
    });

    // Transcript & Cue Filters
    $("wordSearchInput").addEventListener("input", renderTranscript);
    $("btnMuteAllMatches").addEventListener("click", muteAllSearchMatches);
    $("cueFilterInput").addEventListener("input", renderCuesList);
    $("cueFilterChannel").addEventListener("change", renderCuesList);

    // Popover actions
    $("btnMarkWordOnce").addEventListener("click", () => markPopoverWord(false));
    $("btnMarkWordBlanket").addEventListener("click", () => markPopoverWord(true));
    $("btnLoadWordInForm").addEventListener("click", loadPopoverWordInForm);
    document.addEventListener("click", (e) => {
      const pop = $("wordPopover");
      if (!pop.classList.contains("hidden") && !pop.contains(e.target) && !e.target.classList.contains("srt-word")) {
        pop.classList.add("hidden");
      }
    });

    // Keyboard shortcuts
    document.addEventListener("keydown", onKeyDown);
  }

  function setStatus(msg, isError = false) {
    const banner = $("statusBanner");
    $("statusText").textContent = msg;
    banner.classList.toggle("error", isError);
    banner.classList.remove("hidden");
  }

  // --- API & Proxy Helpers ---
  function buildTargetUrl(path) {
    const base = ($("serverUrl").value || state.serverUrl).trim().replace(/\/+$/, "");
    return `${base}${path.startsWith("/") ? "" : "/"}${path}`;
  }

  function buildFetchUrl(path) {
    const target = buildTargetUrl(path);
    if ($("useProxy").checked && window.location.protocol.startsWith("http")) {
      return `/proxy?url=${encodeURIComponent(target)}`;
    }
    return target;
  }

  async function apiFetch(path, options = {}) {
    const url = buildFetchUrl(path);
    const headers = new Headers(options.headers || {});
    const key = ($("apiKey").value || state.apiKey).trim();
    if (key) {
      headers.set("X-Emby-Token", key);
    }
    if (options.body && !headers.has("Content-Type")) {
      headers.set("Content-Type", "application/json");
    }
    const resp = await fetch(url, { ...options, headers });
    if (!resp.ok) {
      const txt = await resp.text().catch(() => "");
      if (resp.status === 401) {
        openLoginModal("Session expired or missing API key. Please sign in to Jellyfin.");
      }
      throw new Error(`HTTP ${resp.status}: ${txt || resp.statusText}`);
    }
    const ct = resp.headers.get("Content-Type") || "";
    if (ct.includes("application/json")) {
      return resp.json();
    }
    return resp.text();
  }

  // --- Pull Everything from JCF ---
  async function pullAllFromJcf(itemIdOverride) {
    const itemId = (itemIdOverride || $("itemIdInput").value).trim();
    if (!itemId) {
      setStatus("Enter an Item GUID or click Browse Library first.", true);
      return;
    }
    state.itemId = itemId;
    state.triedTranscodeFallback = false;
    $("itemIdInput").value = itemId;
    localStorage.setItem("jcf_itemId", itemId);

    setStatus(`Pulling JCF, SRT, and words for ${itemId}...`);

    // 1. Load Video Stream
    loadJellyfinVideo(itemId, false);

    // 2. Pull JCF cues, SRT, and Words in parallel
    const results = await Promise.allSettled([
      fetchJcfCues(itemId),
      fetchSrtSubtitles(itemId),
      fetchJcfWords(itemId),
    ]);

    const summary = [];
    if (results[0].status === "fulfilled") summary.push(`${state.cues.length} JCF cues`);
    if (results[1].status === "fulfilled") summary.push(`${state.srtLines.length} SRT lines`);
    if (results[2].status === "fulfilled") summary.push(`${state.jcfWords.length} detected word groups`);

    setStatus(summary.length ? `Loaded: ${summary.join(" • ")}` : "Connected, but no JCF/SRT data found yet for this item.");
  }

  function destroyHls() {
    if (state.hlsInstance) {
      state.hlsInstance.destroy();
      state.hlsInstance = null;
    }
  }

  function loadJellyfinVideo(itemId, useHls) {
    destroyHls();
    const key = encodeURIComponent(($("apiKey").value || state.apiKey).trim());
    if (!useHls) {
      const path = `/Videos/${itemId}/stream?static=true&api_key=${key}`;
      video.src = buildFetchUrl(path);
      video.load();
      return;
    }

    const playSessionId = `jcfstudio_${Date.now()}`;
    const hlsPath = `/Videos/${itemId}/master.m3u8?MediaSourceId=${itemId}&VideoCodec=h264&AudioCodec=aac&VideoBitrate=8000000&AudioBitrate=192000&MaxStreamingBitrate=8000000&TranscodingMaxAudioChannels=2&SegmentContainer=ts&MinSegments=1&BreakOnNonKeyFrames=True&PlaySessionId=${playSessionId}&api_key=${key}`;
    const hlsUrl = buildFetchUrl(hlsPath);

    if (video.canPlayType("application/vnd.apple.mpegurl")) {
      video.src = hlsUrl;
      video.load();
    } else if (window.Hls && window.Hls.isSupported()) {
      const hls = new window.Hls({ enableWorker: true });
      state.hlsInstance = hls;
      hls.loadSource(hlsUrl);
      hls.attachMedia(video);
      hls.on(window.Hls.Events.ERROR, (_ev, data) => {
        if (data.fatal) {
          setStatus("HLS transcode stream error. You can also load a local MP4/WebM via the Video button.", true);
        }
      });
    } else {
      const mp4Path = `/Videos/${itemId}/stream.mp4?Static=false&MediaSourceId=${itemId}&VideoCodec=h264&AudioCodec=aac&VideoBitrate=8000000&AudioBitrate=192000&MaxStreamingBitrate=8000000&TranscodingMaxAudioChannels=2&api_key=${key}`;
      video.src = buildFetchUrl(mp4Path);
      video.load();
    }
  }

  function onVideoError() {
    if (state.itemId && !state.triedTranscodeFallback && video.src.includes("/Videos/")) {
      state.triedTranscodeFallback = true;
      setStatus("Direct stream unsupported by browser; switching to HLS H.264/AAC transcoded stream...");
      loadJellyfinVideo(state.itemId, true);
      return;
    }
    if (video.src) {
      setStatus("Browser could not play this video stream. You can load a local MP4/WebM using the Video button.", true);
    }
  }

  async function fetchJcfCues(itemId) {
    try {
      const data = await apiFetch(`/ContentFilter/filters/${itemId}`);
      state.title = data.title || state.title || "";
      state.year = data.year || "";
      state.imdbUrl = data.imdbUrl || "";
      state.cues = (data.cues || []).map(normalizeCue);
    } catch (err) {
      if (String(err.message).includes("404")) {
        state.cues = [];
      } else {
        throw err;
      }
    }
    renderTimeline();
    renderCuesList();
    renderTranscript();
  }

  async function fetchSrtSubtitles(itemId) {
    try {
      const srtText = await apiFetch(`/ContentFilter/subtitles/${itemId}.srt`);
      state.srtLines = parseSrt(srtText);
    } catch {
      state.srtLines = [];
    }
    renderTranscript();
  }

  async function fetchJcfWords(itemId) {
    try {
      const data = await apiFetch(`/ContentFilter/subtitles/${itemId}/words?language=eng`);
      state.jcfWords = data.words || data.Words || [];
    } catch {
      state.jcfWords = [];
    }
    renderJcfWords();
  }

  // --- NudeNet & Whisper Actions ---
  async function runNudeNetScan() {
    if (!state.itemId) {
      setStatus("Select a Jellyfin item first to run NudeNet scan.", true);
      return;
    }
    try {
      setStatus("Starting NudeNet visual nudity scan on Jellyfin...");
      const res = await apiFetch(`/ContentFilter/nudity/${state.itemId}/scan?force=true`, { method: "POST" });
      await fetchJcfCues(state.itemId);
      const count = res?.cuesAdded ?? res?.CuesAdded ?? "";
      setStatus(`NudeNet scan finished${count !== "" ? ` (${count} cues added)` : ""}. Timeline refreshed.`);
    } catch (err) {
      setStatus(`NudeNet scan failed: ${err.message}`, true);
    }
  }

  async function runWhisperTranscribe() {
    if (!state.itemId) {
      setStatus("Select a Jellyfin item first to run Whisper transcription.", true);
      return;
    }
    try {
      setStatus("Running Whisper AI transcription on Jellyfin (this may take a moment)...");
      await apiFetch(`/ContentFilter/subtitles/${state.itemId}/transcribe?force=false`, { method: "POST" });
      await Promise.allSettled([fetchSrtSubtitles(state.itemId), fetchJcfWords(state.itemId)]);
      setStatus("Whisper transcription complete. Subtitles & words refreshed.");
    } catch (err) {
      setStatus(`Whisper transcription failed: ${err.message}`, true);
    }
  }

  // --- Sign In Modal ---
  function openLoginModal(errMsg) {
    const errEl = $("loginError");
    if (typeof errMsg === "string" && errMsg) {
      errEl.textContent = errMsg;
      errEl.classList.remove("hidden");
    } else {
      errEl.classList.add("hidden");
    }
    $("loginModal").classList.remove("hidden");
    $("loginUsername").focus();
  }

  function closeLoginModal() {
    $("loginModal").classList.add("hidden");
  }

  async function onLoginSubmit(e) {
    e.preventDefault();
    const errEl = $("loginError");
    errEl.classList.add("hidden");
    const username = $("loginUsername").value.trim();
    const password = $("loginPassword").value;

    try {
      const url = buildFetchUrl("/Users/AuthenticateByName");
      const resp = await fetch(url, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Emby-Authorization": 'MediaBrowser Client="JCF Studio", Device="Browser", DeviceId="jcf-studio-web", Version="1.0.47"',
        },
        body: JSON.stringify({ Username: username, Pw: password }),
      });
      if (!resp.ok) {
        const txt = await resp.text().catch(() => "");
        throw new Error(`HTTP ${resp.status}: ${txt || resp.statusText}`);
      }
      const data = await resp.json();
      const token = data.AccessToken;
      if (!token) throw new Error("No AccessToken returned by Jellyfin.");

      state.apiKey = token;
      $("apiKey").value = token;
      localStorage.setItem("jcf_apiKey", token);
      closeLoginModal();
      setStatus(`Signed in as ${data.User?.Name || username}.`);
    } catch (err) {
      errEl.textContent = err.message;
      errEl.classList.remove("hidden");
    }
  }

  // --- Library Browser Modal ---
  function openLibraryModal() {
    $("libraryModal").classList.remove("hidden");
    $("librarySearchInput").focus();
  }

  function closeLibraryModal() {
    $("libraryModal").classList.add("hidden");
  }

  async function searchLibrary(query) {
    if (!query) return loadMovies();
    try {
      const items = await apiFetch(`/ContentFilter/items/search?query=${encodeURIComponent(query)}`);
      renderLibraryItems(items || [], "Search Results");
    } catch (err) {
      setStatus(`Library search failed: ${err.message}`, true);
    }
  }

  async function loadShows() {
    try {
      const shows = await apiFetch("/ContentFilter/library/shows");
      renderLibraryItems(shows || [], "TV Shows", true);
    } catch (err) {
      setStatus(`Failed to load shows: ${err.message}`, true);
    }
  }

  async function loadMovies() {
    try {
      const movies = await apiFetch("/ContentFilter/library/movies");
      renderLibraryItems(movies || [], "Movies");
    } catch (err) {
      setStatus(`Failed to load movies: ${err.message}`, true);
    }
  }

  async function loadEpisodes(seriesId, seriesName) {
    try {
      const data = await apiFetch(`/ContentFilter/series/${seriesId}/episodes`);
      const seasons = data?.seasons || [];
      const flattened = [];
      seasons.forEach((season) => {
        (season.episodes || []).forEach((ep) => {
          const sNum = String(ep.seasonNumber ?? season.seasonNumber ?? 0).padStart(2, "0");
          const eNum = String(ep.episodeNumber ?? 0).padStart(2, "0");
          flattened.push({
            ...ep,
            name: `S${sNum}E${eNum} — ${ep.name || "Episode"}`,
            type: ep.cuesCount ? `${ep.cuesCount} cues` : "Episode",
          });
        });
      });
      renderLibraryItems(flattened, `Episodes — ${data?.seriesName || seriesName}`);
    } catch (err) {
      setStatus(`Failed to load episodes: ${err.message}`, true);
    }
  }

  function renderLibraryItems(items, label, isShows = false) {
    const bc = $("libraryBreadcrumb");
    bc.textContent = `${label} (${items.length})`;
    bc.classList.remove("hidden");

    const container = $("libraryResults");
    if (!items.length) {
      container.innerHTML = `<div class="empty-state">No items found.</div>`;
      return;
    }

    container.innerHTML = "";
    items.forEach((item) => {
      const id = item.id || item.Id;
      const name = item.name || item.Name || item.title || "Untitled";
      const type = item.type || item.Type || (isShows ? "Series" : "Video");
      const year = item.productionYear || item.ProductionYear || item.year || "";
      const hasFilter = item.hasFilter || item.HasFilter;

      const card = document.createElement("div");
      card.className = "lib-card";
      card.innerHTML = `
        <div class="lib-card-title">${escapeHtml(name)}</div>
        <div class="lib-card-sub">${escapeHtml(type)}${year ? ` • ${year}` : ""}${hasFilter ? " • Has JCF" : ""}</div>
      `;
      card.addEventListener("click", () => {
        if (type === "Series" || isShows) {
          loadEpisodes(id, name);
        } else {
          state.title = name;
          closeLibraryModal();
          pullAllFromJcf(id);
        }
      });
      container.appendChild(card);
    });
  }

  // --- Local File Handlers ---
  function handleLocalVideo(e) {
    const file = e.target.files?.[0];
    if (!file) return;
    video.src = URL.createObjectURL(file);
    video.load();
    if (!state.title) state.title = file.name.replace(/\.[^.]+$/, "");
    setStatus(`Loaded local video: ${file.name}`);
  }

  async function handleLocalSrt(e) {
    const file = e.target.files?.[0];
    if (!file) return;
    const text = await file.text();
    state.srtLines = parseSrt(text);
    renderTranscript();
    setStatus(`Loaded ${state.srtLines.length} subtitle lines from ${file.name}`);
  }

  async function handleLocalJcf(e) {
    const file = e.target.files?.[0];
    if (!file) return;
    const text = await file.text();
    const parsed = parseJcf(text);
    state.title = parsed.title || state.title;
    state.year = parsed.year || state.year;
    state.cues = parsed.cues;
    renderTimeline();
    renderCuesList();
    renderTranscript();
    setStatus(`Imported ${state.cues.length} cues from ${file.name}`);
  }

  function exportJcfFile() {
    const lines = ["WEBVTT JCF", "", "NOTE"];
    lines.push(`TITLE ${state.title || "Untitled"}`);
    if (state.year) lines.push(`YEAR ${state.year}`);
    if (state.imdbUrl) lines.push(`IMDB ${state.imdbUrl}`);
    lines.push("SOURCE JCF Studio");
    lines.push("");

    const sorted = [...state.cues].sort((a, b) => a.startSec - b.startSec);
    for (const cue of sorted) {
      lines.push(`${formatTimecode(cue.startSec)} --> ${formatTimecode(cue.endSec)}`);
      lines.push(`category: ${cue.category}`);
      lines.push(`channel: ${cue.channel}`);
      lines.push(`action: ${cue.action}`);
      if (cue.precision) lines.push(`precision: ${cue.precision}`);
      if (cue.description) lines.push(`description: ${cue.description}`);
      lines.push("");
    }

    const blob = new Blob([lines.join("\n")], { type: "text/vtt;charset=utf-8" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    const safeName = (state.title || state.itemId || "contentfilter").replace(/[^a-z0-9._-]+/gi, "_");
    a.download = `${safeName}.jcf`;
    a.click();
    URL.revokeObjectURL(a.href);
  }

  // --- Transport & Filter Preview ---
  function togglePlay() {
    if (video.paused) video.play().catch(() => {});
    else video.pause();
  }

  function seekTo(sec) {
    const dur = video.duration || getFallbackDuration();
    const clamped = Math.max(0, Math.min(dur || 1e9, sec));
    state.virtualTime = clamped;
    if (video.readyState >= 1) {
      video.currentTime = clamped;
    } else {
      state.pendingSeek = clamped;
      updateTimecodeAndPlayhead(clamped);
    }
  }

  function seekBy(deltaSec) {
    const current = video.readyState >= 1 ? (video.currentTime || 0) : state.virtualTime;
    seekTo(current + deltaSec);
  }

  function markIn() {
    const t = video.readyState >= 1 ? (video.currentTime || 0) : state.virtualTime;
    state.inTime = t;
    $("cueStart").value = formatTimecode(t);
    if (state.outTime === null || state.outTime <= t) {
      state.outTime = t + 2.0;
      $("cueEnd").value = formatTimecode(state.outTime);
    }
    renderInOutRange();
  }

  function markOut() {
    const t = video.readyState >= 1 ? (video.currentTime || 0) : state.virtualTime;
    state.outTime = t;
    $("cueEnd").value = formatTimecode(t);
    if (state.inTime === null || state.inTime >= t) {
      state.inTime = Math.max(0, t - 2.0);
      $("cueStart").value = formatTimecode(state.inTime);
    }
    renderInOutRange();
  }

  function syncInOutFromForm() {
    state.inTime = parseTimecode($("cueStart").value);
    state.outTime = parseTimecode($("cueEnd").value);
    renderInOutRange();
  }

  function updateTimecodeAndPlayhead(t) {
    $("currentTimecode").textContent = formatTimecode(t);

    const dur = video.duration || getFallbackDuration();
    if (dur > 0) {
      const pct = Math.min(100, Math.max(0, (t / dur) * 100));
      $("timelinePlayhead").style.left = `${pct}%`;
    }

    const activeSrt = state.srtLines.find((s) => t >= s.startSec && t <= s.endSec);
    const subOverlay = $("subtitleOverlay");
    if (activeSrt) {
      subOverlay.textContent = activeSrt.text;
      subOverlay.classList.remove("hidden");
    } else {
      subOverlay.classList.add("hidden");
    }

    document.querySelectorAll(".srt-line").forEach((el) => {
      const idx = parseInt(el.dataset.idx, 10);
      const isActive = activeSrt && activeSrt.index === idx;
      el.classList.toggle("active", Boolean(isActive));
    });
  }

  function onVideoTimeUpdate() {
    const t = video.currentTime || 0;
    state.virtualTime = t;
    updateTimecodeAndPlayhead(t);

    // Live Filter Preview
    const overlay = $("filterOverlay");
    if ($("previewFilters").checked && !video.paused) {
      const activeCues = state.cues.filter(
        (c) => c.enabled !== false && t >= c.startSec && t <= c.endSec && c.action !== "none"
      );
      const skipCue = activeCues.find((c) => c.action === "skip");
      if (skipCue && skipCue.endSec > t + 0.05) {
        video.currentTime = skipCue.endSec;
        $("filterOverlayText").textContent = `SKIPPED: ${skipCue.category}`;
        overlay.classList.remove("hidden");
        return;
      }
      const muteCue = activeCues.find((c) => c.action === "mute");
      video.muted = Boolean(muteCue);
      if (muteCue) {
        $("filterOverlayText").textContent = `MUTED: ${muteCue.description || muteCue.category}`;
        overlay.classList.remove("hidden");
      } else {
        overlay.classList.add("hidden");
      }
    } else {
      video.muted = false;
      overlay.classList.add("hidden");
    }
  }

  // --- Timeline Rendering ---
  function getFallbackDuration() {
    let maxT = video.duration || 0;
    for (const c of state.cues) maxT = Math.max(maxT, c.endSec);
    for (const s of state.srtLines) maxT = Math.max(maxT, s.endSec);
    return maxT > 0 ? maxT : 600;
  }

  function renderTimeline() {
    const dur = getFallbackDuration();
    const canvas = $("timelineCanvas");
    canvas.style.width = `${state.zoom * 100}%`;

    // Ruler ticks
    const ruler = $("timelineRuler");
    ruler.innerHTML = "";
    const tickCount = Math.min(40, Math.max(8, Math.floor(10 * state.zoom)));
    for (let i = 0; i <= tickCount; i++) {
      const t = (dur * i) / tickCount;
      const tick = document.createElement("div");
      tick.className = "ruler-tick";
      tick.style.left = `${(i / tickCount) * 100}%`;
      tick.textContent = formatShortTime(t);
      ruler.appendChild(tick);
    }

    const videoTrack = $("trackVideoCues");
    const audioTrack = $("trackAudioCues");
    videoTrack.innerHTML = "";
    audioTrack.innerHTML = "";

    state.cues.forEach((cue) => {
      const left = (cue.startSec / dur) * 100;
      const width = Math.max(0.25, ((cue.endSec - cue.startSec) / dur) * 100);
      const el = document.createElement("div");
      el.className = `timeline-cue ${getCategoryClass(cue.category)}`;
      el.style.left = `${left}%`;
      el.style.width = `${width}%`;
      el.title = `${cue.start} -> ${cue.end} | ${cue.category} (${cue.action})\n${cue.description || ""}`;
      el.textContent = cue.description || cue.category.split(".").pop();
      el.addEventListener("click", (e) => {
        e.stopPropagation();
        seekTo(cue.startSec);
        loadCueIntoForm(cue);
      });

      if (cue.channel === "audio") {
        audioTrack.appendChild(el);
      } else {
        videoTrack.appendChild(el);
      }
    });

    renderInOutRange();
  }

  function renderInOutRange() {
    const el = $("inOutRange");
    if (state.inTime === null || state.outTime === null || state.outTime <= state.inTime) {
      el.classList.add("hidden");
      return;
    }
    const dur = getFallbackDuration();
    const left = (state.inTime / dur) * 100;
    const width = ((state.outTime - state.inTime) / dur) * 100;
    el.style.left = `${left}%`;
    el.style.width = `${width}%`;
    el.classList.remove("hidden");
  }

  function onTimelineClick(e) {
    const rect = $("timelineCanvas").getBoundingClientRect();
    const ratio = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
    const dur = getFallbackDuration();
    seekTo(ratio * dur);
  }

  // --- Cue CRUD ---
  function onCategoryChange() {
    const cat = $("cueCategory").value;
    if (cat.startsWith("Language.") || cat === "SexualReferences.ExplicitWords") {
      $("cueChannel").value = "audio";
      $("cueAction").value = "mute";
    } else {
      $("cueChannel").value = "video";
      $("cueAction").value = "skip";
    }
  }

  async function onCueFormSubmit(e) {
    e.preventDefault();
    const startSec = parseTimecode($("cueStart").value);
    const endSec = parseTimecode($("cueEnd").value);
    if (endSec <= startSec) {
      setStatus("End time must be greater than Start time.", true);
      return;
    }

    const payload = {
      start: formatTimecode(startSec),
      end: formatTimecode(endSec),
      category: $("cueCategory").value,
      channel: $("cueChannel").value,
      action: $("cueAction").value,
      description: $("cueDescription").value.trim() || null,
    };

    try {
      if (state.itemId) {
        if (state.editingCueKey) {
          await apiFetch(`/ContentFilter/filters/${state.itemId}/segments/${encodeURIComponent(state.editingCueKey)}`, {
            method: "PUT",
            body: JSON.stringify(payload),
          });
        } else {
          await apiFetch(`/ContentFilter/filters/${state.itemId}/segments`, {
            method: "POST",
            body: JSON.stringify(payload),
          });
        }
        await fetchJcfCues(state.itemId);
        setStatus(state.editingCueKey ? "Updated cue on JCF server." : "Added cue to JCF server.");
      } else {
        const localCue = normalizeCue(payload);
        if (state.editingCueKey) {
          state.cues = state.cues.map((c) => (c.key === state.editingCueKey ? localCue : c));
        } else {
          state.cues.push(localCue);
        }
        renderTimeline();
        renderCuesList();
        renderTranscript();
      }
      resetCueForm();
    } catch (err) {
      setStatus(`Failed to save cue: ${err.message}`, true);
    }
  }

  function loadCueIntoForm(cue) {
    state.editingCueKey = cue.key;
    $("cueFormTitle").textContent = "Edit Cue";
    $("editingCueBadge").classList.remove("hidden");
    $("btnDeleteEditingCue").classList.remove("hidden");
    $("btnSaveCue").textContent = "Update Cue";

    $("cueStart").value = cue.start;
    $("cueEnd").value = cue.end;
    $("cueCategory").value = cue.category;
    $("cueChannel").value = cue.channel;
    $("cueAction").value = cue.action;
    $("cueDescription").value = cue.description || "";
    syncInOutFromForm();
  }

  function resetCueForm() {
    state.editingCueKey = null;
    $("cueFormTitle").textContent = "New Objectionable / Profanity Cue";
    $("editingCueBadge").classList.add("hidden");
    $("btnDeleteEditingCue").classList.add("hidden");
    $("btnSaveCue").textContent = "Add Cue (M)";
    $("cueDescription").value = "";
  }

  async function deleteEditingCue() {
    if (!state.editingCueKey) return;
    await deleteCueByKey(state.editingCueKey);
    resetCueForm();
  }

  async function deleteCueByKey(cueKey) {
    try {
      if (state.itemId) {
        await apiFetch(`/ContentFilter/filters/${state.itemId}/segments/${encodeURIComponent(cueKey)}`, {
          method: "DELETE",
        });
        await fetchJcfCues(state.itemId);
      } else {
        state.cues = state.cues.filter((c) => c.key !== cueKey);
        renderTimeline();
        renderCuesList();
        renderTranscript();
      }
      setStatus("Cue deleted.");
    } catch (err) {
      setStatus(`Failed to delete cue: ${err.message}`, true);
    }
  }

  function renderCuesList() {
    $("cueCount").textContent = String(state.cues.length);
    const list = $("cuesList");
    const q = $("cueFilterInput").value.trim().toLowerCase();
    const ch = $("cueFilterChannel").value;

    const filtered = state.cues
      .filter((c) => (ch === "all" || c.channel === ch))
      .filter((c) => !q || `${c.category} ${c.description || ""} ${c.start}`.toLowerCase().includes(q))
      .sort((a, b) => a.startSec - b.startSec);

    if (!filtered.length) {
      list.innerHTML = `<div class="empty-state">No matching cues.</div>`;
      return;
    }

    list.innerHTML = "";
    filtered.forEach((cue) => {
      const card = document.createElement("div");
      card.className = `cue-item ${getCategoryClass(cue.category)}`;
      card.innerHTML = `
        <div class="cue-item-top">
          <span class="cue-item-time">${cue.start} → ${cue.end}</span>
          <div class="cue-item-meta">
            <span class="pill">${escapeHtml(cue.channel)}</span>
            <span class="pill">${escapeHtml(cue.action)}</span>
          </div>
        </div>
        <div class="cue-item-desc">
          <span><strong>${escapeHtml(cue.category)}</strong>${cue.description ? ` — ${escapeHtml(cue.description)}` : ""}</span>
          <button class="btn-link btn-del-cue" title="Delete cue">Delete</button>
        </div>
      `;
      card.addEventListener("click", (e) => {
        if (e.target.classList.contains("btn-del-cue")) {
          e.stopPropagation();
          deleteCueByKey(cue.key);
          return;
        }
        seekTo(cue.startSec);
        loadCueIntoForm(cue);
      });
      list.appendChild(card);
    });
  }

  // --- Transcript & Word Marking ---
  function renderJcfWords() {
    const sec = $("jcfWordsSection");
    const box = $("jcfWordsChips");
    if (!state.jcfWords.length) {
      sec.classList.add("hidden");
      return;
    }
    sec.classList.remove("hidden");
    box.innerHTML = "";
    state.jcfWords.forEach((group) => {
      const word = group.word || group.Word;
      const occs = group.occurrences || group.Occurrences || [];
      const chip = document.createElement("button");
      chip.type = "button";
      chip.className = "word-chip";
      chip.innerHTML = `<span>${escapeHtml(word)}</span><span class="count">${occs.length}</span>`;
      chip.title = `Click to mute all ${occs.length} occurrences of "${word}"`;
      chip.addEventListener("click", () => applyBlanketWordFilter(word));
      box.appendChild(chip);
    });
  }

  function renderTranscript() {
    $("srtCount").textContent = String(state.srtLines.length);
    const list = $("transcriptList");
    if (!state.srtLines.length) {
      list.innerHTML = `<div class="empty-state">Load an item from Jellyfin or open a local <code>.srt</code> file to see clickable words and synced subtitles.</div>`;
      return;
    }

    const q = $("wordSearchInput").value.trim().toLowerCase();
    const markedWords = new Set(
      state.cues
        .filter((c) => c.channel === "audio" && c.description)
        .map((c) => c.description.replace(/^Spoken:\s*"?|"?$/g, "").toLowerCase())
    );

    list.innerHTML = "";
    const lines = q ? state.srtLines.filter((l) => l.text.toLowerCase().includes(q)) : state.srtLines;

    lines.forEach((line) => {
      const row = document.createElement("div");
      row.className = "srt-line";
      row.dataset.idx = String(line.index);

      const timeBtn = document.createElement("span");
      timeBtn.className = "srt-time";
      timeBtn.textContent = `${formatTimecode(line.startSec)} → ${formatTimecode(line.endSec)}`;
      timeBtn.addEventListener("click", () => { seekTo(line.startSec); });
      row.appendChild(timeBtn);

      const wordsDiv = document.createElement("div");
      wordsDiv.className = "srt-words";
      const tokens = line.text.split(/(\s+)/);
      const lineDur = Math.max(0.4, line.endSec - line.startSec);
      const totalChars = Math.max(1, line.text.length);
      let charCursor = 0;

      tokens.forEach((tok) => {
        if (/^\s+$/.test(tok)) {
          wordsDiv.appendChild(document.createTextNode(tok));
          charCursor += tok.length;
          return;
        }
        const clean = tok.replace(/^[^\w']+|[^\w']+$/g, "");
        const wStart = Math.max(line.startSec, line.startSec + (charCursor / totalChars) * lineDur - 0.12);
        const wEnd = Math.min(line.endSec, line.startSec + ((charCursor + tok.length) / totalChars) * lineDur + 0.18);
        charCursor += tok.length;

        const span = document.createElement("span");
        span.className = "srt-word";
        if (clean && markedWords.has(clean.toLowerCase())) span.classList.add("marked");
        if (q && tok.toLowerCase().includes(q)) span.classList.add("match-highlight");
        span.textContent = tok;
        span.addEventListener("click", (ev) => {
          ev.stopPropagation();
          seekTo(wStart);
          showWordPopover(ev.currentTarget, clean || tok, wStart, wEnd);
        });
        wordsDiv.appendChild(span);
      });

      row.appendChild(wordsDiv);
      list.appendChild(row);
    });
  }

  function showWordPopover(targetEl, word, startSec, endSec) {
    state.popoverContext = { word, startSec, endSec };
    $("popoverWordText").textContent = word;
    $("popoverTimeText").textContent = `${formatTimecode(startSec)} → ${formatTimecode(endSec)}`;

    const pop = $("wordPopover");
    const rect = targetEl.getBoundingClientRect();
    pop.style.left = `${Math.min(window.innerWidth - 230, Math.max(10, rect.left))}px`;
    pop.style.top = `${Math.min(window.innerHeight - 150, rect.bottom + 6)}px`;
    pop.classList.remove("hidden");
  }

  async function markPopoverWord(blanket) {
    const ctx = state.popoverContext;
    $("wordPopover").classList.add("hidden");
    if (!ctx) return;

    if (blanket) {
      await applyBlanketWordFilter(ctx.word);
      return;
    }

    const payload = {
      start: formatTimecode(ctx.startSec),
      end: formatTimecode(ctx.endSec),
      category: "Language.GeneralProfanity",
      channel: "audio",
      action: "mute",
      description: `Spoken: "${ctx.word}"`,
    };

    try {
      if (state.itemId) {
        await apiFetch(`/ContentFilter/filters/${state.itemId}/segments`, {
          method: "POST",
          body: JSON.stringify(payload),
        });
        await fetchJcfCues(state.itemId);
      } else {
        state.cues.push(normalizeCue(payload));
        renderTimeline();
        renderCuesList();
        renderTranscript();
      }
      setStatus(`Muted "${ctx.word}" at ${payload.start}`);
    } catch (err) {
      setStatus(`Failed to mark word: ${err.message}`, true);
    }
  }

  function loadPopoverWordInForm() {
    const ctx = state.popoverContext;
    $("wordPopover").classList.add("hidden");
    if (!ctx) return;
    resetCueForm();
    $("cueStart").value = formatTimecode(ctx.startSec);
    $("cueEnd").value = formatTimecode(ctx.endSec);
    $("cueCategory").value = "Language.GeneralProfanity";
    $("cueChannel").value = "audio";
    $("cueAction").value = "mute";
    $("cueDescription").value = `Spoken: "${ctx.word}"`;
    syncInOutFromForm();
  }

  async function applyBlanketWordFilter(word) {
    if (!word) return;
    if (state.itemId) {
      try {
        await apiFetch(`/ContentFilter/subtitles/${state.itemId}/blanket-filter`, {
          method: "POST",
          body: JSON.stringify({ word, action: "mute", language: "eng", global: false }),
        });
        await fetchJcfCues(state.itemId);
        setStatus(`Applied blanket mute for "${word}" via JCF.`);
        return;
      } catch {
        // Fallback to local SRT scan if word isn't in JCF's default dictionary
      }
    }
    await muteLocalSrtMatches(word);
  }

  async function muteAllSearchMatches() {
    const term = $("wordSearchInput").value.trim();
    if (!term) {
      setStatus("Type a word or phrase in the search box first.", true);
      return;
    }
    await muteLocalSrtMatches(term);
  }

  async function muteLocalSrtMatches(term) {
    const escaped = term.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    const regex = new RegExp(`\\b${escaped}\\b`, "i");
    const matches = state.srtLines.filter((l) => regex.test(l.text));
    if (!matches.length) {
      setStatus(`No SRT matches found for "${term}".`, true);
      return;
    }

    let added = 0;
    for (const line of matches) {
      const payload = {
        start: formatTimecode(line.startSec),
        end: formatTimecode(line.endSec),
        category: "Language.GeneralProfanity",
        channel: "audio",
        action: "mute",
        description: `Spoken: "${term}"`,
      };
      if (state.itemId) {
        await apiFetch(`/ContentFilter/filters/${state.itemId}/segments`, {
          method: "POST",
          body: JSON.stringify(payload),
        });
      } else {
        state.cues.push(normalizeCue(payload));
      }
      added++;
    }

    if (state.itemId) await fetchJcfCues(state.itemId);
    else {
      renderTimeline();
      renderCuesList();
      renderTranscript();
    }
    setStatus(`Created ${added} mute cues for "${term}".`);
  }

  // --- Keyboard Shortcuts ---
  function onKeyDown(e) {
    const tag = (e.target?.tagName || "").toLowerCase();
    if (["input", "textarea", "select"].includes(tag)) return;

    if (e.code === "Space") {
      e.preventDefault();
      togglePlay();
    } else if (e.code === "ArrowLeft") {
      e.preventDefault();
      seekBy(e.shiftKey ? -1 : -FRAME_STEP);
    } else if (e.code === "ArrowRight") {
      e.preventDefault();
      seekBy(e.shiftKey ? 1 : FRAME_STEP);
    } else if (e.key === "i" || e.key === "I") {
      e.preventDefault();
      markIn();
    } else if (e.key === "o" || e.key === "O") {
      e.preventDefault();
      markOut();
    } else if (e.key === "m" || e.key === "M") {
      e.preventDefault();
      $("cueForm").requestSubmit();
    }
  }

  // --- Parsers & Formatters ---
  function normalizeCue(raw) {
    const startSec = parseTimecode(raw.start);
    const endSec = parseTimecode(raw.end);
    const start = formatTimecode(startSec);
    const end = formatTimecode(endSec);
    const category = raw.category || "SexAndNudity.FullNudity";
    return {
      key: raw.key || `${start}-${end}-${category}`,
      start,
      end,
      startSec,
      endSec,
      category,
      channel: raw.channel || "video",
      action: raw.action || "skip",
      precision: raw.precision || null,
      description: raw.description || "",
      enabled: raw.enabled !== false,
    };
  }

  function parseSrt(text) {
    const blocks = text.replace(/\r\n/g, "\n").trim().split(/\n\s*\n/);
    const lines = [];
    blocks.forEach((blk, i) => {
      const rows = blk.split("\n").map((r) => r.trim()).filter(Boolean);
      const timeIdx = rows.findIndex((r) => r.includes("-->"));
      if (timeIdx === -1) return;
      const [sStr, eStr] = rows[timeIdx].split("-->").map((s) => s.trim().split(/\s+/)[0]);
      const content = rows.slice(timeIdx + 1).join(" ").replace(/<[^>]+>/g, "");
      lines.push({
        index: i + 1,
        startSec: parseTimecode(sStr),
        endSec: parseTimecode(eStr),
        text: content,
      });
    });
    return lines;
  }

  function parseJcf(text) {
    const blocks = text.replace(/\r\n/g, "\n").trim().split(/\n\s*\n/);
    let title = "";
    let year = "";
    const cues = [];

    for (const blk of blocks) {
      const rows = blk.split("\n").map((r) => r.trim());
      if (rows[0]?.startsWith("NOTE")) {
        for (const r of rows.slice(1)) {
          if (r.startsWith("TITLE ")) title = r.slice(6).trim();
          if (r.startsWith("YEAR ")) year = r.slice(5).trim();
        }
        continue;
      }
      if (!rows[0]?.includes("-->")) continue;
      const [start, end] = rows[0].split("-->").map((s) => s.trim());
      const cue = { start, end };
      for (const r of rows.slice(1)) {
        const idx = r.indexOf(":");
        if (idx === -1) continue;
        const k = r.slice(0, idx).trim().toLowerCase();
        const v = r.slice(idx + 1).trim();
        cue[k] = v;
      }
      cues.push(normalizeCue(cue));
    }
    return { title, year, cues };
  }

  function parseTimecode(str) {
    if (typeof str === "number") return str;
    const s = String(str || "0").trim().replace(",", ".");
    const parts = s.split(":");
    if (parts.length === 3) {
      return Number(parts[0]) * 3600 + Number(parts[1]) * 60 + Number(parts[2]);
    }
    if (parts.length === 2) {
      return Number(parts[0]) * 60 + Number(parts[1]);
    }
    return Number(s) || 0;
  }

  function formatTimecode(sec) {
    const s = Math.max(0, Number(sec) || 0);
    const hrs = Math.floor(s / 3600);
    const mins = Math.floor((s % 3600) / 60);
    const secs = Math.floor(s % 60);
    const ms = Math.round((s - Math.floor(s)) * 1000);
    if (ms === 1000) return formatTimecode(Math.floor(s) + 1);
    return `${String(hrs).padStart(2, "0")}:${String(mins).padStart(2, "0")}:${String(secs).padStart(2, "0")}.${String(ms).padStart(3, "0")}`;
  }

  function formatShortTime(sec) {
    const s = Math.max(0, Math.floor(sec));
    const hrs = Math.floor(s / 3600);
    const mins = Math.floor((s % 3600) / 60);
    const secs = s % 60;
    return hrs > 0
      ? `${hrs}:${String(mins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
      : `${mins}:${String(secs).padStart(2, "0")}`;
  }

  function getCategoryClass(cat) {
    if (!cat) return "cat-other";
    if (cat.startsWith("SexAndNudity")) return "cat-nudity";
    if (cat.startsWith("Violence")) return "cat-violence";
    if (cat.startsWith("Language") || cat.startsWith("SexualReferences")) return "cat-profanity";
    return "cat-other";
  }

  function escapeHtml(str) {
    return String(str)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  init();
})();
