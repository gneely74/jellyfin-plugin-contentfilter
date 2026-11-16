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
    knownDuration: 0,
    lastActiveSrtIdx: null,
    undoStack: [],
    detailCue: null,
    recentItems: (() => {
      try {
        return JSON.parse(localStorage.getItem("jcf_recentItems") || "[]");
      } catch {
        return [];
      }
    })(),
  };

  // DOM helpers
  const $ = (id) => document.getElementById(id);
  const video = $("videoPlayer");

  function init() {
    $("serverUrl").value = state.serverUrl;
    $("apiKey").value = state.apiKey;
    $("useProxy").checked = state.useProxy;
    $("itemIdInput").value = state.itemId;
    $("detailCategory").innerHTML = $("cueCategory").innerHTML;

    bindEvents();
    renderRecentItems();
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
    $("btnCloseWhisperStatus").addEventListener("click", () => $("whisperBanner").classList.add("hidden"));
    $("btnPullAll").addEventListener("click", () => pullAllFromJcf());
    $("btnBrowseLibrary").addEventListener("click", openLibraryModal);
    $("recentItemsSelect").addEventListener("change", (e) => {
      const id = e.target.value;
      if (!id) return;
      const match = state.recentItems.find((r) => r.id === id);
      if (match) state.title = match.title;
      e.target.selectedIndex = 0;
      pullAllFromJcf(id);
    });
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
    $("btnUndo").addEventListener("click", undoLastAction);
    $("btnSaveJcf").addEventListener("click", () => saveAllCuesToJcf(false));
    $("btnExportJcf").addEventListener("click", exportJcfFile);
    $("btnHotkeys").addEventListener("click", () => $("hotkeysModal").classList.toggle("hidden"));
    $("btnCloseHotkeysModal").addEventListener("click", () => $("hotkeysModal").classList.add("hidden"));
    document.querySelector("#hotkeysModal .modal-backdrop").addEventListener("click", () => {
      $("hotkeysModal").classList.add("hidden");
    });

    const closeMarkOut = () => $("markOutModal").classList.add("hidden");
    $("btnCloseMarkOutModal").addEventListener("click", closeMarkOut);
    $("btnMarkOutCancel").addEventListener("click", closeMarkOut);
    document.querySelector("#markOutModal .modal-backdrop").addEventListener("click", closeMarkOut);
    document.querySelectorAll(".markout-preset").forEach((btn) => {
      btn.addEventListener("click", () => {
        closeMarkOut();
        resetCueForm();
        $("cueStart").value = formatTimecode(state.inTime ?? 0);
        $("cueEnd").value = formatTimecode(state.outTime ?? 2);
        $("cueCategory").value = btn.dataset.cat;
        $("cueChannel").value = btn.dataset.ch;
        $("cueAction").value = btn.dataset.act;
        $("cueDescription").value = $("markOutDesc").value.trim() || btn.dataset.desc;
        syncInOutFromForm();
        $("cueForm").requestSubmit();
      });
    });
    $("btnMarkOutCustomSave").addEventListener("click", () => {
      closeMarkOut();
      const customDesc = $("markOutDesc").value.trim();
      if (customDesc) $("cueDescription").value = customDesc;
      $("cueAction").value = $("markOutAction").value;
      $("cueForm").requestSubmit();
    });

    // Detailed Cue Scrubber Modal
    const closeDetail = () => $("cueDetailModal").classList.add("hidden");
    $("btnCloseDetailModal").addEventListener("click", closeDetail);
    $("btnDetailCancel").addEventListener("click", closeDetail);
    document.querySelector("#cueDetailModal .modal-backdrop").addEventListener("click", closeDetail);
    $("detailStartSlider").addEventListener("input", (e) => {
      if (!state.detailCue) return;
      const val = Math.min(state.detailCue.endSec - 0.05, parseFloat(e.target.value));
      state.detailCue.startSec = Math.max(0, val);
      $("detailStartLabel").textContent = formatTimecode(state.detailCue.startSec);
      seekTo(state.detailCue.startSec);
    });
    $("detailEndSlider").addEventListener("input", (e) => {
      if (!state.detailCue) return;
      const val = Math.max(state.detailCue.startSec + 0.05, parseFloat(e.target.value));
      state.detailCue.endSec = val;
      $("detailEndLabel").textContent = formatTimecode(state.detailCue.endSec);
      seekTo(state.detailCue.endSec);
    });
    document.querySelectorAll(".detail-nudge").forEach((btn) => {
      btn.addEventListener("click", () => {
        if (!state.detailCue) return;
        const delta = parseFloat(btn.dataset.delta);
        if (btn.dataset.bound === "start") {
          state.detailCue.startSec = Math.max(0, Math.min(state.detailCue.endSec - 0.05, state.detailCue.startSec + delta));
          $("detailStartLabel").textContent = formatTimecode(state.detailCue.startSec);
          $("detailStartSlider").value = String(state.detailCue.startSec);
          seekTo(state.detailCue.startSec);
        } else {
          state.detailCue.endSec = Math.max(state.detailCue.startSec + 0.05, state.detailCue.endSec + delta);
          $("detailEndLabel").textContent = formatTimecode(state.detailCue.endSec);
          $("detailEndSlider").value = String(state.detailCue.endSec);
          seekTo(state.detailCue.endSec);
        }
      });
    });
    $("btnDetailJumpStart").addEventListener("click", () => state.detailCue && seekTo(state.detailCue.startSec));
    $("btnDetailJumpEnd").addEventListener("click", () => state.detailCue && seekTo(state.detailCue.endSec));
    const syncDetailBoundToPlayhead = (bound) => {
      if (!state.detailCue) return;
      const t = Math.max(0, video.readyState >= 1 ? (video.currentTime || 0) : state.virtualTime);
      if (bound === "start") {
        state.detailCue.startSec = t;
        if (state.detailCue.endSec <= t + 0.05) {
          state.detailCue.endSec = t + 0.5;
        }
      } else {
        state.detailCue.endSec = Math.max(0.05, t);
        if (state.detailCue.startSec >= state.detailCue.endSec - 0.05) {
          state.detailCue.startSec = Math.max(0, state.detailCue.endSec - 0.5);
        }
      }
      const startSlider = $("detailStartSlider");
      const endSlider = $("detailEndSlider");
      const curMin = parseFloat(startSlider.min) || 0;
      const curMax = parseFloat(startSlider.max) || 30;
      const nextMin = Math.min(curMin, Math.max(0, state.detailCue.startSec - 15));
      const nextMax = Math.max(curMax, state.detailCue.endSec + 15);
      startSlider.min = String(nextMin);
      startSlider.max = String(nextMax);
      endSlider.min = String(nextMin);
      endSlider.max = String(nextMax);
      startSlider.value = String(state.detailCue.startSec);
      endSlider.value = String(state.detailCue.endSec);
      $("detailStartLabel").textContent = formatTimecode(state.detailCue.startSec);
      $("detailEndLabel").textContent = formatTimecode(state.detailCue.endSec);
    };
    $("btnDetailSyncStart").addEventListener("click", () => syncDetailBoundToPlayhead("start"));
    $("btnDetailSyncEnd").addEventListener("click", () => syncDetailBoundToPlayhead("end"));
    $("btnDetailPreview").addEventListener("click", () => {
      if (!state.detailCue) return;
      $("previewFilters").checked = true;
      seekTo(Math.max(0, state.detailCue.startSec - 1.5));
      video.play().catch(() => {});
    });
    $("btnDetailDelete").addEventListener("click", async () => {
      if (!state.detailCue) return;
      const key = state.detailCue.key;
      closeDetail();
      await deleteCueByKey(key);
    });
    $("btnDetailSave").addEventListener("click", saveDetailCue);
    makeDraggable("cueDetailModal");
    makeDraggable("markOutModal");
    makeDraggable("hotkeysModal");

    // Transport controls
    $("btnPlayPause").addEventListener("click", togglePlay);
    $("btnTestFiltered").addEventListener("click", () => {
      $("previewFilters").checked = true;
      video.play().catch(() => {});
    });
    $("scrubberSlider").addEventListener("input", (e) => {
      const dur = video.duration || getFallbackDuration();
      const ratio = parseFloat(e.target.value) / 1000;
      seekTo(ratio * dur);
    });
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
      if (Number.isFinite(video.duration) && video.duration > 0) {
        state.knownDuration = video.duration;
        $("durationTimecode").textContent = formatTimecode(video.duration);
      }
      if (state.pendingSeek !== null) {
        const target = state.pendingSeek;
        state.pendingSeek = null;
        video.currentTime = target;
      }
      renderTimeline();
    });
    video.addEventListener("durationchange", () => {
      if (Number.isFinite(video.duration) && video.duration > 0) {
        state.knownDuration = video.duration;
        $("durationTimecode").textContent = formatTimecode(video.duration);
        renderTimeline();
      }
    });
    video.addEventListener("play", () => {
      $("btnPlayPause").textContent = "Pause";
      startFilterLoop();
    });
    video.addEventListener("pause", () => {
      $("btnPlayPause").textContent = "Play";
      stopFilterLoop();
    });
    video.addEventListener("ended", stopFilterLoop);
    video.addEventListener("error", onVideoError);

    // Timeline interactions
    $("timelineZoom").addEventListener("input", (e) => {
      state.zoom = parseFloat(e.target.value) || 1;
      $("timelineZoomVal").textContent = `${state.zoom}x`;
      renderTimeline();
    });
    // Timeline drag-scrubbing
    let isDraggingTimeline = false;
    const scrubFromPointer = (e) => {
      const rect = $("timelineCanvas").getBoundingClientRect();
      const ratio = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
      seekTo(ratio * getFallbackDuration());
    };
    $("timelineCanvas").addEventListener("pointerdown", (e) => {
      if (e.target.classList.contains("timeline-cue")) return;
      isDraggingTimeline = true;
      $("timelineCanvas").setPointerCapture(e.pointerId);
      scrubFromPointer(e);
    });
    $("timelineCanvas").addEventListener("pointermove", (e) => {
      if (isDraggingTimeline) scrubFromPointer(e);
    });
    $("timelineCanvas").addEventListener("pointerup", (e) => {
      if (isDraggingTimeline) {
        isDraggingTimeline = false;
        $("timelineCanvas").releasePointerCapture(e.pointerId);
      }
    });

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
    $("btnShiftCues").addEventListener("click", () => {
      const val = parseFloat($("cueOffsetInput").value);
      if (Number.isNaN(val) || val === 0) {
        setStatus("Enter a non-zero offset in seconds (e.g. 35 or -12.5).", true);
        return;
      }
      shiftAllCues(val);
    });
    $("btnAlignCueToPlayhead").addEventListener("click", alignSelectedCueToPlayhead);

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

  function updateBannerState(bannerId, textId, wrapId, fillId, pctId, msg, isError = false, progress = null) {
    const banner = $(bannerId);
    $(textId).textContent = msg;
    banner.classList.toggle("error", isError);
    const wrap = $(wrapId);
    const fill = $(fillId);
    const pctEl = $(pctId);
    if (progress && typeof progress === "object") {
      wrap.classList.remove("hidden");
      if (progress.indeterminate) {
        fill.classList.add("indeterminate");
        fill.style.width = "35%";
        pctEl.textContent = progress.label || "Working...";
      } else {
        const pct = Math.max(0, Math.min(100, Number(progress.percent) || 0));
        fill.classList.remove("indeterminate");
        fill.style.width = `${pct}%`;
        pctEl.textContent = `${pct}%`;
      }
    } else {
      wrap.classList.add("hidden");
      fill.classList.remove("indeterminate");
    }
    banner.classList.remove("hidden");
  }

  function setStatus(msg, isError = false, progress = null) {
    updateBannerState("statusBanner", "statusText", "statusProgressWrap", "statusProgressFill", "statusProgressPct", msg, isError, progress);
  }

  function setWhisperStatus(msg, isError = false, progress = null) {
    updateBannerState("whisperBanner", "whisperText", "whisperProgressWrap", "whisperProgressFill", "whisperProgressPct", msg, isError, progress);
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

    // 1. Load Video Stream via PlaybackInfo (direct play or HLS transcode)
    loadJellyfinVideo(itemId);

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

    recordRecentItem(itemId, state.title || itemId);
    setStatus(summary.length ? `Loaded: ${summary.join(" • ")}` : "Connected, but no JCF/SRT data found yet for this item.");
  }

  function recordRecentItem(id, title) {
    const cleanTitle = (title || id).trim();
    state.recentItems = [
      { id, title: cleanTitle },
      ...state.recentItems.filter((r) => r.id !== id),
    ].slice(0, 5);
    localStorage.setItem("jcf_recentItems", JSON.stringify(state.recentItems));
    renderRecentItems();
  }

  function makeDraggable(modalId) {
    const modal = $(modalId);
    const dialog = modal?.querySelector(".modal-dialog");
    const header = modal?.querySelector(".modal-header");
    if (!dialog || !header) return;

    let dragging = false;
    let startX = 0;
    let startY = 0;
    let origLeft = 0;
    let origTop = 0;

    header.addEventListener("pointerdown", (e) => {
      if (e.target.closest("button")) return;
      dragging = true;
      const rect = dialog.getBoundingClientRect();
      dialog.style.position = "fixed";
      dialog.style.margin = "0";
      dialog.style.left = `${rect.left}px`;
      dialog.style.top = `${rect.top}px`;
      startX = e.clientX;
      startY = e.clientY;
      origLeft = rect.left;
      origTop = rect.top;
      header.setPointerCapture(e.pointerId);
    });

    header.addEventListener("pointermove", (e) => {
      if (!dragging) return;
      const nextLeft = Math.max(0, Math.min(window.innerWidth - 120, origLeft + (e.clientX - startX)));
      const nextTop = Math.max(0, Math.min(window.innerHeight - 60, origTop + (e.clientY - startY)));
      dialog.style.left = `${nextLeft}px`;
      dialog.style.top = `${nextTop}px`;
    });

    const stopDrag = (e) => {
      if (!dragging) return;
      dragging = false;
      try { header.releasePointerCapture(e.pointerId); } catch {}
    };
    header.addEventListener("pointerup", stopDrag);
    header.addEventListener("pointercancel", stopDrag);
  }

  function renderRecentItems() {
    const sel = $("recentItemsSelect");
    if (!sel) return;
    sel.innerHTML = `<option value="">Recent (${state.recentItems.length})</option>`;
    state.recentItems.forEach((item) => {
      const opt = document.createElement("option");
      opt.value = item.id;
      opt.textContent = item.title;
      sel.appendChild(opt);
    });
    sel.selectedIndex = 0;
  }

  function destroyHls() {
    if (state.hlsInstance) {
      state.hlsInstance.destroy();
      state.hlsInstance = null;
    }
  }

  async function loadJellyfinVideo(itemId) {
    destroyHls();
    const key = ($("apiKey").value || state.apiKey).trim();
    const encodedKey = encodeURIComponent(key);

    let mediaSource = null;
    try {
      const pbInfo = await apiFetch(`/Items/${itemId}/PlaybackInfo`, {
        method: "POST",
        body: JSON.stringify({
          DeviceProfile: {
            MaxStreamingBitrate: 20000000,
            DirectPlayProfiles: [
              { Container: "mp4,m4v", Type: "Video", VideoCodec: "h264", AudioCodec: "aac,mp3" },
              { Container: "webm", Type: "Video", VideoCodec: "vp8,vp9", AudioCodec: "vorbis,opus" },
            ],
            TranscodingProfiles: [
              {
                Container: "ts",
                Type: "Video",
                AudioCodec: "aac",
                VideoCodec: "h264",
                Context: "Streaming",
                Protocol: "hls",
                MaxAudioChannels: "2",
                MinSegments: "1",
                BreakOnNonKeyFrames: true,
              },
            ],
          },
        }),
      });
      mediaSource = (pbInfo?.MediaSources || [])[0] || null;
      if (mediaSource?.RunTimeTicks) {
        state.knownDuration = mediaSource.RunTimeTicks / 10_000_000;
        $("durationTimecode").textContent = formatTimecode(state.knownDuration);
        renderTimeline();
      }
    } catch {
      mediaSource = null;
    }

    if (mediaSource?.SupportsDirectPlay) {
      const msId = mediaSource.Id || itemId;
      const directPath = `/Videos/${itemId}/stream?static=true&MediaSourceId=${encodeURIComponent(msId)}&api_key=${encodedKey}`;
      video.src = buildFetchUrl(directPath);
      video.load();
      return;
    }

    const hlsPath = mediaSource?.TranscodingUrl
      ? `${mediaSource.TranscodingUrl}${mediaSource.TranscodingUrl.includes("?") ? "&" : "?"}api_key=${encodedKey}`
      : `/Videos/${itemId}/master.m3u8?MediaSourceId=${encodeURIComponent(mediaSource?.Id || itemId)}&VideoCodec=h264&AudioCodec=aac&VideoBitrate=8000000&AudioBitrate=192000&MaxStreamingBitrate=8000000&TranscodingMaxAudioChannels=2&SegmentContainer=ts&MinSegments=1&BreakOnNonKeyFrames=True&PlaySessionId=jcfstudio_${Date.now()}&api_key=${encodedKey}`;
    const hlsUrl = buildFetchUrl(hlsPath);

    if (video.canPlayType("application/vnd.apple.mpegurl")) {
      video.src = hlsUrl;
      video.load();
    } else if (window.Hls && window.Hls.isSupported()) {
      const hls = new window.Hls({
        enableWorker: true,
        xhrSetup: (xhr) => {
          if (key) xhr.setRequestHeader("X-Emby-Token", key);
        },
      });
      state.hlsInstance = hls;
      hls.loadSource(hlsUrl);
      hls.attachMedia(video);
      hls.on(window.Hls.Events.ERROR, (_ev, data) => {
        if (data.fatal) {
          setStatus(`HLS playback error (${data.details}). Try Safari or load a local MP4 via the Video button.`, true);
        }
      });
    } else {
      const mp4Path = `/Videos/${itemId}/stream.mp4?Static=false&MediaSourceId=${encodeURIComponent(mediaSource?.Id || itemId)}&VideoCodec=h264&AudioCodec=aac&VideoBitrate=8000000&AudioBitrate=192000&MaxStreamingBitrate=8000000&TranscodingMaxAudioChannels=2&api_key=${encodedKey}`;
      video.src = buildFetchUrl(mp4Path);
      video.load();
    }
  }

  function onVideoError() {
    if (state.itemId && !state.triedTranscodeFallback && !state.hlsInstance && video.src.includes("/Videos/")) {
      state.triedTranscodeFallback = true;
      setStatus("Direct stream unsupported by browser; switching to HLS H.264/AAC transcoded stream...");
      const key = ($("apiKey").value || state.apiKey).trim();
      const hlsPath = `/Videos/${state.itemId}/master.m3u8?MediaSourceId=${state.itemId}&VideoCodec=h264&AudioCodec=aac&VideoBitrate=8000000&AudioBitrate=192000&MaxStreamingBitrate=8000000&TranscodingMaxAudioChannels=2&SegmentContainer=ts&MinSegments=1&BreakOnNonKeyFrames=True&PlaySessionId=jcfstudio_${Date.now()}&api_key=${encodeURIComponent(key)}`;
      if (window.Hls && window.Hls.isSupported()) {
        const hls = new window.Hls({
          enableWorker: true,
          xhrSetup: (xhr) => { if (key) xhr.setRequestHeader("X-Emby-Token", key); },
        });
        state.hlsInstance = hls;
        hls.loadSource(buildFetchUrl(hlsPath));
        hls.attachMedia(video);
      } else {
        video.src = buildFetchUrl(hlsPath);
        video.load();
      }
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
    const itemId = state.itemId;
    let pollTimer = null;
    const pollProgress = async () => {
      try {
        const prog = await apiFetch(`/ContentFilter/nudity/${itemId}/scan/progress`);
        if (!prog) return;
        const stage = prog.stage ?? prog.Stage ?? "Scanning...";
        const total = prog.totalFrames ?? prog.TotalFrames ?? 0;
        const pct = prog.percent ?? prog.Percent ?? 0;
        const active = prog.isScanning ?? prog.IsScanning ?? false;
        if (active) {
          setStatus(`NudeNet: ${stage}`, false, total > 0 ? { percent: pct } : { indeterminate: true, label: "Extracting..." });
        }
      } catch (_) {
        // Ignore transient poll errors while scan runs
      }
    };
    try {
      setStatus("Starting NudeNet visual nudity scan on Jellyfin...", false, { indeterminate: true, label: "Starting..." });
      pollTimer = setInterval(pollProgress, 1000);
      const res = await apiFetch(`/ContentFilter/nudity/${itemId}/scan?force=true`, { method: "POST" });
      clearInterval(pollTimer);
      pollTimer = null;
      await fetchJcfCues(itemId);
      const count = res?.cuesAdded ?? res?.CuesAdded ?? res?.cuesCreated ?? res?.CuesCreated ?? "";
      setStatus(`NudeNet scan finished${count !== "" ? ` (${count} cues added)` : ""}. Timeline refreshed.`, false, { percent: 100 });
    } catch (err) {
      if (pollTimer) clearInterval(pollTimer);
      setStatus(`NudeNet scan failed: ${err.message}`, true);
    }
  }

  async function runWhisperTranscribe() {
    if (!state.itemId) {
      setWhisperStatus("Select a Jellyfin item first to run Whisper transcription.", true);
      return;
    }
    const itemId = state.itemId;
    let pollTimer = null;
    const pollWhisperProgress = async () => {
      try {
        const prog = await apiFetch(`/ContentFilter/subtitles/${itemId}/transcribe/progress`);
        if (!prog) return;
        const stage = prog.stage ?? prog.Stage ?? "Transcribing...";
        const pct = prog.percent ?? prog.Percent ?? 0;
        const active = prog.isTranscribing ?? prog.IsTranscribing ?? false;
        if (active) {
          setWhisperStatus(`Whisper SRT: ${stage}`, false, pct > 0 ? { percent: pct } : { indeterminate: true, label: "Starting..." });
        }
      } catch (_) {
        // Ignore transient poll errors while transcribing
      }
    };
    try {
      setWhisperStatus("Whisper SRT: Starting transcription on Jellyfin...", false, { indeterminate: true, label: "Starting..." });
      pollTimer = setInterval(pollWhisperProgress, 1000);
      const res = await apiFetch(`/ContentFilter/subtitles/${itemId}/transcribe?force=false`, { method: "POST" });
      clearInterval(pollTimer);
      pollTimer = null;
      await Promise.allSettled([fetchSrtSubtitles(itemId), fetchJcfWords(itemId)]);
      const count = res?.cuesAdded ?? res?.CuesAdded ?? "";
      setWhisperStatus(`Whisper SRT: Complete${count !== "" ? ` (${count} cues added)` : ""}. Subtitles & words refreshed.`, false, { percent: 100 });
    } catch (err) {
      if (pollTimer) clearInterval(pollTimer);
      setWhisperStatus(`Whisper SRT failed: ${err.message}`, true);
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

  function buildJcfText() {
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
    return lines.join("\n");
  }

  function exportJcfFile() {
    const blob = new Blob([buildJcfText()], { type: "text/vtt;charset=utf-8" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    const safeName = (state.title || state.itemId || "contentfilter").replace(/[^a-z0-9._-]+/gi, "_");
    a.download = `${safeName}.jcf`;
    a.click();
    URL.revokeObjectURL(a.href);
  }

  function pushUndoSnapshot() {
    state.undoStack.push(state.cues.map((c) => ({ ...c })));
    if (state.undoStack.length > 25) state.undoStack.shift();
    $("btnUndo").disabled = state.undoStack.length === 0;
  }

  async function undoLastAction() {
    if (!state.undoStack.length) return;
    state.cues = state.undoStack.pop();
    $("btnUndo").disabled = state.undoStack.length === 0;
    resetCueForm();
    renderTimeline();
    renderCuesList();
    renderTranscript();
    if (state.itemId) {
      await saveAllCuesToJcf(true);
      setStatus("Undid last change and synced to JCF server.");
    } else {
      setStatus("Undid last change.");
    }
  }

  async function saveAllCuesToJcf(quiet = false) {
    if (!state.itemId) {
      setStatus("Select a Jellyfin item first to save cues to JCF.", true);
      return;
    }
    try {
      const res = await apiFetch(`/ContentFilter/filters/${state.itemId}`, {
        method: "POST",
        headers: { "Content-Type": "text/plain; charset=utf-8" },
        body: buildJcfText(),
      });
      await fetchJcfCues(state.itemId);
      if (!quiet) {
        const count = res?.cues ?? state.cues.length;
        setStatus(`Saved ${count} cues to JCF server.`);
      }
    } catch (err) {
      setStatus(`Failed to save to JCF: ${err.message}`, true);
    }
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
    updateTimecodeAndPlayhead(clamped);
    if (video.readyState >= 1) {
      video.currentTime = clamped;
    } else {
      state.pendingSeek = clamped;
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

    if (!video.paused) video.pause();
    $("markOutRangeLabel").textContent = `${formatTimecode(state.inTime)} \u2192 ${formatTimecode(state.outTime)}`;
    $("markOutDesc").value = "";
    $("markOutAction").value = $("cueAction").value;
    $("markOutModal").classList.remove("hidden");
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
      $("scrubberSlider").value = String((t / dur) * 1000);
    }

    const activeSrt = state.srtLines.find((s) => t >= s.startSec && t <= s.endSec);
    const subOverlay = $("subtitleOverlay");
    if (activeSrt) {
      subOverlay.textContent = activeSrt.text;
      subOverlay.classList.remove("hidden");
    } else {
      subOverlay.classList.add("hidden");
    }

    let activeEl = null;
    document.querySelectorAll(".srt-line").forEach((el) => {
      const idx = parseInt(el.dataset.idx, 10);
      const isActive = activeSrt && activeSrt.index === idx;
      el.classList.toggle("active", Boolean(isActive));
      if (isActive) activeEl = el;
    });

    const activeIdx = activeSrt ? activeSrt.index : null;
    if (activeIdx !== state.lastActiveSrtIdx) {
      state.lastActiveSrtIdx = activeIdx;
      if (activeEl && $("autoScrollTranscript")?.checked) {
        activeEl.scrollIntoView({ block: "center", behavior: "smooth" });
      }
    }
  }

  let filterRafId = null;

  function startFilterLoop() {
    stopFilterLoop();
    const tick = () => {
      if (!video.paused && !video.ended) {
        const t = video.currentTime || 0;
        applyActiveFilters(t);
        filterRafId = requestAnimationFrame(tick);
      }
    };
    filterRafId = requestAnimationFrame(tick);
  }

  function stopFilterLoop() {
    if (filterRafId !== null) {
      cancelAnimationFrame(filterRafId);
      filterRafId = null;
    }
    video.muted = false;
    video.style.filter = "";
    $("filterOverlay").classList.add("hidden");
  }

  function applyActiveFilters(t) {
    const overlay = $("filterOverlay");
    if (!$("previewFilters").checked || video.paused) {
      video.muted = false;
      video.style.filter = "";
      overlay.classList.add("hidden");
      return;
    }

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

    const blurCue = activeCues.find((c) => c.action === "blur");
    video.style.filter = blurCue ? "blur(28px)" : "";

    const muteCue = activeCues.find((c) => c.action === "mute");
    video.muted = Boolean(muteCue);

    if (muteCue || blurCue) {
      const label = muteCue
        ? `MUTED: ${muteCue.description || muteCue.category}`
        : `BLURRED: ${blurCue.description || blurCue.category}`;
      $("filterOverlayText").textContent = label;
      overlay.classList.remove("hidden");
    } else {
      overlay.classList.add("hidden");
    }
  }

  function onVideoTimeUpdate() {
    const t = video.currentTime || 0;
    state.virtualTime = t;
    updateTimecodeAndPlayhead(t);
    applyActiveFilters(t);
  }

  // --- Timeline Rendering ---
  function getFallbackDuration() {
    if (Number.isFinite(video.duration) && video.duration > 0) return video.duration;
    if (state.knownDuration > 0) return state.knownDuration;
    let maxT = 0;
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
      pushUndoSnapshot();
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

  async function alignSelectedCueToPlayhead() {
    if (!state.editingCueKey) {
      setStatus("Click a cue first to select it, scrub to where it actually starts, then click Align to Playhead.", true);
      return;
    }
    const cue = state.cues.find((c) => c.key === state.editingCueKey);
    if (!cue) return;
    const t = video.readyState >= 1 ? (video.currentTime || 0) : state.virtualTime;
    const offset = Number((t - cue.startSec).toFixed(3));
    if (Math.abs(offset) < 0.01) {
      setStatus("Selected cue is already aligned with the playhead.");
      return;
    }
    $("cueOffsetInput").value = offset > 0 ? `+${offset}` : String(offset);
    await shiftAllCues(offset);
  }

  async function shiftAllCues(offsetSeconds) {
    try {
      pushUndoSnapshot();
      if (state.itemId) {
        await apiFetch(`/ContentFilter/filters/${state.itemId}/segments/offset`, {
          method: "POST",
          body: JSON.stringify({ offsetSeconds, channel: "all" }),
        });
        await fetchJcfCues(state.itemId);
      } else {
        state.cues = state.cues.map((c) => {
          const s = Math.max(0, c.startSec + offsetSeconds);
          const e = Math.max(s + 0.1, c.endSec + offsetSeconds);
          return normalizeCue({ ...c, start: formatTimecode(s), end: formatTimecode(e) });
        });
        renderTimeline();
        renderCuesList();
        renderTranscript();
      }
      resetCueForm();
      setStatus(`Shifted all cues by ${offsetSeconds > 0 ? "+" : ""}${offsetSeconds}s.`);
    } catch (err) {
      setStatus(`Failed to shift cues: ${err.message}`, true);
    }
  }

  async function deleteCueByKey(cueKey) {
    try {
      pushUndoSnapshot();
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
          <span style="display: inline-flex; gap: 8px;">
            <button class="btn-link btn-edit-cue" title="Edit cue in detailed scrubber">Edit</button>
            <button class="btn-link btn-del-cue" title="Delete cue">Delete</button>
          </span>
        </div>
      `;
      card.addEventListener("click", (e) => {
        if (e.target.classList.contains("btn-edit-cue")) {
          e.stopPropagation();
          seekTo(cue.startSec);
          openDetailCueModal(cue);
          return;
        }
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

  function openDetailCueModal(cue) {
    state.detailCue = { ...cue };
    const winMin = Math.max(0, cue.startSec - 15);
    const winMax = Math.max(cue.endSec + 15, winMin + 30);

    const startSlider = $("detailStartSlider");
    const endSlider = $("detailEndSlider");
    startSlider.min = String(winMin);
    startSlider.max = String(winMax);
    startSlider.value = String(cue.startSec);
    endSlider.min = String(winMin);
    endSlider.max = String(winMax);
    endSlider.value = String(cue.endSec);

    $("detailStartLabel").textContent = formatTimecode(cue.startSec);
    $("detailEndLabel").textContent = formatTimecode(cue.endSec);
    $("detailCategory").value = cue.category;
    $("detailChannel").value = cue.channel;
    $("detailAction").value = cue.action;
    $("detailDescription").value = cue.description || "";
    $("cueDetailModal").classList.remove("hidden");
  }

  async function saveDetailCue() {
    if (!state.detailCue) return;
    const origKey = state.detailCue.key;
    const payload = {
      start: formatTimecode(state.detailCue.startSec),
      end: formatTimecode(state.detailCue.endSec),
      category: $("detailCategory").value,
      channel: $("detailChannel").value,
      action: $("detailAction").value,
      description: $("detailDescription").value.trim() || null,
    };

    try {
      pushUndoSnapshot();
      if (state.itemId) {
        await apiFetch(`/ContentFilter/filters/${state.itemId}/segments/${encodeURIComponent(origKey)}`, {
          method: "PUT",
          body: JSON.stringify(payload),
        });
        await fetchJcfCues(state.itemId);
      } else {
        const updated = normalizeCue(payload);
        state.cues = state.cues.map((c) => (c.key === origKey ? updated : c));
        renderTimeline();
        renderCuesList();
        renderTranscript();
      }
      $("cueDetailModal").classList.add("hidden");
      setStatus("Updated cue.");
    } catch (err) {
      setStatus(`Failed to update cue: ${err.message}`, true);
    }
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
      pushUndoSnapshot();
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
    pushUndoSnapshot();
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
    pushUndoSnapshot();

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
  function nudgeField(fieldId, delta) {
    const input = $(fieldId);
    const next = Math.max(0, parseTimecode(input.value) + delta);
    input.value = formatTimecode(next);
    syncInOutFromForm();
  }

  function jumpToAdjacentCue(dir) {
    if (!state.cues.length) return;
    const t = video.readyState >= 1 ? (video.currentTime || 0) : state.virtualTime;
    const sorted = [...state.cues].sort((a, b) => a.startSec - b.startSec);
    const target = dir > 0
      ? sorted.find((c) => c.startSec > t + 0.15) || sorted[0]
      : [...sorted].reverse().find((c) => c.startSec < t - 0.15) || sorted[sorted.length - 1];
    if (target) {
      seekTo(target.startSec);
      loadCueIntoForm(target);
    }
  }

  function quickMarkCue(category, channel, action, description) {
    const t = video.readyState >= 1 ? (video.currentTime || 0) : state.virtualTime;
    const hasRange = state.inTime !== null && state.outTime !== null && state.outTime > state.inTime;
    const startSec = hasRange ? state.inTime : (action === "mute" ? Math.max(0, t - 0.5) : t);
    const endSec = hasRange ? state.outTime : (action === "mute" ? t + 0.5 : t + 2.0);

    resetCueForm();
    $("cueStart").value = formatTimecode(startSec);
    $("cueEnd").value = formatTimecode(endSec);
    $("cueCategory").value = category;
    $("cueChannel").value = channel;
    $("cueAction").value = action;
    $("cueDescription").value = description;
    syncInOutFromForm();
    $("cueForm").requestSubmit();
  }

  function onKeyDown(e) {
    const tag = (e.target?.tagName || "").toLowerCase();
    const isInput = ["input", "textarea", "select"].includes(tag) && e.target?.type !== "checkbox" && e.target?.type !== "range";
    if (isInput) return;

    const key = e.key.toLowerCase();
    if ((e.metaKey || e.ctrlKey) && key === "z") {
      e.preventDefault();
      undoLastAction();
      return;
    }
    if (e.code === "Space" || key === "k") {
      e.preventDefault();
      togglePlay();
    } else if (e.code === "ArrowLeft" || key === ",") {
      e.preventDefault();
      seekBy(e.shiftKey ? -1 : -FRAME_STEP);
    } else if (e.code === "ArrowRight" || key === ".") {
      e.preventDefault();
      seekBy(e.shiftKey ? 1 : FRAME_STEP);
    } else if (key === "a") {
      e.preventDefault();
      seekBy(-1);
    } else if (key === "d") {
      e.preventDefault();
      seekBy(1);
    } else if (key === "j") {
      e.preventDefault();
      seekBy(-5);
    } else if (key === "l") {
      e.preventDefault();
      seekBy(5);
    } else if (e.key === "[") {
      e.preventDefault();
      jumpToAdjacentCue(-1);
    } else if (e.key === "]") {
      e.preventDefault();
      jumpToAdjacentCue(1);
    } else if (key === "i") {
      e.preventDefault();
      markIn();
    } else if (key === "o") {
      e.preventDefault();
      markOut();
    } else if (key === "m") {
      e.preventDefault();
      $("cueForm").requestSubmit();
    } else if (key === "p") {
      e.preventDefault();
      quickMarkCue("Language.GeneralProfanity", "audio", "mute", "Profanity");
    } else if (key === "n") {
      e.preventDefault();
      quickMarkCue("SexAndNudity.FullNudity", "video", "skip", "Nudity");
    } else if (key === "v") {
      e.preventDefault();
      quickMarkCue("Violence.Graphic", "video", "skip", "Violence");
    } else if (key === "f") {
      e.preventDefault();
      $("previewFilters").checked = !$("previewFilters").checked;
      applyActiveFilters(video.readyState >= 1 ? (video.currentTime || 0) : state.virtualTime);
    } else if (e.key === "1") {
      e.preventDefault();
      nudgeField("cueStart", -0.1);
    } else if (e.key === "2") {
      e.preventDefault();
      nudgeField("cueStart", 0.1);
    } else if (e.key === "3") {
      e.preventDefault();
      nudgeField("cueEnd", -0.1);
    } else if (e.key === "4") {
      e.preventDefault();
      nudgeField("cueEnd", 0.1);
    } else if ((e.key === "Delete" || e.key === "Backspace") && state.editingCueKey) {
      e.preventDefault();
      deleteEditingCue();
    } else if (e.key === "Escape") {
      resetCueForm();
      $("hotkeysModal").classList.add("hidden");
      $("markOutModal").classList.add("hidden");
      $("cueDetailModal").classList.add("hidden");
      $("libraryModal").classList.add("hidden");
      $("loginModal").classList.add("hidden");
      $("wordPopover").classList.add("hidden");
    } else if (e.key === "/" || e.key === "?") {
      e.preventDefault();
      $("hotkeysModal").classList.toggle("hidden");
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
