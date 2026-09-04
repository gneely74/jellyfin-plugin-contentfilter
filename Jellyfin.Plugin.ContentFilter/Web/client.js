(function () {
    'use strict';

    if (window._jellyfinContentFilterLoaded) {
        return;
    }
    window._jellyfinContentFilterLoaded = true;

    console.log('[ContentFilter] Client playback enforcement & Cue Editor HUD loaded.');

    var activeFilter = null;
    var activeItemId = null;
    var activeVideo = null;
    var pollInterval = null;
    var isMutedByFilter = false;
    var lastSkippedCue = null;
    var hudElement = null;
    var hudTimeout = null;

    // HUD Editor state
    var editorModal = null;
    var launchBtn = null;
    var editorTimeInterval = null;
    var currentActiveTab = 'add';
    var editingCueKey = null; // When non-null, editing an existing cue
    var subtitleTracks = null;
    var subtitleWords = null;
    var selectedSubtitleLanguage = 'eng';

    function parseTimecode(tc) {
        if (!tc) return 0;
        tc = String(tc).trim();
        // Pure seconds string, e.g. "1880" or "45.5"
        if (/^\d+(?:\.\d+)?$/.test(tc)) {
            return parseFloat(tc) || 0;
        }
        var parts = tc.split(':');
        if (parts.length === 3) {
            // hh:mm:ss[.fff]
            var h = parseFloat(parts[0]) || 0;
            var m = parseFloat(parts[1]) || 0;
            var s = parseFloat(parts[2].replace(',', '.')) || 0;
            return (h * 3600) + (m * 60) + s;
        } else if (parts.length === 2) {
            // mm:ss[.fff]
            var m2 = parseFloat(parts[0]) || 0;
            var s2 = parseFloat(parts[1].replace(',', '.')) || 0;
            return (m2 * 60) + s2;
        } else if (parts.length === 1) {
            return parseFloat(parts[0].replace(',', '.')) || 0;
        }
        return 0;
    }

    function formatTime(sec) {
        if (isNaN(sec) || sec < 0) sec = 0;
        var h = Math.floor(sec / 3600);
        var m = Math.floor((sec % 3600) / 60);
        var s = Math.floor(sec % 60);
        if (h > 0) {
            return h + ':' + (m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
        }
        return m + ':' + (s < 10 ? '0' : '') + s;
    }

    function formatTimecode(sec) {
        if (isNaN(sec) || sec < 0) sec = 0;
        var h = Math.floor(sec / 3600);
        var m = Math.floor((sec % 3600) / 60);
        var s = Math.floor(sec % 60);
        var ms = Math.floor((sec - Math.floor(sec)) * 1000);
        return (h < 10 ? '0' : '') + h + ':' +
               (m < 10 ? '0' : '') + m + ':' +
               (s < 10 ? '0' : '') + s + '.' +
               (ms < 100 ? (ms < 10 ? '00' : '0') : '') + ms;
    }

    function getPlayerContainer() {
        if (document.fullscreenElement) {
            return document.fullscreenElement;
        }
        return document.body;
    }

    // --- Toast HUD ---
    function ensureHud() {
        var container = getPlayerContainer();
        if (hudElement && container.contains(hudElement)) {
            return hudElement;
        }
        if (!hudElement) {
            hudElement = document.createElement('div');
            hudElement.id = 'contentFilterHud';
            hudElement.style.cssText = [
                'position: fixed',
                'top: 24px',
                'right: 24px',
                'z-index: 2147483646',
                'background: rgba(15, 23, 42, 0.92)',
                'color: #38bdf8',
                'border: 1px solid rgba(56, 189, 248, 0.4)',
                'box-shadow: 0 10px 25px -5px rgba(0, 0, 0, 0.6), 0 0 15px rgba(56, 189, 248, 0.2)',
                'backdrop-filter: blur(12px)',
                '-webkit-backdrop-filter: blur(12px)',
                'padding: 10px 18px',
                'border-radius: 12px',
                'font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
                'font-size: 14px',
                'font-weight: 600',
                'display: flex',
                'align-items: center',
                'gap: 10px',
                'opacity: 0',
                'transform: translateY(-8px)',
                'transition: opacity 0.25s ease, transform 0.25s ease',
                'pointer-events: none'
            ].join(';');
        }
        if (!container.contains(hudElement)) {
            container.appendChild(hudElement);
        }
        return hudElement;
    }

    function showHud(text, icon) {
        try {
            var el = ensureHud();
            el.innerHTML = '<span style="font-size:18px;">' + (icon || '🛡️') + '</span><span>' + text + '</span>';
            el.style.opacity = '1';
            el.style.transform = 'translateY(0)';

            if (hudTimeout) {
                clearTimeout(hudTimeout);
            }
            hudTimeout = setTimeout(function () {
                el.style.opacity = '0';
                el.style.transform = 'translateY(-8px)';
            }, 3200);
        } catch (e) {
            console.warn('[ContentFilter] Failed to show HUD:', e);
        }
    }

    function getApiClient() {
        if (window.ApiClient) return window.ApiClient;
        if (window.ServerConnections && typeof window.ServerConnections.currentApiClient === 'function') {
            return window.ServerConnections.currentApiClient();
        }
        return null;
    }

    function fetchItemFilter(itemId) {
        var client = getApiClient();
        if (!client || !itemId) return Promise.resolve(null);

        var url = client.getUrl('ContentFilter/filters/' + itemId);
        var token = client.accessToken ? client.accessToken() : '';

        return fetch(url, {
            headers: { 'X-Emby-Token': token }
        }).then(function (res) {
            if (!res.ok) return null;
            return res.json();
        }).then(function (data) {
            if (!data) return null;

            var rawCues = data.cues || [];
            var parsedCues = rawCues.map(function (c) {
                return {
                    start: parseTimecode(c.start),
                    end: parseTimecode(c.end),
                    action: (c.action || 'none').toLowerCase(),
                    channel: (c.channel || 'both').toLowerCase(),
                    category: c.category || '',
                    description: c.description || c.category || 'Filtered Content',
                    key: c.key || (c.start + '-' + c.end + '-' + c.category)
                };
            }).filter(function (c) {
                return c.action !== 'none' && c.end > c.start;
            });

            parsedCues.sort(function (a, b) { return a.start - b.start; });

            console.log('[ContentFilter] Loaded ' + parsedCues.length + ' cue(s) for item ' + itemId);
            return {
                itemId: itemId,
                title: data.title || '',
                cues: parsedCues
            };
        }).catch(function (err) {
            console.warn('[ContentFilter] Could not load filter for item ' + itemId, err);
            return null;
        });
    }

    function resolveMediaItemId(video) {
        // 1. Direct match on HTML5 video stream URL
        if (video) {
            var src = video.currentSrc || video.src || '';
            var m = src.match(/(?:\/videos\/|[?&]mediasourceid=|\/videos\/transcode\/)([a-f0-9]{32})/i);
            if (m) return m[1];
        }

        // 2. Jellyfin playbackManager
        try {
            if (window.playbackManager && typeof window.playbackManager.currentItem === 'function') {
                var curItem = window.playbackManager.currentItem();
                if (curItem && curItem.Id) return curItem.Id;
            }
        } catch (e) {}

        // 3. Current URL hash
        var hash = window.location.hash || '';
        var match = hash.match(/[?&]id=([a-f0-9]{32})/i);
        if (match) return match[1];

        return null;
    }


    // --- Subtitle Masking & Redaction (Leaving First Letter) ---
    var activeTargetWords = new Set();
    var globalBlanketWords = new Set();

    function maskLeavingFirstLetter(word) {
        if (!word) return word;
        return word.replace(/\b\w+/g, function (match) {
            if (match.length <= 1) return match;
            return match[0] + "*".repeat(match.length - 1);
        });
    }

    function escapeRegExp(string) {
        return string.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    }

    function sanitizeSubtitleText(text, targetWords) {
        if (!text || !targetWords || targetWords.size === 0) return text;
        var output = text;
        var words = Array.from(targetWords).sort(function (a, b) {
            return b.length - a.length;
        });
        for (var i = 0; i < words.length; i++) {
            var w = words[i].trim();
            if (!w) continue;
            var re = new RegExp("\\b" + escapeRegExp(w) + "\\b", "gi");
            output = output.replace(re, function (match) {
                return maskLeavingFirstLetter(match);
            });
        }
        return output;
    }

    function updateTargetWords() {
        activeTargetWords.clear();
        globalBlanketWords.forEach(function (w) {
            if (w) activeTargetWords.add(w.toLowerCase());
        });

        if (activeFilter && activeFilter.cues) {
            for (var i = 0; i < activeFilter.cues.length; i++) {
                var c = activeFilter.cues[i];
                if (c.action === "mute" || c.action === "skip") {
                    if (c.description) {
                        var m = c.description.match(/"([^"]+)"/);
                        if (m && m[1]) {
                            activeTargetWords.add(m[1].toLowerCase());
                        } else if (!c.description.includes(" ") && c.description.length < 30) {
                            activeTargetWords.add(c.description.toLowerCase());
                        }
                    }
                }
            }
        }
        if (activeVideo) {
            hookTextTracks(activeVideo);
        }
    }

    function loadGlobalBlanketWords() {
        var client = getApiClient();
        if (!client) return;
        var url = client.getUrl("ContentFilter/subtitles/global-blanket-words");
        var token = client.accessToken ? client.accessToken() : "";
        fetch(url, { headers: { "X-Emby-Token": token } })
            .then(function (res) { return res.ok ? res.json() : []; })
            .then(function (words) {
                globalBlanketWords.clear();
                if (Array.isArray(words)) {
                    words.forEach(function (w) {
                        if (w) globalBlanketWords.add(w.toLowerCase());
                    });
                }
                updateTargetWords();
            }).catch(function () {});
    }

    function sanitizeTrackCues(track) {
        if (!track || !track.cues) return;
        for (var i = 0; i < track.cues.length; i++) {
            var c = track.cues[i];
            if (c && c.text) {
                var sanitized = sanitizeSubtitleText(c.text, activeTargetWords);
                if (sanitized !== c.text) {
                    c.text = sanitized;
                }
            }
        }
    }

    function hookTextTracks(video) {
        if (!video || !video.textTracks) return;
        var tracks = video.textTracks;
        for (var i = 0; i < tracks.length; i++) {
            var tr = tracks[i];
            sanitizeTrackCues(tr);
            if (!tr._cfHooked) {
                tr._cfHooked = true;
                tr.addEventListener("cuechange", function () {
                    sanitizeTrackCues(this);
                });
            }
        }
        if (!tracks._cfHooked) {
            tracks._cfHooked = true;
            tracks.addEventListener("addtrack", function (e) {
                if (e.track) {
                    sanitizeTrackCues(e.track);
                    e.track.addEventListener("cuechange", function () {
                        sanitizeTrackCues(this);
                    });
                }
            });
        }
    }

    function checkCleanSubStatus() {
        var modal = ensureEditorModal();
        var banner = modal.querySelector("#cfCleanSubStatusBanner");
        var client = getApiClient();
        if (!client || !activeItemId || !banner) return;

        var url = client.getUrl("ContentFilter/subtitles/" + activeItemId + "/filtered-status?language=" + encodeURIComponent(selectedSubtitleLanguage));
        var token = client.accessToken ? client.accessToken() : "";

        fetch(url, { headers: { "X-Emby-Token": token } })
            .then(function (res) { return res.ok ? res.json() : null; })
            .then(function (data) {
                if (data && (data.hasSidecar || data.HasSidecar)) {
                    banner.style.display = "block";
                    banner.style.color = "#34d399";
                    banner.style.borderColor = "rgba(52, 211, 153, 0.4)";
                    banner.style.background = "rgba(16, 185, 129, 0.15)";
                    banner.innerHTML = "✓ <strong>Clean Subtitles Active</strong> — Filtered .srt sidecar is saved on disk and indexed by Jellyfin (Universal device support: Apple TV, Roku, Fire TV, Swiftfin).";
                } else if (data && (data.hasPluginSubtitle || data.HasPluginSubtitle)) {
                    banner.style.display = "block";
                    banner.style.color = "#38bdf8";
                    banner.style.borderColor = "rgba(56, 189, 248, 0.4)";
                    banner.style.background = "rgba(56, 189, 248, 0.15)";
                    banner.innerHTML = "ℹ️ <strong>Clean Subtitles Generated</strong> in plugin cache.";
                } else {
                    banner.style.display = "none";
                }
            }).catch(function () {
                banner.style.display = "none";
            });
    }

    function generateCleanSubtitles() {
        var modal = ensureEditorModal();
        var genBtn = modal.querySelector("#cfBtnGenCleanSubs");
        var statusMsg = modal.querySelector("#cfWordsStatusMsg");
        var client = getApiClient();
        if (!client || !activeItemId) return;

        if (genBtn) {
            genBtn.disabled = true;
            genBtn.innerHTML = "⏳ Generating...";
        }
        if (statusMsg) {
            statusMsg.style.color = "#38bdf8";
            statusMsg.textContent = "⏳ Generating clean .srt subtitles with first-letter word masking...";
        }

        var url = client.getUrl("ContentFilter/subtitles/" + activeItemId + "/generate-filtered?language=" + encodeURIComponent(selectedSubtitleLanguage));
        var token = client.accessToken ? client.accessToken() : "";

        fetch(url, {
            method: "POST",
            headers: { "X-Emby-Token": token }
        }).then(function (res) {
            if (!res.ok) throw new Error("Server returned " + res.status);
            return res.json();
        }).then(function (data) {
            if (genBtn) {
                genBtn.disabled = false;
                genBtn.innerHTML = "📄 Generate Clean Subtitles (.srt)";
            }
            if (statusMsg) {
                statusMsg.style.color = "#10b981";
                statusMsg.textContent = "✅ Clean subtitle sidecar created! Jellyfin was notified to index it.";
            }
            showHud("Clean subtitles (.srt) generated!", "📄");
            checkCleanSubStatus();
        }).catch(function (err) {
            if (genBtn) {
                genBtn.disabled = false;
                genBtn.innerHTML = "📄 Generate Clean Subtitles (.srt)";
            }
            if (statusMsg) {
                statusMsg.style.color = "#ef4444";
                statusMsg.textContent = "Failed to generate clean subtitles: " + err.message;
            }
        });
    }

    function checkCues() {
        if (!activeFilter || !activeVideo || activeVideo.paused) {
            return;
        }

        // Live sanitize visible subtitle elements
        try {
            var subEls = document.querySelectorAll('.subtitleappearance-text, .track-cues, .subtitleText, [class*="subtitleText"], [class*="subtitleappearance"]');
            for (var s = 0; s < subEls.length; s++) {
                var el = subEls[s];
                if (el && el.textContent) {
                    var cleanedText = sanitizeSubtitleText(el.textContent, activeTargetWords);
                    if (cleanedText !== el.textContent) {
                        el.textContent = cleanedText;
                    }
                }
            }
        } catch (e) {}


        var cur = activeVideo.currentTime;
        var cues = activeFilter.cues || [];
        var shouldMute = false;
        var muteDescription = '';

        for (var i = 0; i < cues.length; i++) {
            var cue = cues[i];

            // 1. Skip check (video or both channels)
            if (cue.action === 'skip' && cue.channel !== 'audio') {
                if (cur >= (cue.start - 0.2) && cur < (cue.end - 0.1)) {
                    if (lastSkippedCue !== cue.key) {
                        lastSkippedCue = cue.key;
                        console.log('[ContentFilter] Skipping cue:', cue.category, 'from', cur, 'to', cue.end);

                        activeVideo.currentTime = cue.end;

                        try {
                            if (window.playbackManager && typeof window.playbackManager.seek === 'function') {
                                window.playbackManager.seek(Math.round(cue.end * 10000000));
                            }
                        } catch (e) {}

                        var label = cue.description || cue.category.split('.').pop() || 'Filtered Scene';
                        showHud('Skipped ' + label + ' (' + formatTime(cue.start) + ' - ' + formatTime(cue.end) + ')', '⏩');

                        if (isMutedByFilter) {
                            activeVideo.muted = false;
                            isMutedByFilter = false;
                        }
                    }
                    return;
                }
            }

            // 2. Mute check (mute action or audio-channel skip)
            if (cue.action === 'mute' || (cue.action === 'skip' && cue.channel === 'audio')) {
                if (cur >= (cue.start - 0.2) && cur < cue.end) {
                    shouldMute = true;
                    muteDescription = cue.description || cue.category.split('.').pop() || 'Audio Filtered';
                }
            }
        }

        if (lastSkippedCue) {
            var prevCue = cues.find(function (c) { return c.key === lastSkippedCue; });
            if (!prevCue || cur >= (prevCue.end + 1.0) || cur < (prevCue.start - 1.0)) {
                lastSkippedCue = null;
            }
        }

        if (shouldMute && !isMutedByFilter) {
            console.log('[ContentFilter] Muting audio for cue:', muteDescription);
            activeVideo.muted = true;
            isMutedByFilter = true;
            showHud('Muted ' + muteDescription, '🔇');
        } else if (!shouldMute && isMutedByFilter) {
            console.log('[ContentFilter] Unmuting audio — past cue');
            activeVideo.muted = false;
            isMutedByFilter = false;
        }
    }

    // --- Floating Cue Editor Launcher ---
    function ensureLauncherButton() {
        var container = getPlayerContainer();
        if (launchBtn && container.contains(launchBtn)) {
            return launchBtn;
        }
        if (!launchBtn) {
            launchBtn = document.createElement('button');
            launchBtn.id = 'cfEditorLaunchBtn';
            launchBtn.title = 'Open Content Filter Cue Editor (Hotkey: C)';
            launchBtn.innerHTML = '<span style="font-size:16px;">🛡️</span> <span>Cue Editor</span>';
            launchBtn.style.cssText = [
                'position: fixed',
                'top: 76px',
                'left: 24px',
                'z-index: 2147483647',
                'pointer-events: auto !important',
                'background: rgba(15, 23, 42, 0.88)',
                'color: #38bdf8',
                'border: 1px solid rgba(56, 189, 248, 0.4)',
                'box-shadow: 0 4px 15px rgba(0, 0, 0, 0.4)',
                'backdrop-filter: blur(10px)',
                '-webkit-backdrop-filter: blur(10px)',
                'padding: 8px 16px',
                'border-radius: 9999px',
                'font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
                'font-size: 13px',
                'font-weight: 600',
                'display: flex',
                'align-items: center',
                'gap: 8px',
                'cursor: pointer',
                'outline: none',
                'transition: all 0.2s ease',
                'user-select: none'
            ].join(';');

            launchBtn.addEventListener('mouseenter', function () {
                launchBtn.style.background = 'rgba(56, 189, 248, 0.2)';
                launchBtn.style.borderColor = '#38bdf8';
                launchBtn.style.transform = 'scale(1.04)';
            });
            launchBtn.addEventListener('mouseleave', function () {
                launchBtn.style.background = 'rgba(15, 23, 42, 0.88)';
                launchBtn.style.borderColor = 'rgba(56, 189, 248, 0.4)';
                launchBtn.style.transform = 'scale(1)';
            });
            launchBtn.addEventListener('click', function (e) {
                e.stopPropagation();
                e.preventDefault();
                toggleEditorModal();
            });
        }
        if (!container.contains(launchBtn)) {
            container.appendChild(launchBtn);
        }
        return launchBtn;
    }

    function removeLauncherButton() {
        if (launchBtn && launchBtn.parentElement) {
            launchBtn.parentElement.removeChild(launchBtn);
        }
    }

    // Helper to stop key events from leaking to Jellyfin player shortcuts
    function isolateInput(input) {
        if (!input) return;
        ['keydown', 'keyup', 'keypress'].forEach(function (evType) {
            input.addEventListener(evType, function (e) {
                e.stopPropagation();
            }, true);
        });
    }

    // --- In-Player Cue Editor Modal ---
    function ensureEditorModal() {
        var container = getPlayerContainer();
        if (editorModal && container.contains(editorModal)) {
            return editorModal;
        }

        if (!editorModal) {
            editorModal = document.createElement('div');
            editorModal.id = 'cfEditorModal';
            editorModal.style.cssText = [
                'position: fixed',
                'top: 50%',
                'left: 50%',
                'transform: translate(-50%, -50%)',
                'z-index: 2147483647',
                'width: 560px',
                'max-width: 95vw',
                'max-height: 88vh',
                'overflow-y: auto',
                'background: rgba(15, 23, 42, 0.97)',
                'color: #f8fafc',
                'border: 1px solid rgba(56, 189, 248, 0.4)',
                'box-shadow: 0 25px 60px -15px rgba(0, 0, 0, 0.8), 0 0 35px rgba(56, 189, 248, 0.2)',
                'backdrop-filter: blur(20px)',
                '-webkit-backdrop-filter: blur(20px)',
                'border-radius: 18px',
                'padding: 22px',
                'box-sizing: border-box',
                'font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
                'font-size: 13px',
                'display: none',
                'flex-direction: column',
                'gap: 16px',
                'user-select: none'
            ].join(';');

            editorModal.innerHTML = [
                // Draggable Header
                '<div id="cfEditorHeader" style="display:flex; justify-content:space-between; align-items:center; border-bottom:1px solid rgba(255,255,255,0.1); padding-bottom:12px; cursor:grab; user-select:none;">',
                '  <div style="display:flex; align-items:center; gap:10px;">',
                '    <span style="font-size:22px;">🛡️</span>',
                '    <div>',
                '      <div style="font-size:16px; font-weight:700; color:#38bdf8; display:flex; align-items:center; gap:8px;">',
                '        <span>Content Filter • Cue Editor</span>',
                '        <span style="font-size:10px; background:rgba(255,255,255,0.1); color:#94a3b8; padding:2px 6px; border-radius:4px; font-weight:500;">⋮⋮ Drag to Move</span>',
                '      </div>',
                '      <div id="cfModalItemTitle" style="font-size:12px; color:#94a3b8; max-width:320px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis;">Media Item</div>',
                '    </div>',
                '  </div>',
                '  <button id="cfModalCloseBtn" style="background:rgba(255,255,255,0.1); border:none; color:#f8fafc; width:30px; height:30px; border-radius:50%; cursor:pointer; font-size:16px; display:flex; align-items:center; justify-content:center; transition:background 0.2s;">✕</button>',
                '</div>',

                // Live Timecode Bar & Frame Nudge
                '<div style="background:rgba(30, 41, 59, 0.7); border:1px solid rgba(255,255,255,0.08); border-radius:12px; padding:12px 14px; display:flex; justify-content:space-between; align-items:center;">',
                '  <div>',
                '    <div style="font-size:11px; color:#94a3b8; font-weight:600; text-transform:uppercase; letter-spacing:0.5px;">Playback Timecode</div>',
                '    <div id="cfLiveTimecode" style="font-family:ui-monospace,SFMono-Regular,Menlo,Monaco,Consolas,monospace; font-size:20px; font-weight:700; color:#38bdf8; margin-top:2px;">00:00:00.000</div>',
                '  </div>',
                '  <div style="display:flex; gap:6px; align-items:center;">',
                '    <button id="cfFrameBack1" title="Step Back 1s" style="background:rgba(255,255,255,0.1); border:none; color:#f8fafc; padding:6px 10px; border-radius:8px; cursor:pointer; font-size:12px; font-weight:600;">-1s</button>',
                '    <button id="cfFrameBackFine" title="Step Back 0.1s" style="background:rgba(255,255,255,0.1); border:none; color:#f8fafc; padding:6px 10px; border-radius:8px; cursor:pointer; font-size:12px; font-weight:600;">-0.1s</button>',
                '    <button id="cfPlayPauseToggle" title="Play/Pause" style="background:#0284c7; border:none; color:#ffffff; padding:6px 12px; border-radius:8px; cursor:pointer; font-size:13px; font-weight:700;">⏯️</button>',
                '    <button id="cfFrameFwdFine" title="Step Forward 0.1s" style="background:rgba(255,255,255,0.1); border:none; color:#f8fafc; padding:6px 10px; border-radius:8px; cursor:pointer; font-size:12px; font-weight:600;">+0.1s</button>',
                '    <button id="cfFrameFwd1" title="Step Forward 1s" style="background:rgba(255,255,255,0.1); border:none; color:#f8fafc; padding:6px 10px; border-radius:8px; cursor:pointer; font-size:12px; font-weight:600;">+1s</button>',
                '  </div>',
                '</div>',

                // Navigation Tabs
                '<div style="display:flex; gap:8px; border-bottom:1px solid rgba(255,255,255,0.1); padding-bottom:8px; flex-wrap:wrap;">',
                '  <button id="cfTabBtnAdd" style="background:rgba(56, 189, 248, 0.2); color:#38bdf8; border:1px solid rgba(56, 189, 248, 0.4); padding:7px 14px; border-radius:8px; font-weight:600; cursor:pointer; font-size:12px;">➕ Set Cue Point</button>',
                '  <button id="cfTabBtnShift" style="background:transparent; color:#94a3b8; border:1px solid transparent; padding:7px 14px; border-radius:8px; font-weight:600; cursor:pointer; font-size:12px;">⏱️ Shift Cues</button>',
                '  <button id="cfTabBtnWords" style="background:transparent; color:#94a3b8; border:1px solid transparent; padding:7px 14px; border-radius:8px; font-weight:600; cursor:pointer; font-size:12px;">💬 Subtitle Words (<span id="cfTabWordsCount">0</span>)</button>',
                '  <button id="cfTabBtnList" style="background:transparent; color:#94a3b8; border:1px solid transparent; padding:7px 14px; border-radius:8px; font-weight:600; cursor:pointer; font-size:12px;">📋 Active Cues (<span id="cfTabCuesCount">0</span>)</button>',
                '</div>',

                // Tab 1: Set / Add / Edit Cue View
                '<div id="cfTabPaneAdd" style="display:flex; flex-direction:column; gap:14px;">',
                '  <!-- Edit Banner (hidden unless editing) -->',
                '  <div id="cfEditBanner" style="display:none; justify-content:space-between; align-items:center; background:rgba(234, 179, 8, 0.15); border:1px solid rgba(234, 179, 8, 0.4); border-radius:8px; padding:8px 12px; color:#fef08a; font-size:12px;">',
                '    <div style="display:flex; align-items:center; gap:8px;">',
                '      <div id="cfEditBannerText">✏️ Editing Cue</div>',
                '      <div style="display:flex; gap:4px; align-items:center; margin-left:4px;">',
                '        <button id="cfBannerShiftM10" title="Shift cue 10s earlier" style="background:rgba(234, 179, 8, 0.25); border:1px solid rgba(234, 179, 8, 0.6); color:#fef08a; padding:2px 8px; border-radius:4px; cursor:pointer; font-size:11px; font-weight:700;">-10s</button>',
                '        <button id="cfBannerShiftP10" title="Shift cue 10s later" style="background:rgba(234, 179, 8, 0.25); border:1px solid rgba(234, 179, 8, 0.6); color:#fef08a; padding:2px 8px; border-radius:4px; cursor:pointer; font-size:11px; font-weight:700;">+10s</button>',
                '      </div>',
                '    </div>',
                '    <button id="cfBtnCancelEdit" style="background:transparent; border:1px solid rgba(234, 179, 8, 0.5); color:#fef08a; padding:3px 8px; border-radius:6px; cursor:pointer; font-size:11px;">Cancel Edit</button>',
                '  </div>',

                '  <!-- Start (In-Point) -->',
                '  <div>',
                '    <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:6px;">',
                '      <label style="font-weight:600; color:#cbd5e1;">Cue In (Start Time):</label>',
                '      <div style="display:flex; gap:4px;">',
                '        <button id="cfNudgeStartM1" style="background:rgba(255,255,255,0.08); border:none; color:#94a3b8; padding:3px 6px; border-radius:4px; cursor:pointer; font-size:11px;">-1s</button>',
                '        <button id="cfNudgeStartM02" style="background:rgba(255,255,255,0.08); border:none; color:#94a3b8; padding:3px 6px; border-radius:4px; cursor:pointer; font-size:11px;">-0.2s</button>',
                '        <button id="cfNudgeStartP02" style="background:rgba(255,255,255,0.08); border:none; color:#94a3b8; padding:3px 6px; border-radius:4px; cursor:pointer; font-size:11px;">+0.2s</button>',
                '        <button id="cfNudgeStartP1" style="background:rgba(255,255,255,0.08); border:none; color:#94a3b8; padding:3px 6px; border-radius:4px; cursor:pointer; font-size:11px;">+1s</button>',
                '      </div>',
                '    </div>',
                '    <div style="display:flex; gap:8px;">',
                '      <input id="cfInputStart" type="text" placeholder="e.g. 31:20 or 00:31:20.000" style="flex:1; background:rgba(30, 41, 59, 0.9); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:8px 12px; border-radius:8px; font-family:ui-monospace,monospace; font-size:14px; user-select:text;">',
                '      <button id="cfBtnMarkStart" style="background:#0284c7; color:#fff; border:none; padding:8px 14px; border-radius:8px; font-weight:600; cursor:pointer; font-size:12px; display:flex; align-items:center; gap:6px;">📍 Mark In (Now)</button>',
                '    </div>',
                '  </div>',

                '  <!-- End (Out-Point) -->',
                '  <div>',
                '    <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:6px;">',
                '      <label style="font-weight:600; color:#cbd5e1;">Cue Out (End Time):</label>',
                '      <div style="display:flex; gap:4px;">',
                '        <button id="cfQuickDuration5" style="background:rgba(56, 189, 248, 0.15); border:1px solid rgba(56, 189, 248, 0.3); color:#38bdf8; padding:3px 6px; border-radius:4px; cursor:pointer; font-size:11px;">+5s</button>',
                '        <button id="cfQuickDuration15" style="background:rgba(56, 189, 248, 0.15); border:1px solid rgba(56, 189, 248, 0.3); color:#38bdf8; padding:3px 6px; border-radius:4px; cursor:pointer; font-size:11px;">+15s</button>',
                '        <button id="cfQuickDuration30" style="background:rgba(56, 189, 248, 0.15); border:1px solid rgba(56, 189, 248, 0.3); color:#38bdf8; padding:3px 6px; border-radius:4px; cursor:pointer; font-size:11px;">+30s</button>',
                '        <button id="cfNudgeEndM02" style="background:rgba(255,255,255,0.08); border:none; color:#94a3b8; padding:3px 6px; border-radius:4px; cursor:pointer; font-size:11px;">-0.2s</button>',
                '        <button id="cfNudgeEndP02" style="background:rgba(255,255,255,0.08); border:none; color:#94a3b8; padding:3px 6px; border-radius:4px; cursor:pointer; font-size:11px;">+0.2s</button>',
                '      </div>',
                '    </div>',
                '    <div style="display:flex; gap:8px;">',
                '      <input id="cfInputEnd" type="text" placeholder="e.g. 31:45 or 00:31:45.000" style="flex:1; background:rgba(30, 41, 59, 0.9); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:8px 12px; border-radius:8px; font-family:ui-monospace,monospace; font-size:14px; user-select:text;">',
                '      <button id="cfBtnMarkEnd" style="background:#0284c7; color:#fff; border:none; padding:8px 14px; border-radius:8px; font-weight:600; cursor:pointer; font-size:12px; display:flex; align-items:center; gap:6px;">📍 Mark Out (Now)</button>',
                '    </div>',
                '  </div>',
                '',
                '  <!-- Shift Entire Cue Block -->',
                '  <div style="background:rgba(30, 41, 59, 0.7); border:1px solid rgba(255,255,255,0.08); border-radius:10px; padding:8px 12px; display:flex; justify-content:space-between; align-items:center;">',
                '    <div>',
                '      <span style="font-weight:600; color:#cbd5e1; font-size:12px;">Shift Entire Cue:</span>',
                '      <span style="font-size:11px; color:#94a3b8; margin-left:6px;">(moves In & Out together)</span>',
                '    </div>',
                '    <div style="display:flex; gap:4px; align-items:center;">',
                '      <button id="cfShiftCueM10" title="Shift entire cue 10s earlier" style="background:rgba(234, 179, 8, 0.2); border:1px solid rgba(234, 179, 8, 0.5); color:#fef08a; padding:4px 8px; border-radius:6px; cursor:pointer; font-weight:700; font-size:11px;">-10s</button>',
                '      <button id="cfShiftCueM5" title="Shift cue 5s earlier" style="background:rgba(255,255,255,0.08); border:none; color:#cbd5e1; padding:4px 7px; border-radius:6px; cursor:pointer; font-weight:600; font-size:11px;">-5s</button>',
                '      <button id="cfShiftCueM1" title="Shift cue 1s earlier" style="background:rgba(255,255,255,0.08); border:none; color:#cbd5e1; padding:4px 7px; border-radius:6px; cursor:pointer; font-weight:600; font-size:11px;">-1s</button>',
                '      <button id="cfShiftCueP1" title="Shift cue 1s later" style="background:rgba(255,255,255,0.08); border:none; color:#cbd5e1; padding:4px 7px; border-radius:6px; cursor:pointer; font-weight:600; font-size:11px;">+1s</button>',
                '      <button id="cfShiftCueP5" title="Shift cue 5s later" style="background:rgba(255,255,255,0.08); border:none; color:#cbd5e1; padding:4px 7px; border-radius:6px; cursor:pointer; font-weight:600; font-size:11px;">+5s</button>',
                '      <button id="cfShiftCueP10" title="Shift entire cue 10s later" style="background:rgba(234, 179, 8, 0.2); border:1px solid rgba(234, 179, 8, 0.5); color:#fef08a; padding:4px 8px; border-radius:6px; cursor:pointer; font-weight:700; font-size:11px;">+10s</button>',
                '    </div>',
                '  </div>',

                '  <!-- Category & Channel/Action -->',
                '  <div style="display:grid; grid-template-columns:1fr 1fr; gap:12px;">',
                '    <div>',
                '      <label style="font-weight:600; color:#cbd5e1; display:block; margin-bottom:6px;">Category Tag:</label>',
                '      <select id="cfSelectCategory" style="width:100%; background:rgba(30, 41, 59, 0.9); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:8px 10px; border-radius:8px; font-size:13px;">',
                '        <optgroup label="Violence & Horror">',
                '          <option value="Violence.Mild">Violence.Mild (Slaps, Comic Action)</option>',
                '          <option value="Violence.Moderate">Violence.Moderate (Combat, Shootouts)</option>',
                '          <option value="Violence.Graphic">Violence.Graphic (Fatal, Visceral)</option>',
                '          <option value="Violence.Gore">Violence.Gore (Blood, Dismemberment)</option>',
                '          <option value="Violence.JumpScares">Violence.JumpScares (Startle)</option>',
                '          <option value="Violence.Disturbing">Violence.Disturbing (Corpses, Trauma)</option>',
                '        </optgroup>',
                '        <optgroup label="Sex & Nudity">',
                '          <option value="SexAndNudity.Graphic">SexAndNudity.Graphic (Intercourse)</option>',
                '          <option value="SexAndNudity.ImpliedSex">SexAndNudity.ImpliedSex (Suggestive)</option>',
                '          <option value="SexAndNudity.SexualAssault">SexAndNudity.SexualAssault</option>',
                '          <option value="SexAndNudity.FullNudity">SexAndNudity.FullNudity (Frontal)</option>',
                '          <option value="SexAndNudity.PartialNudity">SexAndNudity.PartialNudity (Lingerie)</option>',
                '          <option value="SexAndNudity.PhysicalIntimacy">SexAndNudity.PhysicalIntimacy (Kissing)</option>',
                '          <option value="SexAndNudity.Mild">SexAndNudity.Mild (Swimwear)</option>',
                '        </optgroup>',
                '        <optgroup label="Language & Sexual References">',
                '          <option value="Language.GeneralProfanity">Language.GeneralProfanity</option>',
                '          <option value="Language.Blasphemy">Language.Blasphemy</option>',
                '          <option value="Language.RacialAndBigotedSlurs">Language.RacialAndBigotedSlurs</option>',
                '          <option value="Language.ChildishLanguage">Language.ChildishLanguage</option>',
                '          <option value="SexualReferences.ContextualDialogue">SexualReferences.ContextualDialogue</option>',
                '          <option value="SexualReferences.Visuals">SexualReferences.Visuals</option>',
                '        </optgroup>',
                '        <optgroup label="Substances">',
                '          <option value="Substances.Alcohol">Substances.Alcohol (Drinking)</option>',
                '          <option value="Substances.Tobacco">Substances.Tobacco (Smoking)</option>',
                '          <option value="Substances.IllegalDrugs">Substances.IllegalDrugs (Narcotics)</option>',
                '        </optgroup>',
                '        <optgroup label="Medical & Structural">',
                '          <option value="Medical.Events">Medical.Events (Procedures)</option>',
                '          <option value="Medical.BodilyFunctions">Medical.BodilyFunctions (Vomit)</option>',
                '          <option value="Structural.Credits">Structural.Credits</option>',
                '          <option value="Structural.IntroRecap">Structural.IntroRecap</option>',
                '        </optgroup>',
                '        <optgroup label="Legacy & Custom">',
                '          <option value="Violence.Tiers">Violence.Tiers (Legacy)</option>',
                '          <option value="SexAndNudity.NudityProfiles">SexAndNudity.NudityProfiles (Legacy)</option>',
                '          <option value="SexAndNudity.OnscreenActivity">SexAndNudity.OnscreenActivity (Legacy)</option>',
                '          <option value="Substances.Usage">Substances.Usage (Legacy)</option>',
                '          <option value="__custom__">Custom Category...</option>',
                '        </optgroup>',
                '      </select>',
                '      <input id="cfInputCustomCategory" type="text" placeholder="Enter custom category" style="display:none; width:100%; margin-top:6px; box-sizing:border-box; background:rgba(30, 41, 59, 0.9); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:6px 10px; border-radius:6px; font-size:12px; user-select:text;">',
                '    </div>',
                '    <div>',
                '      <label style="font-weight:600; color:#cbd5e1; display:block; margin-bottom:6px;">Action & Channel:</label>',
                '      <div style="display:flex; gap:6px;">',
                '        <select id="cfSelectAction" style="flex:1; background:rgba(30, 41, 59, 0.9); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:8px 6px; border-radius:8px; font-size:13px;">',
                '          <option value="skip">Skip Scene</option>',
                '          <option value="mute">Mute Audio</option>',
                '        </select>',
                '        <select id="cfSelectChannel" style="flex:1; background:rgba(30, 41, 59, 0.9); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:8px 6px; border-radius:8px; font-size:13px;">',
                '          <option value="video">Video</option>',
                '          <option value="both">Both</option>',
                '          <option value="audio">Audio</option>',
                '        </select>',
                '      </div>',
                '    </div>',
                '  </div>',

                '  <!-- Description -->',
                '  <div>',
                '    <label style="font-weight:600; color:#cbd5e1; display:block; margin-bottom:6px;">Description (Optional):</label>',
                '    <input id="cfInputDescription" type="text" placeholder="e.g. Battle decapitation" style="width:100%; box-sizing:border-box; background:rgba(30, 41, 59, 0.9); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:8px 12px; border-radius:8px; font-size:13px; user-select:text;">',
                '  </div>',

                '  <!-- Save Button & Status -->',
                '  <div style="display:flex; justify-content:space-between; align-items:center; margin-top:4px;">',
                '    <span id="cfAddStatusMsg" style="font-size:12px; color:#38bdf8; font-weight:600;"></span>',
                '    <button id="cfBtnSaveCue" style="background:#10b981; color:#ffffff; border:none; padding:10px 20px; border-radius:10px; font-weight:700; cursor:pointer; font-size:13px; display:flex; align-items:center; gap:8px; transition:background 0.2s;">💾 Save Cue to Episode</button>',
                '  </div>',
                '</div>',

                // Tab 2: Shift All Cues View
                '<div id="cfTabPaneShift" style="display:none; flex-direction:column; gap:14px;">',
                '  <div style="background:rgba(30, 41, 59, 0.5); border:1px solid rgba(56, 189, 248, 0.2); border-radius:10px; padding:12px; color:#cbd5e1; font-size:12px; line-height:1.5;">',
                '    💡 <strong>Shift Cues:</strong> If the cues are off by a fixed amount because your video file has an extra logo or different cut, shift cues earlier or later. You can shift video and audio cues together or separately.',
                '  </div>',
                '',
                '  <div>',
                '    <label style="font-weight:600; color:#cbd5e1; display:block; margin-bottom:6px;">Target Channel:</label>',
                '    <select id="cfSelectShiftChannel" style="width:100%; background:rgba(30, 41, 59, 0.9); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:8px 10px; border-radius:8px; font-size:13px;">',
                '      <option value="all">⚡ All Cues (Together)</option>',
                '      <option value="video">🎬 Video Cues Only (Skips & Visuals)</option>',
                '      <option value="audio">🔊 Audio Cues Only (Mutes & Dialogue)</option>',
                '    </select>',
                '  </div>',

                '  <div>',
                '    <label style="font-weight:600; color:#cbd5e1; display:block; margin-bottom:8px;">Quick Shift Presets:</label>',
                '    <div style="display:flex; flex-wrap:wrap; gap:6px;">',
                '      <button class="cf-shift-preset" data-sec="-10" style="background:rgba(255,255,255,0.08); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:6px 12px; border-radius:6px; cursor:pointer; font-weight:600;">-10s</button>',
                '      <button class="cf-shift-preset" data-sec="-5" style="background:rgba(255,255,255,0.08); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:6px 12px; border-radius:6px; cursor:pointer; font-weight:600;">-5s</button>',
                '      <button class="cf-shift-preset" data-sec="-2" style="background:rgba(255,255,255,0.08); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:6px 12px; border-radius:6px; cursor:pointer; font-weight:600;">-2s</button>',
                '      <button class="cf-shift-preset" data-sec="-1" style="background:rgba(255,255,255,0.08); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:6px 12px; border-radius:6px; cursor:pointer; font-weight:600;">-1s</button>',
                '      <button class="cf-shift-preset" data-sec="-0.5" style="background:rgba(255,255,255,0.08); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:6px 12px; border-radius:6px; cursor:pointer; font-weight:600;">-0.5s</button>',
                '      <button class="cf-shift-preset" data-sec="0.5" style="background:rgba(255,255,255,0.08); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:6px 12px; border-radius:6px; cursor:pointer; font-weight:600;">+0.5s</button>',
                '      <button class="cf-shift-preset" data-sec="1" style="background:rgba(255,255,255,0.08); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:6px 12px; border-radius:6px; cursor:pointer; font-weight:600;">+1s</button>',
                '      <button class="cf-shift-preset" data-sec="2" style="background:rgba(255,255,255,0.08); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:6px 12px; border-radius:6px; cursor:pointer; font-weight:600;">+2s</button>',
                '      <button class="cf-shift-preset" data-sec="5" style="background:rgba(255,255,255,0.08); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:6px 12px; border-radius:6px; cursor:pointer; font-weight:600;">+5s</button>',
                '      <button class="cf-shift-preset" data-sec="10" style="background:rgba(255,255,255,0.08); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:6px 12px; border-radius:6px; cursor:pointer; font-weight:600;">+10s</button>',
                '    </div>',
                '  </div>',

                '  <div>',
                '    <label style="font-weight:600; color:#cbd5e1; display:block; margin-bottom:6px;">Custom Offset (seconds, e.g. +3.5 or -2.4):</label>',
                '    <div style="display:flex; gap:8px;">',
                '      <input id="cfInputShiftSec" type="number" step="0.1" value="0.0" style="flex:1; background:rgba(30, 41, 59, 0.9); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:8px 12px; border-radius:8px; font-size:14px; user-select:text;">',
                '      <button id="cfBtnApplyShift" style="background:#0284c7; color:#fff; border:none; padding:8px 16px; border-radius:8px; font-weight:700; cursor:pointer; font-size:13px; display:flex; align-items:center; gap:6px;">⚡ Apply Shift</button>',
                '    </div>',
                '  </div>',

                '  <div id="cfShiftStatusMsg" style="font-size:12px; color:#10b981; font-weight:600; min-height:16px;"></div>',
                '</div>',

                // Tab 3: Subtitle Words View
                '<div id="cfTabPaneWords" style="display:none; flex-direction:column; gap:12px;">',
                '  <div style="background:rgba(30, 41, 59, 0.7); border:1px solid rgba(255,255,255,0.08); border-radius:10px; padding:10px 12px; display:flex; justify-content:space-between; align-items:center; gap:8px; flex-wrap:wrap;">',
                '    <div style="display:flex; align-items:center; gap:8px;">',
                '      <label style="font-weight:600; color:#cbd5e1; font-size:12px;">Language:</label>',
                '      <select id="cfSelectSubLanguage" style="background:rgba(15, 23, 42, 0.9); border:1px solid rgba(255,255,255,0.2); color:#f8fafc; padding:5px 8px; border-radius:6px; font-size:12px; max-width:210px;">',
                '        <option value="eng">English (eng)</option>',
                '      </select>',
                '    </div>',
                '    <div style="display:flex; gap:6px; align-items:center;">',
                '      <button id="cfBtnScanSubs" style="background:#0284c7; border:none; color:#fff; padding:6px 12px; border-radius:6px; font-weight:600; cursor:pointer; font-size:12px; display:flex; align-items:center; gap:4px;">🔄 Scan Subtitles</button>',
                '      <button id="cfBtnGenCleanSubs" title="Generate clean .srt sidecar with profanity masked leaving first letter" style="background:rgba(16, 185, 129, 0.2); border:1px solid rgba(16, 185, 129, 0.5); color:#6ee7b7; padding:6px 12px; border-radius:6px; font-weight:600; cursor:pointer; font-size:12px; display:flex; align-items:center; gap:4px;">📄 Generate Clean Subtitles (.srt)</button>',
                '    </div>',
                '  </div>',
                '  <div id="cfCleanSubStatusBanner" style="font-size:11px; padding:8px 12px; border-radius:8px; display:none;"></div>',
                '  <div style="display:flex; gap:8px; align-items:center;">',
                '    <input id="cfInputSearchWords" type="text" placeholder="Search detected words (e.g. bastard, profanity)..." style="flex:1; background:rgba(30, 41, 59, 0.9); border:1px solid rgba(255,255,255,0.15); color:#f8fafc; padding:7px 10px; border-radius:8px; font-size:12px; user-select:text;">',
                '    <button id="cfBtnBlanketVisibleWords" title="Blanket filter all currently visible words" style="background:rgba(234, 179, 8, 0.2); border:1px solid rgba(234, 179, 8, 0.5); color:#fef08a; padding:7px 12px; border-radius:8px; font-weight:700; cursor:pointer; font-size:12px; white-space:nowrap;">⚡ Blanket All</button>',
                '  </div>',
                '  <div id="cfWordsStatusMsg" style="font-size:11px; color:#94a3b8; min-height:16px;"></div>',
                '  <div id="cfWordsListContainer" style="max-height:300px; overflow-y:auto; display:flex; flex-direction:column; gap:8px; padding-right:4px;">',
                '    <div style="text-align:center; color:#94a3b8; padding:24px;">Click "Scan Subtitles" to detect filterable words.</div>',
                '  </div>',
                '</div>',

                // Tab 4: Active Cues List View
                '<div id="cfTabPaneList" style="display:none; flex-direction:column; gap:10px;">',
                '  <div id="cfCuesListContainer" style="max-height:300px; overflow-y:auto; display:flex; flex-direction:column; gap:8px; padding-right:4px;">',
                '    <div style="text-align:center; color:#94a3b8; padding:20px;">No cues loaded</div>',
                '  </div>',
                '</div>'
            ].join('\n');

            wireEditorEvents(editorModal);
        }

        if (!container.contains(editorModal)) {
            container.appendChild(editorModal);
        }

        return editorModal;
    }

    function makeDraggable(modal, handle) {
        var isDragging = false;
        var startX, startY, initialLeft, initialTop;

        function startDrag(e) {
            // Only drag on left mouse click or touch, and not on buttons/inputs
            if (e.type === 'mousedown' && e.button !== 0) return;
            if (e.target.tagName === 'BUTTON' || e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT') return;

            isDragging = true;
            handle.style.cursor = 'grabbing';

            var point = e.touches ? e.touches[0] : e;
            startX = point.clientX;
            startY = point.clientY;

            var rect = modal.getBoundingClientRect();
            initialLeft = rect.left;
            initialTop = rect.top;

            modal.style.transform = 'none';
            modal.style.left = initialLeft + 'px';
            modal.style.top = initialTop + 'px';

            document.addEventListener('mousemove', onDrag, true);
            document.addEventListener('mouseup', stopDrag, true);
            document.addEventListener('touchmove', onDrag, { passive: false, capture: true });
            document.addEventListener('touchend', stopDrag, true);

            e.preventDefault();
        }

        function onDrag(e) {
            if (!isDragging) return;
            var point = e.touches ? e.touches[0] : e;
            var dx = point.clientX - startX;
            var dy = point.clientY - startY;

            var newLeft = initialLeft + dx;
            var newTop = initialTop + dy;

            // Clamp inside window
            var maxLeft = window.innerWidth - modal.offsetWidth - 10;
            var maxTop = window.innerHeight - modal.offsetHeight - 10;
            newLeft = Math.max(10, Math.min(maxLeft, newLeft));
            newTop = Math.max(10, Math.min(maxTop, newTop));

            modal.style.left = newLeft + 'px';
            modal.style.top = newTop + 'px';

            if (e.cancelable) e.preventDefault();
        }

        function stopDrag() {
            if (!isDragging) return;
            isDragging = false;
            handle.style.cursor = 'grab';
            document.removeEventListener('mousemove', onDrag, true);
            document.removeEventListener('mouseup', stopDrag, true);
            document.removeEventListener('touchmove', onDrag, true);
            document.removeEventListener('touchend', stopDrag, true);
        }

        handle.addEventListener('mousedown', startDrag);
        handle.addEventListener('touchstart', startDrag, { passive: false });
    }

    function wireEditorEvents(modal) {
        var header = modal.querySelector('#cfEditorHeader');
        makeDraggable(modal, header);

        var closeBtn = modal.querySelector('#cfModalCloseBtn');
        closeBtn.addEventListener('click', closeEditorModal);

        // Frame controls
        modal.querySelector('#cfFrameBack1').addEventListener('click', function () {
            if (activeVideo) activeVideo.currentTime = Math.max(0, activeVideo.currentTime - 1.0);
        });
        modal.querySelector('#cfFrameBackFine').addEventListener('click', function () {
            if (activeVideo) activeVideo.currentTime = Math.max(0, activeVideo.currentTime - 0.1);
        });
        modal.querySelector('#cfFrameFwdFine').addEventListener('click', function () {
            if (activeVideo) activeVideo.currentTime = Math.min(activeVideo.duration || 999999, activeVideo.currentTime + 0.1);
        });
        modal.querySelector('#cfFrameFwd1').addEventListener('click', function () {
            if (activeVideo) activeVideo.currentTime = Math.min(activeVideo.duration || 999999, activeVideo.currentTime + 1.0);
        });
        modal.querySelector('#cfPlayPauseToggle').addEventListener('click', function () {
            if (activeVideo) {
                if (activeVideo.paused) activeVideo.play();
                else activeVideo.pause();
            }
        });

        // Tabs
        var tabBtnAdd = modal.querySelector('#cfTabBtnAdd');
        var tabBtnShift = modal.querySelector('#cfTabBtnShift');
        var tabBtnWords = modal.querySelector('#cfTabBtnWords');
        var tabBtnList = modal.querySelector('#cfTabBtnList');
        var paneAdd = modal.querySelector('#cfTabPaneAdd');
        var paneShift = modal.querySelector('#cfTabPaneShift');
        var paneWords = modal.querySelector('#cfTabPaneWords');
        var paneList = modal.querySelector('#cfTabPaneList');

        function switchTab(tab) {
            currentActiveTab = tab;
            tabBtnAdd.style.background = tab === 'add' ? 'rgba(56, 189, 248, 0.2)' : 'transparent';
            tabBtnAdd.style.color = tab === 'add' ? '#38bdf8' : '#94a3b8';
            tabBtnAdd.style.borderColor = tab === 'add' ? 'rgba(56, 189, 248, 0.4)' : 'transparent';

            tabBtnShift.style.background = tab === 'shift' ? 'rgba(56, 189, 248, 0.2)' : 'transparent';
            tabBtnShift.style.color = tab === 'shift' ? '#38bdf8' : '#94a3b8';
            tabBtnShift.style.borderColor = tab === 'shift' ? 'rgba(56, 189, 248, 0.4)' : 'transparent';

            tabBtnWords.style.background = tab === 'words' ? 'rgba(56, 189, 248, 0.2)' : 'transparent';
            tabBtnWords.style.color = tab === 'words' ? '#38bdf8' : '#94a3b8';
            tabBtnWords.style.borderColor = tab === 'words' ? 'rgba(56, 189, 248, 0.4)' : 'transparent';

            tabBtnList.style.background = tab === 'list' ? 'rgba(56, 189, 248, 0.2)' : 'transparent';
            tabBtnList.style.color = tab === 'list' ? '#38bdf8' : '#94a3b8';
            tabBtnList.style.borderColor = tab === 'list' ? 'rgba(56, 189, 248, 0.4)' : 'transparent';

            paneAdd.style.display = tab === 'add' ? 'flex' : 'none';
            paneShift.style.display = tab === 'shift' ? 'flex' : 'none';
            paneWords.style.display = tab === 'words' ? 'flex' : 'none';
            paneList.style.display = tab === 'list' ? 'flex' : 'none';

            if (tab === 'words') {
                loadAndRenderSubtitleWords(false);
                checkCleanSubStatus();
            } else if (tab === 'list') {
                renderActiveCuesList();
            }
        }

        tabBtnAdd.addEventListener('click', function () { switchTab('add'); });
        tabBtnShift.addEventListener('click', function () { switchTab('shift'); });
        tabBtnWords.addEventListener('click', function () { switchTab('words'); });
        tabBtnList.addEventListener('click', function () { switchTab('list'); });

        // Category dropdown custom trigger
        var catSelect = modal.querySelector('#cfSelectCategory');
        var customCatInput = modal.querySelector('#cfInputCustomCategory');
        catSelect.addEventListener('change', function () {
            if (catSelect.value === '__custom__') {
                customCatInput.style.display = 'block';
                customCatInput.focus();
            } else {
                customCatInput.style.display = 'none';
            }
        });

        // Inputs
        var startInput = modal.querySelector('#cfInputStart');
        var endInput = modal.querySelector('#cfInputEnd');
        var descInput = modal.querySelector('#cfInputDescription');
        var shiftInput = modal.querySelector('#cfInputShiftSec');

        // Isolate inputs from global Jellyfin hotkeys
        isolateInput(startInput);
        isolateInput(endInput);
        isolateInput(descInput);
        isolateInput(shiftInput);
        isolateInput(customCatInput);

        var searchWordsInput = modal.querySelector('#cfInputSearchWords');
        isolateInput(searchWordsInput);
        searchWordsInput.addEventListener('input', function () {
            renderSubtitleWordsList(searchWordsInput.value.trim());
        });

        var langSelect = modal.querySelector('#cfSelectSubLanguage');
        langSelect.addEventListener('change', function () {
            selectedSubtitleLanguage = langSelect.value;
            loadAndRenderSubtitleWords(true);
        });

        var scanBtn = modal.querySelector('#cfBtnScanSubs');
        scanBtn.addEventListener('click', function () {
            loadAndRenderSubtitleWords(true);
        });
        var genCleanBtn = modal.querySelector('#cfBtnGenCleanSubs');
        if (genCleanBtn) {
            genCleanBtn.addEventListener('click', function () {
                generateCleanSubtitles();
            });
        }

        var blanketVisibleBtn = modal.querySelector('#cfBtnBlanketVisibleWords');
        blanketVisibleBtn.addEventListener('click', function () {
            blanketFilterVisibleWords();
        });

        // Normalize inputs on blur if user typed freeform (e.g. "31:20" -> "00:31:20.000")
        startInput.addEventListener('blur', function () {
            if (startInput.value.trim()) {
                var s = parseTimecode(startInput.value);
                if (s > 0) startInput.value = formatTimecode(s);
            }
        });
        endInput.addEventListener('blur', function () {
            if (endInput.value.trim()) {
                var e = parseTimecode(endInput.value);
                if (e > 0) endInput.value = formatTimecode(e);
            }
        });

        // Mark Start & Mark End
        modal.querySelector('#cfBtnMarkStart').addEventListener('click', function () {
            if (activeVideo) {
                startInput.value = formatTimecode(activeVideo.currentTime);
                if (!endInput.value || parseTimecode(endInput.value) <= activeVideo.currentTime) {
                    endInput.value = formatTimecode(activeVideo.currentTime + 10);
                }
            }
        });

        modal.querySelector('#cfBtnMarkEnd').addEventListener('click', function () {
            if (activeVideo) {
                endInput.value = formatTimecode(activeVideo.currentTime);
                if (!startInput.value) {
                    startInput.value = formatTimecode(Math.max(0, activeVideo.currentTime - 10));
                }
            }
        });

        // Start Nudges
        function nudgeStart(delta) {
            var val = parseTimecode(startInput.value || (activeVideo ? activeVideo.currentTime : 0));
            startInput.value = formatTimecode(Math.max(0, val + delta));
        }
        modal.querySelector('#cfNudgeStartM1').addEventListener('click', function () { nudgeStart(-1.0); });
        modal.querySelector('#cfNudgeStartM02').addEventListener('click', function () { nudgeStart(-0.2); });
        modal.querySelector('#cfNudgeStartP02').addEventListener('click', function () { nudgeStart(0.2); });
        modal.querySelector('#cfNudgeStartP1').addEventListener('click', function () { nudgeStart(1.0); });

        // End Nudges & Durations
        function nudgeEnd(delta) {
            var val = parseTimecode(endInput.value || (activeVideo ? activeVideo.currentTime : 0));
            endInput.value = formatTimecode(Math.max(0, val + delta));
        }
        function setRelativeDuration(dur) {
            var startVal = parseTimecode(startInput.value || (activeVideo ? activeVideo.currentTime : 0));
            endInput.value = formatTimecode(startVal + dur);
        }
        modal.querySelector('#cfQuickDuration5').addEventListener('click', function () { setRelativeDuration(5); });
        modal.querySelector('#cfQuickDuration15').addEventListener('click', function () { setRelativeDuration(15); });
        modal.querySelector('#cfQuickDuration30').addEventListener('click', function () { setRelativeDuration(30); });
        modal.querySelector('#cfNudgeEndM02').addEventListener('click', function () { nudgeEnd(-0.2); });
        modal.querySelector('#cfNudgeEndP02').addEventListener('click', function () { nudgeEnd(0.2); });

        // Shift Entire Cue handler (moves In and Out points together)
        function shiftEntireCue(deltaSec) {
            var sVal = parseTimecode(startInput.value);
            var eVal = parseTimecode(endInput.value);
            if (sVal === 0 && eVal === 0 && activeVideo) {
                sVal = activeVideo.currentTime;
                eVal = activeVideo.currentTime + 10;
            }
            var duration = Math.max(0.1, eVal - sVal);
            var newStart = Math.max(0, sVal + deltaSec);
            var newEnd = newStart + duration;

            startInput.value = formatTimecode(newStart);
            endInput.value = formatTimecode(newEnd);

            if (activeVideo && activeVideo.paused) {
                activeVideo.currentTime = newStart;
            }

            var sign = deltaSec > 0 ? '+' : '';
            showHud('Shifted cue by ' + sign + deltaSec + 's (' + formatTime(newStart) + ' - ' + formatTime(newEnd) + ')', '⏱️');
        }

        modal.querySelector('#cfShiftCueM10').addEventListener('click', function () { shiftEntireCue(-10); });
        modal.querySelector('#cfShiftCueM5').addEventListener('click', function () { shiftEntireCue(-5); });
        modal.querySelector('#cfShiftCueM1').addEventListener('click', function () { shiftEntireCue(-1); });
        modal.querySelector('#cfShiftCueP1').addEventListener('click', function () { shiftEntireCue(1); });
        modal.querySelector('#cfShiftCueP5').addEventListener('click', function () { shiftEntireCue(5); });
        modal.querySelector('#cfShiftCueP10').addEventListener('click', function () { shiftEntireCue(10); });

        var bannerShiftM10 = modal.querySelector('#cfBannerShiftM10');
        var bannerShiftP10 = modal.querySelector('#cfBannerShiftP10');
        if (bannerShiftM10) bannerShiftM10.addEventListener('click', function () { shiftEntireCue(-10); });
        if (bannerShiftP10) bannerShiftP10.addEventListener('click', function () { shiftEntireCue(10); });

        // Cancel Edit handler
        var editBanner = modal.querySelector('#cfEditBanner');
        var btnCancelEdit = modal.querySelector('#cfBtnCancelEdit');
        var saveBtn = modal.querySelector('#cfBtnSaveCue');
        var addStatus = modal.querySelector('#cfAddStatusMsg');

        function cancelEditing() {
            editingCueKey = null;
            editBanner.style.display = 'none';
            saveBtn.textContent = '💾 Save Cue to Episode';
            saveBtn.style.background = '#10b981';
            tabBtnAdd.textContent = '➕ Set Cue Point';
            startInput.value = '';
            endInput.value = '';
            descInput.value = '';
            catSelect.value = 'Violence.Moderate';
            customCatInput.style.display = 'none';
            customCatInput.value = '';
            addStatus.textContent = '';
        }
        btnCancelEdit.addEventListener('click', cancelEditing);

        // Save / Update Cue Handler
        saveBtn.addEventListener('click', function () {
            var sRaw = startInput.value.trim();
            var eRaw = endInput.value.trim();
            if (!sRaw || !eRaw) {
                addStatus.style.color = '#ef4444';
                addStatus.textContent = 'Please provide Start and End time.';
                return;
            }

            var startSec = parseTimecode(sRaw);
            var endSec = parseTimecode(eRaw);
            if (endSec <= startSec) {
                addStatus.style.color = '#ef4444';
                addStatus.textContent = 'End time must be greater than Start time.';
                return;
            }

            var sFormatted = formatTimecode(startSec);
            var eFormatted = formatTimecode(endSec);

            var cat = catSelect.value === '__custom__' ? customCatInput.value.trim() : catSelect.value;
            if (!cat) cat = 'General';

            var channel = modal.querySelector('#cfSelectChannel').value;
            var action = modal.querySelector('#cfSelectAction').value;
            var desc = descInput.value.trim();

            var client = getApiClient();
            if (!client || !activeItemId) {
                addStatus.style.color = '#ef4444';
                addStatus.textContent = 'No active media item or ApiClient.';
                return;
            }

            var isEdit = !!editingCueKey;
            saveBtn.disabled = true;
            saveBtn.textContent = isEdit ? 'Updating...' : 'Saving...';

            var payload = {
                start: sFormatted,
                end: eFormatted,
                category: cat,
                channel: channel,
                action: action,
                description: desc
            };

            var url = isEdit
                ? client.getUrl('ContentFilter/filters/' + activeItemId + '/segments/' + encodeURIComponent(editingCueKey))
                : client.getUrl('ContentFilter/filters/' + activeItemId + '/segments');

            var method = isEdit ? 'PUT' : 'POST';
            var token = client.accessToken ? client.accessToken() : '';

            fetch(url, {
                method: method,
                headers: {
                    'Content-Type': 'application/json',
                    'X-Emby-Token': token
                },
                body: JSON.stringify(payload)
            }).then(function (res) {
                saveBtn.disabled = false;
                saveBtn.textContent = isEdit ? '💾 Update Cue' : '💾 Save Cue to Episode';

                if (res.ok) {
                    addStatus.style.color = '#10b981';
                    addStatus.textContent = isEdit
                        ? '✅ Cue updated! (' + sFormatted + ' - ' + eFormatted + ')'
                        : '✅ Cue saved! (' + sFormatted + ' - ' + eFormatted + ')';

                    if (!activeFilter) {
                        activeFilter = { itemId: activeItemId, title: '', cues: [] };
                    }

                    var newCue = {
                        start: startSec,
                        end: endSec,
                        category: cat,
                        channel: channel,
                        action: action,
                        description: desc || cat,
                        key: sFormatted + '-' + eFormatted + '-' + cat
                    };

                    if (isEdit) {
                        var idx = activeFilter.cues.findIndex(function (c) { return c.key === editingCueKey; });
                        if (idx !== -1) {
                            activeFilter.cues[idx] = newCue;
                        } else {
                            activeFilter.cues.push(newCue);
                        }
                    } else {
                        activeFilter.cues.push(newCue);
                    }

                    activeFilter.cues.sort(function (a, b) { return a.start - b.start; });

                    cancelEditing();
                    updateCuesBadge();
                    showHud((isEdit ? 'Updated ' : 'Added ') + cat + ' (' + formatTime(startSec) + ' - ' + formatTime(endSec) + ')', '✅');

                    setTimeout(function () {
                        switchTab('list');
                    }, 500);
                } else {
                    res.text().then(function (txt) {
                        addStatus.style.color = '#ef4444';
                        addStatus.textContent = 'Failed: ' + txt;
                    });
                }
            }).catch(function (err) {
                saveBtn.disabled = false;
                saveBtn.textContent = isEdit ? '💾 Update Cue' : '💾 Save Cue to Episode';
                addStatus.style.color = '#ef4444';
                addStatus.textContent = 'Error: ' + err.message;
            });
        });

        // Shift Cues Presets & Apply
        var shiftStatus = modal.querySelector('#cfShiftStatusMsg');
        var applyShiftBtn = modal.querySelector('#cfBtnApplyShift');
        var shiftChannelSelect = modal.querySelector('#cfSelectShiftChannel');

        modal.querySelectorAll('.cf-shift-preset').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var sec = parseFloat(btn.getAttribute('data-sec')) || 0;
                shiftInput.value = sec > 0 ? ('+' + sec) : sec;
                executeShift(sec);
            });
        });

        applyShiftBtn.addEventListener('click', function () {
            var sec = parseFloat(shiftInput.value) || 0;
            executeShift(sec);
        });

        function executeShift(sec) {
            if (sec === 0) {
                shiftStatus.textContent = 'Offset is 0 seconds.';
                return;
            }

            var client = getApiClient();
            if (!client || !activeItemId) {
                shiftStatus.textContent = 'No active media item or ApiClient.';
                return;
            }

            var targetChannel = (shiftChannelSelect ? shiftChannelSelect.value : 'all') || 'all';

            applyShiftBtn.disabled = true;
            applyShiftBtn.textContent = 'Shifting ' + targetChannel + '...';

            var url = client.getUrl('ContentFilter/filters/' + activeItemId + '/segments/offset');
            var token = client.accessToken ? client.accessToken() : '';

            fetch(url, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'X-Emby-Token': token
                },
                body: JSON.stringify({ offsetSeconds: sec, channel: targetChannel })
            }).then(function (res) {
                applyShiftBtn.disabled = false;
                applyShiftBtn.textContent = '⚡ Apply Shift';

                if (res.ok) {
                    return res.json();
                } else {
                    throw new Error('Server returned ' + res.status);
                }
            }).then(function (data) {
                var channelLabel = data.channel === 'video' ? 'video' : (data.channel === 'audio' ? 'audio' : 'total');
                var count = data.shiftedCues !== undefined ? data.shiftedCues : (data.totalCues || 0);

                shiftStatus.style.color = '#10b981';
                shiftStatus.textContent = '✅ Shifted ' + count + ' ' + channelLabel + ' cues by ' + (sec > 0 ? '+' : '') + sec + 's!';

                fetchItemFilter(activeItemId).then(function (refreshed) {
                    if (refreshed) activeFilter = refreshed;
                    updateCuesBadge();
                    if (currentActiveTab === 'list') renderActiveCuesList();
                    showHud('Shifted ' + channelLabel + ' cues by ' + (sec > 0 ? '+' : '') + sec + 's (' + count + ' cues)', '⏱️');
                });
            }).catch(function (err) {
                applyShiftBtn.disabled = false;
                applyShiftBtn.textContent = '⚡ Apply Shift';
                shiftStatus.style.color = '#ef4444';
                shiftStatus.textContent = 'Shift failed: ' + err.message;
            });
        }
    }

    function escHtml(str) {
        if (!str) return '';
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    function loadSubtitleTracks() {
        var client = getApiClient();
        if (!client || !activeItemId) return Promise.resolve([]);
        var url = client.getUrl('ContentFilter/subtitles/' + activeItemId + '/tracks');
        var token = client.accessToken ? client.accessToken() : '';

        return fetch(url, {
            headers: { 'X-Emby-Token': token }
        }).then(function (res) {
            if (!res.ok) return [];
            return res.json();
        }).then(function (tracks) {
            subtitleTracks = tracks || [];
            populateLanguageSelector();
            return subtitleTracks;
        }).catch(function (e) {
            console.warn('[ContentFilter] Failed to fetch subtitle tracks:', e);
            return [];
        });
    }

    function populateLanguageSelector() {
        var modal = ensureEditorModal();
        var sel = modal.querySelector('#cfSelectSubLanguage');
        if (!sel) return;

        var tracks = subtitleTracks || [];
        sel.innerHTML = '';

        if (tracks.length === 0) {
            var opt = document.createElement('option');
            opt.value = 'eng';
            opt.textContent = 'English (eng) [default]';
            sel.appendChild(opt);
            return;
        }

        // Sort: English first
        var sorted = tracks.slice().sort(function (a, b) {
            var aLang = (a.Language || a.language || '').toLowerCase();
            var bLang = (b.Language || b.language || '').toLowerCase();
            var aIsEng = aLang.startsWith('en') ? 1 : 0;
            var bIsEng = bLang.startsWith('en') ? 1 : 0;
            return bIsEng - aIsEng;
        });

        sorted.forEach(function (tr) {
            var opt = document.createElement('option');
            var lang = tr.Language || tr.language || 'eng';
            var idx = (tr.Index !== undefined) ? tr.Index : tr.index;
            var isExt = (tr.IsExternal !== undefined) ? tr.IsExternal : tr.isExternal;
            var dName = tr.DisplayName || tr.displayName || lang;
            var cdc = tr.Codec || tr.codec;
            var cdcText = cdc ? (' (' + cdc + ')') : '';
            var extText = isExt ? ' [ext]' : '';

            opt.value = isExt ? lang : String(idx);
            opt.textContent = dName + cdcText + extText;
            sel.appendChild(opt);
        });

        // Default to full non-forced English subtitles if available
        var defaultOpt = Array.from(sel.options).find(function (o) {
            var text = o.textContent.toLowerCase();
            return text.indexOf('english') !== -1 && text.indexOf('forced') === -1;
        }) || Array.from(sel.options).find(function (o) {
            return o.textContent.toLowerCase().indexOf('english') !== -1;
        }) || sel.options[0];

        if (defaultOpt) {
            defaultOpt.selected = true;
            selectedSubtitleLanguage = defaultOpt.value;
        }
    }

    function loadAndRenderSubtitleWords(forceRefresh) {
        var modal = ensureEditorModal();
        var statusMsg = modal.querySelector('#cfWordsStatusMsg');
        var container = modal.querySelector('#cfWordsListContainer');
        var wordsBadge = modal.querySelector('#cfTabWordsCount');
        var client = getApiClient();

        if (!client || !activeItemId) {
            if (statusMsg) statusMsg.textContent = 'No active media item.';
            return;
        }

        if (!subtitleTracks || forceRefresh) {
            loadSubtitleTracks();
        }

        if (subtitleWords && !forceRefresh) {
            renderSubtitleWordsList(modal.querySelector('#cfInputSearchWords').value.trim());
            return;
        }

        if (statusMsg) {
            statusMsg.style.color = '#38bdf8';
            statusMsg.textContent = '⏳ Scanning subtitles for ' + selectedSubtitleLanguage + '...';
        }
        if (container) {
            container.innerHTML = '<div style="text-align:center; color:#94a3b8; padding:24px;">Scanning subtitles for filterable words...</div>';
        }

        var url = client.getUrl('ContentFilter/subtitles/' + activeItemId + '/words?language=' + encodeURIComponent(selectedSubtitleLanguage));
        var token = client.accessToken ? client.accessToken() : '';

        fetch(url, {
            headers: { 'X-Emby-Token': token }
        }).then(function (res) {
            if (!res.ok) throw new Error('Server returned ' + res.status);
            return res.json();
        }).then(function (data) {
            subtitleWords = data;
            var list = (data && (data.Words || data.words)) || [];
            if (wordsBadge) wordsBadge.textContent = list.length;
            if (statusMsg) {
                statusMsg.style.color = '#10b981';
                statusMsg.textContent = '✅ Found ' + list.length + ' filterable word(s) (' + ((data.TotalOccurrences || data.totalOccurrences || data.TotalProfanities || data.totalProfanities || 0)) + ' total occurrences)';
            }
            renderSubtitleWordsList(modal.querySelector('#cfInputSearchWords').value.trim());
            checkCleanSubStatus();
        }).catch(function (err) {
            if (statusMsg) {
                statusMsg.style.color = '#ef4444';
                statusMsg.textContent = 'Scan failed: ' + err.message;
            }
            if (container) {
                container.innerHTML = '<div style="text-align:center; color:#ef4444; padding:20px;">Could not extract subtitles for this item.<br><span style="font-size:11px; color:#94a3b8;">Ensure media has embedded or external subtitles in ' + selectedSubtitleLanguage + '.</span></div>';
            }
        });
    }

    function renderSubtitleWordsList(searchTerm) {
        var modal = ensureEditorModal();
        var container = modal.querySelector('#cfWordsListContainer');
        if (!container) return;

        var groups = (subtitleWords && (subtitleWords.Words || subtitleWords.words)) || [];
        if (groups.length === 0) {
            container.innerHTML = '<div style="text-align:center; color:#94a3b8; padding:24px;">No filterable words found in this subtitle track.</div>';
            return;
        }

        var filterLower = (searchTerm || '').toLowerCase();
        var filteredGroups = groups.filter(function (g) {
            var gw = (g.Word || g.word || '').toLowerCase();
            var gc = (g.Category || g.category || '').toLowerCase();
            if (!filterLower) return true;
            return gw.indexOf(filterLower) !== -1 || gc.indexOf(filterLower) !== -1;
        });

        if (filteredGroups.length === 0) {
            container.innerHTML = '<div style="text-align:center; color:#94a3b8; padding:20px;">No words match "' + escHtml(searchTerm) + '".</div>';
            return;
        }

        container.innerHTML = filteredGroups.map(function (g, gIdx) {
            var gWord = g.Word || g.word || '';
            var gCat = g.Category || g.category || 'General';
            var gCount = (g.Count !== undefined) ? g.Count : g.count;
            var isFiltered = (g.IsFiltered !== undefined) ? g.IsFiltered : g.isFiltered;
            var isGlobal = (g.IsGlobalBlanket !== undefined) ? g.IsGlobalBlanket : g.isGlobalBlanket;
            var occList = g.Occurrences || g.occurrences || [];

            var catColor = gCat.indexOf('Slur') !== -1 ? '#ef4444' :
                           gCat.indexOf('Explicit') !== -1 ? '#ec4899' :
                           gCat.indexOf('Blasphemy') !== -1 ? '#f59e0b' : '#38bdf8';

            return [
                '<div class="cf-word-card" data-word="' + escHtml(gWord) + '" style="background:rgba(30, 41, 59, 0.75); border:1px solid ' + (isFiltered ? 'rgba(16, 185, 129, 0.3)' : 'rgba(255,255,255,0.08)') + '; border-radius:10px; padding:10px 12px; display:flex; flex-direction:column; gap:8px;">',
                '  <div style="display:flex; justify-content:space-between; align-items:center; flex-wrap:wrap; gap:6px;">',
                '    <div style="display:flex; align-items:center; gap:8px;">',
                '      <span style="font-size:14px; font-weight:700; color:#f8fafc;">' + escHtml(gWord) + '</span>',
                '      <span style="font-size:11px; background:rgba(255,255,255,0.1); color:' + catColor + '; padding:2px 7px; border-radius:4px; font-weight:600;">' + escHtml(gCat) + '</span>',
                '      <span style="font-size:11px; background:rgba(56, 189, 248, 0.15); color:#38bdf8; padding:2px 6px; border-radius:4px; font-weight:700;">' + gCount + 'x</span>',
                isFiltered ? '      <span style="font-size:11px; background:rgba(16, 185, 129, 0.2); color:#10b981; padding:2px 6px; border-radius:4px; font-weight:600;">✅ Filtered</span>' : '',
                '    </div>',
                '    <div style="display:flex; align-items:center; gap:6px;">',
                '      <label style="font-size:11px; color:#94a3b8; display:flex; align-items:center; gap:4px; cursor:pointer; user-select:none;" title="Filter this word across ALL media items">',
                '        <input type="checkbox" class="cf-word-global-cb" data-word="' + escHtml(gWord) + '" ' + (isGlobal ? 'checked' : '') + '> 🌐 Global',
                '      </label>',
                isFiltered
                    ? ('      <button class="cf-btn-unfilter-word" data-word="' + escHtml(gWord) + '" style="background:rgba(239, 68, 68, 0.2); border:1px solid rgba(239, 68, 68, 0.4); color:#ef4444; padding:4px 8px; border-radius:6px; cursor:pointer; font-size:11px; font-weight:600;">🗑️ Unfilter</button>')
                    : ('      <button class="cf-btn-blanket-word" data-word="' + escHtml(gWord) + '" style="background:#0284c7; border:none; color:#fff; padding:5px 10px; border-radius:6px; cursor:pointer; font-size:11px; font-weight:600;">⚡ Blanket Mute</button>'),
                '      <button class="cf-btn-toggle-occ" data-word-idx="' + gIdx + '" style="background:rgba(255,255,255,0.08); border:none; color:#cbd5e1; padding:4px 8px; border-radius:6px; cursor:pointer; font-size:11px;">▼</button>',
                '    </div>',
                '  </div>',
                '  <!-- Occurrences list (hidden by default) -->',
                '  <div id="cfOccList_' + gIdx + '" style="display:none; flex-direction:column; gap:6px; border-top:1px solid rgba(255,255,255,0.08); padding-top:6px; margin-top:2px;">',
                occList.map(function (occ) {
                    var escapedWord = gWord.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
                    var highlightedText = escHtml(occ.Text || occ.text || '').replace(new RegExp('(' + escapedWord + ')', 'gi'), '<strong style="color:#f87171; background:rgba(239,68,68,0.2); padding:1px 3px; border-radius:3px;">$1</strong>');
                    return [
                        '    <div style="background:rgba(15, 23, 42, 0.6); border:1px solid rgba(255,255,255,0.05); border-radius:6px; padding:6px 10px; display:flex; justify-content:space-between; align-items:center; gap:8px;">',
                        '      <div style="font-size:12px; color:#cbd5e1; line-height:1.4; flex:1;">',
                        '        <span style="font-family:monospace; color:#38bdf8; font-size:11px; margin-right:6px;">' + escHtml(occ.Start || occ.start || '') + '</span>',
                        '        <span>' + highlightedText + '</span>',
                        '      </div>',
                        '      <button class="cf-occ-jump-btn" data-sec="' + (occ.StartSeconds !== undefined ? occ.StartSeconds : occ.startSeconds) + '" style="background:rgba(56, 189, 248, 0.15); border:1px solid rgba(56, 189, 248, 0.3); color:#38bdf8; padding:3px 8px; border-radius:4px; cursor:pointer; font-size:11px; white-space:nowrap;">▶ Jump (-1.5s)</button>',
                        '    </div>'
                    ].join('');
                }).join(''),
                '  </div>',
                '</div>'
            ].join('');
        }).join('');

        // Wire occurrence toggle
        container.querySelectorAll('.cf-btn-toggle-occ').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var idx = btn.getAttribute('data-word-idx');
                var occEl = container.querySelector('#cfOccList_' + idx);
                if (occEl) {
                    var isShown = occEl.style.display === 'flex';
                    occEl.style.display = isShown ? 'none' : 'flex';
                    btn.textContent = isShown ? '▼' : '▲';
                }
            });
        });

        // Wire jump buttons inside occurrences
        container.querySelectorAll('.cf-occ-jump-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var sec = parseFloat(btn.getAttribute('data-sec')) || 0;
                if (activeVideo) {
                    activeVideo.currentTime = Math.max(0, sec - 1.5);
                    showHud('Jumped to ' + formatTime(Math.max(0, sec - 1.5)), '▶');
                }
            });
        });

        // Wire blanket button
        container.querySelectorAll('.cf-btn-blanket-word').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var word = btn.getAttribute('data-word');
                var card = btn.closest('.cf-word-card');
                var globalCb = card ? card.querySelector('.cf-word-global-cb') : null;
                var isGlobal = globalCb ? globalCb.checked : false;
                applyBlanketFilter(word, isGlobal);
            });
        });

        // Wire unfilter button
        container.querySelectorAll('.cf-btn-unfilter-word').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var word = btn.getAttribute('data-word');
                var card = btn.closest('.cf-word-card');
                var globalCb = card ? card.querySelector('.cf-word-global-cb') : null;
                var isGlobal = globalCb ? globalCb.checked : false;
                removeWordFilter(word, isGlobal);
            });
        });
    }

    function applyBlanketFilter(word, isGlobal) {
        var client = getApiClient();
        if (!client || !activeItemId) return;

        var modal = ensureEditorModal();
        var statusMsg = modal.querySelector('#cfWordsStatusMsg');
        if (statusMsg) {
            statusMsg.style.color = '#38bdf8';
            statusMsg.textContent = 'Applying blanket filter for "' + word + '"...';
        }

        var url = client.getUrl('ContentFilter/subtitles/' + activeItemId + '/blanket-filter');
        var token = client.accessToken ? client.accessToken() : '';

        fetch(url, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'X-Emby-Token': token
            },
            body: JSON.stringify({
                word: word,
                language: selectedSubtitleLanguage,
                action: 'mute',
                global: isGlobal
            })
        }).then(function (res) {
            if (!res.ok) throw new Error('Server returned ' + res.status);
            return res.json();
        }).then(function (data) {
            showHud('Blanket muted "' + word + '" (' + (data.cuesAdded || 0) + ' cues added)', '⚡');
            fetchItemFilter(activeItemId).then(function (refreshed) {
                if (refreshed) activeFilter = refreshed;
                updateCuesBadge();
                loadAndRenderSubtitleWords(true);
            });
        }).catch(function (err) {
            if (statusMsg) {
                statusMsg.style.color = '#ef4444';
                statusMsg.textContent = 'Blanket filter failed: ' + err.message;
            }
        });
    }

    function removeWordFilter(word, removeFromGlobal) {
        var client = getApiClient();
        if (!client || !activeItemId) return;

        var modal = ensureEditorModal();
        var statusMsg = modal.querySelector('#cfWordsStatusMsg');
        if (statusMsg) {
            statusMsg.style.color = '#38bdf8';
            statusMsg.textContent = 'Removing filter for "' + word + '"...';
        }

        var url = client.getUrl('ContentFilter/subtitles/' + activeItemId + '/remove-word-filter');
        var token = client.accessToken ? client.accessToken() : '';

        fetch(url, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'X-Emby-Token': token
            },
            body: JSON.stringify({
                word: word,
                removeFromGlobal: removeFromGlobal
            })
        }).then(function (res) {
            if (!res.ok) throw new Error('Server returned ' + res.status);
            return res.json();
        }).then(function (data) {
            showHud('Removed filter for "' + word + '" (' + (data.cuesRemoved || 0) + ' cues removed)', '🗑️');
            fetchItemFilter(activeItemId).then(function (refreshed) {
                if (refreshed) activeFilter = refreshed;
                updateCuesBadge();
                loadAndRenderSubtitleWords(true);
            });
        }).catch(function (err) {
            if (statusMsg) {
                statusMsg.style.color = '#ef4444';
                statusMsg.textContent = 'Remove failed: ' + err.message;
            }
        });
    }

    function blanketFilterVisibleWords() {
        var modal = ensureEditorModal();
        var searchWordsInput = modal.querySelector('#cfInputSearchWords');
        var searchTerm = searchWordsInput ? searchWordsInput.value.trim().toLowerCase() : '';

        var groups = (subtitleWords && (subtitleWords.Words || subtitleWords.words)) || [];
        var targets = groups.filter(function (g) {
            var isF = (g.IsFiltered !== undefined) ? g.IsFiltered : g.isFiltered;
            if (isF) return false;
            if (!searchTerm) return true;
            var gw = (g.Word || g.word || '').toLowerCase();
            var gc = (g.Category || g.category || '').toLowerCase();
            return gw.indexOf(searchTerm) !== -1 || gc.indexOf(searchTerm) !== -1;
        });

        if (targets.length === 0) {
            alert('No un-filtered words match the current filter.');
            return;
        }

        var wordList = targets.map(function (g) { return g.Word || g.word; });
        if (!confirm('Blanket filter all ' + wordList.length + ' visible words in this media item?')) {
            return;
        }

        var client = getApiClient();
        if (!client || !activeItemId) return;

        var url = client.getUrl('ContentFilter/subtitles/' + activeItemId + '/blanket-filter');
        var token = client.accessToken ? client.accessToken() : '';

        fetch(url, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'X-Emby-Token': token
            },
            body: JSON.stringify({
                words: wordList,
                language: selectedSubtitleLanguage,
                action: 'mute',
                global: false
            })
        }).then(function (res) {
            if (!res.ok) throw new Error('Server returned ' + res.status);
            return res.json();
        }).then(function (data) {
            showHud('Blanket filtered ' + wordList.length + ' words (' + (data.cuesAdded || 0) + ' cues added)', '⚡');
            fetchItemFilter(activeItemId).then(function (refreshed) {
                if (refreshed) activeFilter = refreshed;
                updateCuesBadge();
                loadAndRenderSubtitleWords(true);
            });
        }).catch(function (err) {
            alert('Failed to blanket filter words: ' + err.message);
        });
    }

    function updateCuesBadge() {
        var count = (activeFilter && activeFilter.cues) ? activeFilter.cues.length : 0;
        var badge = document.querySelector('#cfTabCuesCount');
        if (badge) badge.textContent = count;
    }

    function startEditingCue(cue) {
        if (!cue) return;
        editingCueKey = cue.key;

        var modal = ensureEditorModal();
        var editBanner = modal.querySelector('#cfEditBanner');
        var editBannerText = modal.querySelector('#cfEditBannerText');
        var saveBtn = modal.querySelector('#cfBtnSaveCue');
        var startInput = modal.querySelector('#cfInputStart');
        var endInput = modal.querySelector('#cfInputEnd');
        var descInput = modal.querySelector('#cfInputDescription');
        var catSelect = modal.querySelector('#cfSelectCategory');
        var customCatInput = modal.querySelector('#cfInputCustomCategory');
        var channelSelect = modal.querySelector('#cfSelectChannel');
        var actionSelect = modal.querySelector('#cfSelectAction');
        var tabBtnAdd = modal.querySelector('#cfTabBtnAdd');

        editBanner.style.display = 'flex';
        editBannerText.textContent = '✏️ Editing Cue: ' + formatTime(cue.start) + ' - ' + formatTime(cue.end) + ' (' + cue.category + ')';
        tabBtnAdd.textContent = '✏️ Edit Cue';
        saveBtn.textContent = '💾 Update Cue';
        saveBtn.style.background = '#eab308';

        startInput.value = formatTimecode(cue.start);
        endInput.value = formatTimecode(cue.end);
        descInput.value = cue.description || '';

        // Match category
        var knownOption = Array.from(catSelect.options).some(function (opt) { return opt.value === cue.category; });
        if (knownOption) {
            catSelect.value = cue.category;
            customCatInput.style.display = 'none';
        } else {
            catSelect.value = '__custom__';
            customCatInput.style.display = 'block';
            customCatInput.value = cue.category;
        }

        channelSelect.value = cue.channel || 'video';
        actionSelect.value = cue.action || 'skip';

        // Switch to Add/Edit tab
        tabBtnAdd.click();
    }

    function renderActiveCuesList() {
        var container = document.querySelector('#cfCuesListContainer');
        if (!container) return;

        var cues = (activeFilter && activeFilter.cues) ? activeFilter.cues : [];
        if (cues.length === 0) {
            container.innerHTML = '<div style="text-align:center; color:#94a3b8; padding:24px;">No cues configured for this item yet.<br><span style="font-size:12px; color:#38bdf8;">Use the "Set Cue Point" tab to add your first cue!</span></div>';
            return;
        }

        container.innerHTML = cues.map(function (c, idx) {
            var dur = (c.end - c.start).toFixed(1);
            var catColor = c.category.indexOf('Violence') !== -1 ? '#f87171' :
                           c.category.indexOf('Sex') !== -1 ? '#c084fc' :
                           c.category.indexOf('Language') !== -1 ? '#fbbf24' :
                           c.category.indexOf('Substance') !== -1 ? '#34d399' :
                           c.category.indexOf('Medical') !== -1 ? '#f472b6' :
                           c.category.indexOf('Structural') !== -1 ? '#94a3b8' :
                           c.category.indexOf('Gore') !== -1 ? '#fb923c' : '#38bdf8';

            return [
                '<div style="background:rgba(30, 41, 59, 0.7); border:1px solid rgba(255,255,255,0.08); border-radius:10px; padding:10px 14px; display:flex; justify-content:space-between; align-items:center;">',
                '  <div>',
                '    <div style="display:flex; align-items:center; gap:8px;">',
                '      <span style="font-family:ui-monospace,monospace; font-weight:700; color:#f8fafc; font-size:13px;">' + formatTime(c.start) + ' - ' + formatTime(c.end) + '</span>',
                '      <span style="font-size:11px; color:#94a3b8;">(' + dur + 's)</span>',
                '      <span style="background:rgba(255,255,255,0.1); color:' + catColor + '; font-size:11px; font-weight:600; padding:2px 8px; border-radius:4px;">' + c.category + '</span>',
                '      <span style="background:rgba(16, 185, 129, 0.2); color:#10b981; font-size:11px; font-weight:600; padding:2px 6px; border-radius:4px;">' + c.action + '</span>',
                '    </div>',
                c.description ? ('    <div style="font-size:11px; color:#94a3b8; margin-top:3px;">' + c.description + '</div>') : '',
                '  </div>',
                '  <div style="display:flex; gap:6px; align-items:center;">',
                '    <button class="cf-cue-jump-btn" data-start="' + c.start + '" title="Jump to 2s before cue" style="background:#0284c7; border:none; color:#fff; padding:5px 10px; border-radius:6px; cursor:pointer; font-size:11px; font-weight:600;">▶ Jump (-2s)</button>',
                '    <button class="cf-cue-edit-btn" data-idx="' + idx + '" title="Edit Cue" style="background:rgba(234, 179, 8, 0.2); border:1px solid rgba(234, 179, 8, 0.4); color:#fef08a; padding:5px 8px; border-radius:6px; cursor:pointer; font-size:11px; font-weight:600;">✏️ Edit</button>',
                '    <button class="cf-cue-del-btn" data-key="' + encodeURIComponent(c.key) + '" title="Delete Cue" style="background:rgba(239, 68, 68, 0.2); border:1px solid rgba(239, 68, 68, 0.4); color:#ef4444; padding:5px 8px; border-radius:6px; cursor:pointer; font-size:12px;">🗑️</button>',
                '  </div>',
                '</div>'
            ].join('\n');
        }).join('\n');

        // Wire jump buttons
        container.querySelectorAll('.cf-cue-jump-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var start = parseFloat(btn.getAttribute('data-start')) || 0;
                if (activeVideo) {
                    activeVideo.currentTime = Math.max(0, start - 2.0);
                    showHud('Jumped to ' + formatTime(Math.max(0, start - 2.0)), '▶');
                }
            });
        });

        // Wire edit buttons
        container.querySelectorAll('.cf-cue-edit-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var idx = parseInt(btn.getAttribute('data-idx'), 10);
                if (!isNaN(idx) && activeFilter && activeFilter.cues && activeFilter.cues[idx]) {
                    startEditingCue(activeFilter.cues[idx]);
                }
            });
        });

        // Wire delete buttons
        container.querySelectorAll('.cf-cue-del-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var cueKey = decodeURIComponent(btn.getAttribute('data-key'));
                if (!confirm('Are you sure you want to delete this cue?')) return;

                var client = getApiClient();
                if (!client || !activeItemId) return;

                var url = client.getUrl('ContentFilter/filters/' + activeItemId + '/segments/' + encodeURIComponent(cueKey));
                var token = client.accessToken ? client.accessToken() : '';

                fetch(url, {
                    method: 'DELETE',
                    headers: { 'X-Emby-Token': token }
                }).then(function (res) {
                    if (res.ok) {
                        if (activeFilter && activeFilter.cues) {
                            activeFilter.cues = activeFilter.cues.filter(function (c) { return c.key !== cueKey; });
                        }
                        updateCuesBadge();
                        renderActiveCuesList();
                        showHud('Deleted cue', '🗑️');
                    } else {
                        alert('Failed to delete cue.');
                    }
                }).catch(function (e) {
                    alert('Error deleting cue: ' + e.message);
                });
            });
        });
    }

    function openEditorModal() {
        var modal = ensureEditorModal();
        modal.style.display = 'flex';

        var titleEl = modal.querySelector('#cfModalItemTitle');
        if (titleEl) {
            var t = (activeFilter && activeFilter.title) || '';
            if (!t && window.playbackManager && typeof window.playbackManager.currentItem === 'function') {
                var itm = window.playbackManager.currentItem();
                if (itm) t = (itm.SeriesName ? (itm.SeriesName + ' - ') : '') + (itm.Name || '');
            }
            titleEl.textContent = t || ('Item: ' + (activeItemId || 'Unknown'));
        }

        var startInput = modal.querySelector('#cfInputStart');
        var endInput = modal.querySelector('#cfInputEnd');
        if (activeVideo && !startInput.value && !editingCueKey) {
            startInput.value = formatTimecode(activeVideo.currentTime);
            endInput.value = formatTimecode(activeVideo.currentTime + 10);
        }

        updateCuesBadge();
        if (currentActiveTab === 'list') {
            renderActiveCuesList();
        }

        if (editorTimeInterval) clearInterval(editorTimeInterval);
        var timecodeEl = modal.querySelector('#cfLiveTimecode');
        editorTimeInterval = setInterval(function () {
            if (activeVideo && timecodeEl) {
                timecodeEl.textContent = formatTimecode(activeVideo.currentTime);
            }
        }, 80);
    }

    function closeEditorModal() {
        if (editorModal) {
            editorModal.style.display = 'none';
        }
        if (editorTimeInterval) {
            clearInterval(editorTimeInterval);
            editorTimeInterval = null;
        }
    }

    function toggleEditorModal() {
        var modal = ensureEditorModal();
        if (modal.style.display === 'none' || !modal.style.display) {
            openEditorModal();
        } else {
            closeEditorModal();
        }
    }

    function onFullscreenChange() {
        var target = getPlayerContainer();
        if (launchBtn && !target.contains(launchBtn)) {
            target.appendChild(launchBtn);
        }
        if (editorModal && !target.contains(editorModal)) {
            target.appendChild(editorModal);
        }
        if (hudElement && !target.contains(hudElement)) {
            target.appendChild(hudElement);
        }
    }

    document.addEventListener('fullscreenchange', onFullscreenChange);
    document.addEventListener('webkitfullscreenchange', onFullscreenChange);

    window.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') {
            if (editorModal && editorModal.style.display === 'flex') {
                closeEditorModal();
                e.stopPropagation();
            }
            return;
        }

        if (e.key === 'c' || e.key === 'C') {
            var tag = (document.activeElement && document.activeElement.tagName) || '';
            if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') {
                return;
            }
            if (activeVideo) {
                e.preventDefault();
                e.stopPropagation();
                toggleEditorModal();
            }
        }
    }, true);

    function attachToVideo(video, itemId) {
        if (!video || !itemId) return;

        if (activeVideo === video && activeItemId === itemId) {
            return;
        }

        detach();
        activeVideo = video;
        activeItemId = itemId;

        ensureLauncherButton();

        fetchItemFilter(itemId).then(function (filter) {
            activeFilter = filter || { itemId: itemId, title: '', cues: [] };
            updateTargetWords();
            loadGlobalBlanketWords();
            hookTextTracks(video);

            video.addEventListener('timeupdate', checkCues);
            video.addEventListener('seeked', checkCues);
            pollInterval = setInterval(checkCues, 150);

            if (activeFilter.cues && activeFilter.cues.length > 0) {
                showHud('Content Filter Active (' + activeFilter.cues.length + ' cues)', '🛡️');
            }
        });
    }

    function detach() {
        if (activeVideo) {
            try {
                activeVideo.removeEventListener('timeupdate', checkCues);
                activeVideo.removeEventListener('seeked', checkCues);
                if (isMutedByFilter) {
                    activeVideo.muted = false;
                }
            } catch (e) {}
        }
        if (pollInterval) {
            clearInterval(pollInterval);
            pollInterval = null;
        }
        closeEditorModal();
        removeLauncherButton();

        activeFilter = null;
        activeItemId = null;
        activeTargetWords.clear();
        subtitleTracks = null;
        subtitleWords = null;
        activeVideo = null;
        isMutedByFilter = false;
        lastSkippedCue = null;
    }

    function onPlaybackStart(e, state) {
        var video = document.querySelector('video');
        var itemId = resolveMediaItemId(video);
        if (state) {
            var item = state.Item || state.NowPlayingItem;
            if (item && item.Id) itemId = item.Id;
        }

        if (video && itemId) {
            attachToVideo(video, itemId);
        }
    }

    function onPlaybackStop() {
        detach();
    }

    function init() {
        if (window.Events) {
            window.Events.on(window.playbackManager || document, 'playbackstart', onPlaybackStart);
            window.Events.on(window.playbackManager || document, 'playbackstop', onPlaybackStop);
        }

        document.addEventListener('playbackstart', onPlaybackStart);
        document.addEventListener('playbackprogress', function (e) {
            var video = document.querySelector('video');
            if (video) {
                var itemId = (e.detail && e.detail.ItemId) || resolveMediaItemId(video);
                if (itemId && itemId !== activeItemId) attachToVideo(video, itemId);
            }
        });

        setInterval(function () {
            var video = document.querySelector('video');
            if (video) {
                var itemId = resolveMediaItemId(video);
                if (itemId && itemId !== activeItemId) {
                    attachToVideo(video, itemId);
                }
            } else if (!video && activeVideo) {
                detach();
            }
        }, 600);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
