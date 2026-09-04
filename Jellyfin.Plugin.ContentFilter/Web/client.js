(function () {
    'use strict';

    if (window._jellyfinContentFilterLoaded) {
        return;
    }
    window._jellyfinContentFilterLoaded = true;

    console.log('[ContentFilter] Client playback enforcement loaded.');

    var activeFilter = null;
    var activeVideo = null;
    var pollInterval = null;
    var isMutedByFilter = false;
    var lastSkippedCue = null;
    var hudElement = null;
    var hudTimeout = null;

    function parseTimecode(tc) {
        if (!tc) return 0;
        var parts = tc.trim().split(':');
        if (parts.length < 3) return 0;
        var h = parseFloat(parts[0]) || 0;
        var m = parseFloat(parts[1]) || 0;
        var s = parseFloat(parts[2].replace(',', '.')) || 0;
        return (h * 3600) + (m * 60) + s;
    }

    function formatTime(sec) {
        var h = Math.floor(sec / 3600);
        var m = Math.floor((sec % 3600) / 60);
        var s = Math.floor(sec % 60);
        if (h > 0) {
            return h + ':' + (m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
        }
        return m + ':' + (s < 10 ? '0' : '') + s;
    }

    function ensureHud() {
        if (hudElement && document.body.contains(hudElement)) {
            return hudElement;
        }
        hudElement = document.createElement('div');
        hudElement.id = 'contentFilterHud';
        hudElement.style.cssText = [
            'position: fixed',
            'top: 24px',
            'right: 24px',
            'z-index: 999999',
            'background: rgba(15, 23, 42, 0.92)',
            'color: #38bdf8',
            'border: 1px solid rgba(56, 189, 248, 0.4)',
            'box-shadow: 0 10px 25px -5px rgba(0, 0, 0, 0.6), 0 0 15px rgba(56, 189, 248, 0.2)',
            'backdrop-filter: blur(12px)',
            '-webkit-backdrop-filter: blur(12px)',
            'padding: 10px 18px',
            'border-radius: 12px',
            'font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif',
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

        document.body.appendChild(hudElement);
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
            if (!data || !data.cues || data.cues.length === 0) return null;

            var parsedCues = data.cues.map(function (c) {
                return {
                    start: parseTimecode(c.start),
                    end: parseTimecode(c.end),
                    action: (c.action || 'none').toLowerCase(),
                    channel: (c.channel || 'both').toLowerCase(),
                    category: c.category || '',
                    description: c.description || c.category || 'Filtered Content',
                    key: c.key || (c.start + '-' + c.end)
                };
            }).filter(function (c) {
                return c.action !== 'none' && c.end > c.start;
            });

            console.log('[ContentFilter] Loaded ' + parsedCues.length + ' active cue(s) for item ' + itemId);
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

    function checkCues() {
        if (!activeFilter || !activeVideo || activeVideo.paused) {
            return;
        }

        var cur = activeVideo.currentTime;
        var cues = activeFilter.cues;
        var shouldMute = false;
        var muteDescription = '';

        for (var i = 0; i < cues.length; i++) {
            var cue = cues[i];

            // 1. Skip check (video or both channels)
            if (cue.action === 'skip' && cue.channel !== 'audio') {
                if (cur >= (cue.start - 0.05) && cur < cue.end) {
                    if (lastSkippedCue !== cue.key) {
                        lastSkippedCue = cue.key;
                        console.log('[ContentFilter] Skipping cue:', cue.category, 'from', cur, 'to', cue.end);

                        activeVideo.currentTime = cue.end;

                        // Also inform Jellyfin playbackManager if possible
                        try {
                            if (window.playbackManager && typeof window.playbackManager.seek === 'function') {
                                window.playbackManager.seek(Math.round(cue.end * 10000000));
                            }
                        } catch (e) {
                            // Video seek is sufficient
                        }

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
                if (cur >= (cue.start - 0.05) && cur < cue.end) {
                    shouldMute = true;
                    muteDescription = cue.description || cue.category.split('.').pop() || 'Audio Filtered';
                }
            }
        }

        // Reset last skipped cue when safely past it
        if (lastSkippedCue) {
            var prevCue = cues.find(function (c) { return c.key === lastSkippedCue; });
            if (!prevCue || cur >= (prevCue.end + 1.0) || cur < (prevCue.start - 1.0)) {
                lastSkippedCue = null;
            }
        }

        // Apply muting
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

    function attachToVideo(video, itemId) {
        if (!video) return;

        if (activeVideo === video && activeFilter && activeFilter.itemId === itemId) {
            return;
        }

        detach();
        activeVideo = video;

        fetchItemFilter(itemId).then(function (filter) {
            if (!filter || filter.cues.length === 0) {
                return;
            }

            activeFilter = filter;
            video.addEventListener('timeupdate', checkCues);
            pollInterval = setInterval(checkCues, 150);

            showHud('Content Filter Active (' + filter.cues.length + ' cues)', '🛡️');
        });
    }

    function detach() {
        if (activeVideo) {
            try {
                activeVideo.removeEventListener('timeupdate', checkCues);
                if (isMutedByFilter) {
                    activeVideo.muted = false;
                }
            } catch (e) {
                // Ignore
            }
        }
        if (pollInterval) {
            clearInterval(pollInterval);
            pollInterval = null;
        }
        activeFilter = null;
        activeVideo = null;
        isMutedByFilter = false;
        lastSkippedCue = null;
    }

    function onPlaybackStart(e, state) {
        var item = state ? (state.Item || state.NowPlayingItem) : null;
        if (!item && window.playbackManager && typeof window.playbackManager.currentItem === 'function') {
            item = window.playbackManager.currentItem();
        }

        var itemId = item ? item.Id : null;
        if (!itemId) {
            // Check URL hash for itemId
            var hash = window.location.hash || '';
            var match = hash.match(/[?&]id=([a-f0-9]+)/i);
            if (match) {
                itemId = match[1];
            }
        }

        var video = document.querySelector('video');
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

        // Background polling fallback for player detection
        setInterval(function () {
            var video = document.querySelector('video');
            if (video && !video.paused && (!activeVideo || !activeFilter)) {
                var itemId = null;
                if (window.playbackManager && typeof window.playbackManager.currentItem === 'function') {
                    var curItem = window.playbackManager.currentItem();
                    if (curItem) itemId = curItem.Id;
                }
                if (!itemId) {
                    var hash = window.location.hash || '';
                    var match = hash.match(/[?&]id=([a-f0-9]+)/i);
                    if (match) itemId = match[1];
                }
                if (video && itemId) {
                    attachToVideo(video, itemId);
                }
            } else if (!video && activeVideo) {
                detach();
            }
        }, 1000);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
