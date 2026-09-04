using System.Collections.Concurrent;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Background hosted service for monitoring active playback sessions.
/// </summary>
public class PlaybackMonitor : IHostedService
{
    private readonly ILogger<PlaybackMonitor> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly FilterStore _filterStore;
    private readonly SubtitleFilter _subtitleFilter;
    private readonly ConcurrentDictionary<string, SessionState> _sessionState = new(StringComparer.Ordinal);
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackMonitor"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="sessionManager">The Jellyfin session manager.</param>
    /// <param name="filterStore">The filter store.</param>
    /// <param name="subtitleFilter">The subtitle filter service.</param>
    public PlaybackMonitor(
        ILogger<PlaybackMonitor> logger,
        ISessionManager sessionManager,
        FilterStore filterStore,
        SubtitleFilter subtitleFilter)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _filterStore = filterStore;
        _subtitleFilter = subtitleFilter;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            ClientScriptInjector.Initialize(_logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize ClientScriptInjector during PlaybackMonitor startup.");
        }

        _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitorTask = Task.Run(() => MonitorLoopAsync(_monitorCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_monitorCts is null || _monitorTask is null)
        {
            return;
        }

        _monitorCts.Cancel();
        await Task.WhenAny(_monitorTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    break;
                }

                var activeSessionIds = new HashSet<string>(StringComparer.Ordinal);
                var sessions = _sessionManager.Sessions.ToArray();
                foreach (var session in sessions)
                {
                    if (string.IsNullOrWhiteSpace(session.Id))
                    {
                        continue;
                    }

                    activeSessionIds.Add(session.Id);

                    if (session.NowPlayingItem is null || session.PlayState?.IsPaused == true)
                    {
                        continue;
                    }

                    try
                    {
                        await HandleSessionAsync(session, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error handling playback session {SessionId}.", session.Id);
                    }
                }

                foreach (var staleId in _sessionState.Keys.Where(id => !activeSessionIds.Contains(id)).ToArray())
                {
                    _sessionState.TryRemove(staleId, out _);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected exception in PlaybackMonitor loop.");
            }
        }
    }

    private async Task HandleSessionAsync(SessionInfo session, CancellationToken ct)
    {
        if (Plugin.Instance?.Configuration.IsEnabled != true)
        {
            return;
        }

        if (session.NowPlayingItem is null || string.IsNullOrWhiteSpace(session.Id))
        {
            return;
        }

        var itemId = session.NowPlayingItem.Id;
        var sessionId = session.Id;
        var state = _sessionState.GetOrAdd(sessionId, _ => new SessionState(itemId, false, 0, int.MinValue));
        var isNewItem = state.ItemId != itemId;
        if (isNewItem)
        {
            state = new SessionState(itemId, false, 0, int.MinValue);
            _sessionState[sessionId] = state;
        }

        if ((isNewItem || state.FilteredSubtitleIndex != -1) && _subtitleFilter.HasFilteredSubtitle(itemId))
        {
            await SendGeneralToSessionOrGroupAsync(session, GeneralCommandType.SetSubtitleStreamIndex, new Dictionary<string, string>
            {
                ["Index"] = "-1"
            }, ct).ConfigureAwait(false);

            state = state with { FilteredSubtitleIndex = -1 };
            _sessionState[sessionId] = state;
        }

        var filter = _filterStore.GetFilter(itemId);
        if (filter is null)
        {
            if (state.IsMuted)
            {
                await SendUnmuteToSessionOrGroupAsync(session, ct).ConfigureAwait(false);
                _sessionState[sessionId] = state with { IsMuted = false };
            }

            return;
        }

        var positionTicks = session.PlayState?.PositionTicks ?? 0;
        var position = positionTicks > 0 ? TimeSpan.FromTicks(positionTicks) : TimeSpan.Zero;
        var windowEnd = position + TimeSpan.FromSeconds(3);

        var activeCues = filter.Cues
            .Where(c => !string.Equals(c.Action, "none", StringComparison.OrdinalIgnoreCase))
            .Where(c => (c.Start <= windowEnd && c.End > position) ||
                        (position >= c.Start - TimeSpan.FromSeconds(1) && position < c.End))
            .ToArray();

        // Only video/both-channel skip cues trigger a seek; audio-channel skips are treated as mutes
        var seekCue = activeCues
            .Where(c => string.Equals(c.Action, "skip", StringComparison.OrdinalIgnoreCase))
            .Where(c => !string.Equals(c.Channel, "audio", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.End)
            .FirstOrDefault();

        if (seekCue is not null)
        {
            var seekTarget = seekCue.End.Ticks;
            if (state.LastSeekTarget != seekTarget)
            {
                _logger.LogInformation(
                    "ContentFilter: Session {SessionId} ({User}/{Device}) at {Position:mm\\:ss}: seeking past {Category} cue ({Channel}) to {End:mm\\:ss}",
                    sessionId, session.UserName, session.DeviceName, position, seekCue.Category, seekCue.Channel, seekCue.End);

                await SendPlaystateToSessionOrGroupAsync(session, new PlaystateRequest
                {
                    Command = PlaystateCommand.Seek,
                    SeekPositionTicks = seekTarget
                }, ct).ConfigureAwait(false);

                // If we were muted for a prior audio cue, clear it so the next poll unmutes.
                if (state.IsMuted)
                {
                    await SendUnmuteToSessionOrGroupAsync(session, ct).ConfigureAwait(false);
                    state = state with { IsMuted = false };
                }

                state = state with { LastSeekTarget = seekTarget };
                _sessionState[sessionId] = state;
            }

            return;
        }

        // When not inside any active seek cue, reset LastSeekTarget when playback moves safely past target or rewinds.
        if (state.LastSeekTarget != 0)
        {
            if (positionTicks >= state.LastSeekTarget || positionTicks < state.LastSeekTarget - TimeSpan.FromSeconds(30).Ticks)
            {
                state = state with { LastSeekTarget = 0 };
                _sessionState[sessionId] = state;
            }
        }

        // Mute when: explicit mute-action cue, OR audio-channel skip cue.
        var shouldMute = activeCues.Any(c =>
            string.Equals(c.Action, "mute", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(c.Action, "skip", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(c.Channel, "audio", StringComparison.OrdinalIgnoreCase)));

        if (shouldMute && !state.IsMuted)
        {
            _logger.LogInformation(
                "ContentFilter: Session {SessionId} ({User}/{Device}) at {Position:mm\\:ss}: muting for active cue(s)",
                sessionId, session.UserName, session.DeviceName, position);
            await SendMuteToSessionOrGroupAsync(session, ct).ConfigureAwait(false);
            _sessionState[sessionId] = state with { IsMuted = true };
        }
        else if (!shouldMute && state.IsMuted)
        {
            _logger.LogInformation(
                "ContentFilter: Session {SessionId} ({User}/{Device}) at {Position:mm\\:ss}: unmuting — no active mute cues",
                sessionId, session.UserName, session.DeviceName, position);
            await SendUnmuteToSessionOrGroupAsync(session, ct).ConfigureAwait(false);
            _sessionState[sessionId] = state with { IsMuted = false };
        }
    }

    private HashSet<string> GetTargetSessionIds(SessionInfo session)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(session.Id))
        {
            targets.Add(session.Id);
        }

        // If the playing session has no active controllers, check other sessions with same device ID or user ID
        if (session.SessionControllers.Count == 0)
        {
            foreach (var candidate in _sessionManager.Sessions)
            {
                if (candidate.SessionControllers.Count > 0 &&
                    ((!string.IsNullOrEmpty(session.DeviceId) && candidate.DeviceId == session.DeviceId) ||
                     (candidate.UserId != Guid.Empty && candidate.UserId == session.UserId)))
                {
                    targets.Add(candidate.Id);
                }
            }
        }

        return targets;
    }

    private async Task SendPlaystateToSessionOrGroupAsync(SessionInfo session, PlaystateRequest request, CancellationToken ct)
    {
        var targetSessionIds = GetTargetSessionIds(session);
        foreach (var targetId in targetSessionIds)
        {
            await SendPlaystateAsync(targetId, request, ct).ConfigureAwait(false);
        }
    }

    private async Task SendMuteToSessionOrGroupAsync(SessionInfo session, CancellationToken ct)
    {
        await SendGeneralToSessionOrGroupAsync(session, GeneralCommandType.Mute, null, ct).ConfigureAwait(false);
    }

    private async Task SendUnmuteToSessionOrGroupAsync(SessionInfo session, CancellationToken ct)
    {
        await SendGeneralToSessionOrGroupAsync(session, GeneralCommandType.Unmute, null, ct).ConfigureAwait(false);
    }

    private async Task SendGeneralToSessionOrGroupAsync(SessionInfo session, GeneralCommandType commandType, Dictionary<string, string>? arguments, CancellationToken ct)
    {
        var targetSessionIds = GetTargetSessionIds(session);
        foreach (var targetId in targetSessionIds)
        {
            await SendGeneralAsync(targetId, commandType, arguments, ct).ConfigureAwait(false);
        }
    }

    private async Task SendPlaystateAsync(string sessionId, PlaystateRequest request, CancellationToken ct)
    {
        try
        {
            await _sessionManager.SendPlaystateCommand(string.Empty, sessionId, request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send playstate command {Command} to session {SessionId}", request.Command, sessionId);
        }
    }

    private async Task SendGeneralAsync(string sessionId, GeneralCommandType commandType, Dictionary<string, string>? arguments, CancellationToken ct)
    {
        try
        {
            var command = new GeneralCommand
            {
                Name = commandType
            };

            if (arguments is not null)
            {
                foreach (var (key, value) in arguments)
                {
                    command.Arguments[key] = value;
                }
            }

            await _sessionManager.SendGeneralCommand(string.Empty, sessionId, command, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send general command {Command} to session {SessionId}", commandType, sessionId);
        }
    }

    private sealed record SessionState(Guid ItemId, bool IsMuted, long LastSeekTarget, int FilteredSubtitleIndex);
}
