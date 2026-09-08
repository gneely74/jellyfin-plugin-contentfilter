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
    /// <summary>
    /// The logger instance.
    /// </summary>
    private readonly ILogger<PlaybackMonitor> _logger;

    /// <summary>
    /// The Jellyfin session manager used to monitor and control playback sessions.
    /// </summary>
    private readonly ISessionManager _sessionManager;

    /// <summary>
    /// The filter store providing access to cached and stored filter profiles.
    /// </summary>
    private readonly FilterStore _filterStore;

    /// <summary>
    /// The subtitle filtering service.
    /// </summary>
    private readonly SubtitleFilter _subtitleFilter;

    /// <summary>
    /// The filter rule evaluation service.
    /// </summary>
    private readonly FilterRuleService _filterRuleService;

    /// <summary>
    /// Concurrently maps session IDs to ephemeral playback state.
    /// </summary>
    private readonly ConcurrentDictionary<string, SessionState> _sessionState = new(StringComparer.Ordinal);

    /// <summary>
    /// Cancellation token source for the background monitoring loop.
    /// </summary>
    private CancellationTokenSource? _monitorCts;

    /// <summary>
    /// Background task running the monitoring loop.
    /// </summary>
    private Task? _monitorTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackMonitor"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="sessionManager">The Jellyfin session manager.</param>
    /// <param name="filterStore">The filter store.</param>
    /// <param name="subtitleFilter">The subtitle filter service.</param>
    /// <param name="filterRuleService">The filter rule service.</param>
    public PlaybackMonitor(
        ILogger<PlaybackMonitor> logger,
        ISessionManager sessionManager,
        FilterStore filterStore,
        SubtitleFilter subtitleFilter,
        FilterRuleService filterRuleService)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _filterStore = filterStore;
        _subtitleFilter = subtitleFilter;
        _filterRuleService = filterRuleService;
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

    /// <summary>
    /// Continuously polls active sessions at a high frequency to evaluate filter cues and enforce muting or skipping.
    /// </summary>
    /// <param name="ct">A cancellation token to observe while waiting for the next tick.</param>
    /// <returns>A task representing the background loop operation.</returns>
    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
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

    /// <summary>
    /// Evaluates an individual active playback session against stored filter cues, dispatching seek or mute commands as appropriate.
    /// </summary>
    /// <param name="session">The active playback session information.</param>
    /// <param name="ct">A cancellation token for the session handling operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
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
        var state = _sessionState.GetOrAdd(sessionId, _ => new SessionState(itemId, false, 0, DateTime.MinValue, 0, int.MinValue, 0, DateTime.MinValue, DateTime.MinValue));
        var isNewItem = state.ItemId != itemId;
        if (isNewItem)
        {
            state = new SessionState(itemId, false, 0, DateTime.MinValue, 0, int.MinValue, 0, DateTime.MinValue, DateTime.MinValue);
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

        var rawTicks = session.PlayState?.PositionTicks ?? 0;
        var now = DateTime.UtcNow;

        if (state.LastReportedTicks != rawTicks || state.LastReportedUtc == DateTime.MinValue)
        {
            state = state with { LastReportedTicks = rawTicks, LastReportedUtc = now };
            _sessionState[sessionId] = state;
        }

        // Extrapolate current playback position to overcome client 1-second reporting latency
        TimeSpan position;
        if (session.PlayState?.IsPaused == true || state.LastReportedTicks <= 0)
        {
            position = state.LastReportedTicks > 0 ? TimeSpan.FromTicks(state.LastReportedTicks) : TimeSpan.Zero;
        }
        else
        {
            var elapsed = now - state.LastReportedUtc;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }
            else if (elapsed > TimeSpan.FromSeconds(3.0))
            {
                elapsed = TimeSpan.FromSeconds(3.0);
            }

            var extrapolatedTicks = state.LastReportedTicks + (long)(elapsed.TotalSeconds * 10_000_000);
            position = TimeSpan.FromTicks(extrapolatedTicks);
        }

        var config = Plugin.Instance?.Configuration;
        var fallbackToSkip = config?.FallbackToSkipOnUnmutableClients ?? true;
        var isAutonomousClient = IsClientAutonomousFilterClient(session);
        var canMute = CanSessionMute(session);

        // Video/both-channel skip cues trigger a seek with 3.5s lookahead to give players time to jump
        var seekWindowEnd = position + TimeSpan.FromSeconds(3.5);
        var seekCue = filter.Cues
            .Where(c => !string.Equals(c.Action, "none", StringComparison.OrdinalIgnoreCase))
            .Where(c => _filterRuleService.IsCueEnabled(c, itemId))
            .Where(c => (c.Start <= seekWindowEnd && c.End > position) ||
                        (position >= c.Start - TimeSpan.FromSeconds(1) && position < c.End))
            .Where(c =>
                (string.Equals(c.Action, "skip", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(c.Channel, "audio", StringComparison.OrdinalIgnoreCase)) ||
                (!isAutonomousClient && !canMute && fallbackToSkip &&
                 (string.Equals(c.Action, "mute", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(c.Action, "skip", StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(c => c.End)
            .FirstOrDefault();

        if (seekCue is not null)
        {
            var seekTarget = seekCue.End.Ticks;
            var isNewTarget = state.LastSeekTarget != seekTarget;

            // If we already sent a seek to this target and the player reported position near or past target, skip
            if (!isNewTarget && position.Ticks >= seekTarget - TimeSpan.FromMilliseconds(500).Ticks)
            {
                return;
            }

            var shouldRetry = !isNewTarget &&
                              (now - state.LastSeekTime) >= TimeSpan.FromSeconds(7.0) &&
                              state.SeekRetryCount < 3 &&
                              position.Ticks < seekTarget - TimeSpan.FromMilliseconds(500).Ticks;

            if (isNewTarget || shouldRetry)
            {
                var attempt = isNewTarget ? 1 : state.SeekRetryCount + 1;
                var reason = !canMute && fallbackToSkip && (seekCue.Channel.Equals("audio", StringComparison.OrdinalIgnoreCase) || seekCue.Action.Equals("mute", StringComparison.OrdinalIgnoreCase))
                    ? " [client cannot mute, fallback to seek]"
                    : string.Empty;
                _logger.LogInformation(
                    "ContentFilter: Session {SessionId} ({User}/{Device}) at {Position:mm\\:ss}: seeking past {Category} cue ({Channel}) to {End:mm\\:ss}{Reason} (attempt {Attempt})",
                    sessionId, session.UserName, session.DeviceName, position, seekCue.Category, seekCue.Channel, seekCue.End, reason, attempt);

                var sent = await SendPlaystateToSessionOrGroupAsync(session, new PlaystateRequest
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

                if (sent)
                {
                    state = state with
                    {
                        LastSeekTarget = seekTarget,
                        LastSeekTime = now,
                        SeekRetryCount = attempt
                    };
                }
                else
                {
                    _logger.LogWarning(
                        "ContentFilter: Session {SessionId} ({User}/{Device}) has no active controllers to receive seek command; will retry when connected",
                        sessionId, session.UserName, session.DeviceName);

                    state = state with
                    {
                        LastSeekTarget = seekTarget,
                        LastSeekTime = now - TimeSpan.FromSeconds(5.0),
                        SeekRetryCount = 0
                    };
                }

                _sessionState[sessionId] = state;
            }

            return;
        }

        // When not inside any active seek cue, reset LastSeekTarget when playback moves safely past target or rewinds.
        if (state.LastSeekTarget != 0)
        {
            if (position.Ticks >= state.LastSeekTarget - TimeSpan.FromMilliseconds(500).Ticks ||
                position.Ticks < state.LastSeekTarget - TimeSpan.FromSeconds(30).Ticks)
            {
                state = state with { LastSeekTarget = 0, LastSeekTime = DateTime.MinValue, SeekRetryCount = 0 };
                _sessionState[sessionId] = state;
            }
        }

        var enableRemoteMute = (config?.EnableRemoteWebSocketMuting ?? false) && !isAutonomousClient;

        if (enableRemoteMute)
        {
            // Mute timing:
            // 1. muteLeadTime: Pre-roll lead window before word onset to overcome player audio buffering, network transmission, and DAC ramp-down (default 1800ms).
            // 2. muteLagTime: Post-roll trailing window after word completion to cover trailing consonants and room acoustics (default 300ms).
            // 3. minMuteDuration: Stable floor to prevent AVR/soundbar eARC dropouts or fluttering on short words (1000ms).
            var muteLeadTime = TimeSpan.FromMilliseconds(config?.RemoteMuteLeadMs ?? 1800);
            var muteLagTime = TimeSpan.FromMilliseconds(config?.RemoteMuteLagMs ?? 300);
            var minMuteDuration = TimeSpan.FromMilliseconds(1000);

            var isInsideMuteCue = filter.Cues
                .Where(c => !string.Equals(c.Action, "none", StringComparison.OrdinalIgnoreCase))
                .Where(c => _filterRuleService.IsCueEnabled(c, itemId))
                .Where(c =>
                    string.Equals(c.Action, "mute", StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(c.Action, "skip", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(c.Channel, "audio", StringComparison.OrdinalIgnoreCase)))
                .Any(c => position >= (c.Start - muteLeadTime) && position < (c.End + muteLagTime));

            var shouldMute = canMute && (isInsideMuteCue || (state.IsMuted && (now - state.LastMuteTimeUtc) < minMuteDuration));

            if (shouldMute && !state.IsMuted)
            {
                _logger.LogInformation(
                    "ContentFilter: Session {SessionId} ({User}/{Device}) at {Position:mm\\:ss\\.ff}: muting for active cue(s)",
                    sessionId, session.UserName, session.DeviceName, position);
                await SendMuteToSessionOrGroupAsync(session, ct).ConfigureAwait(false);
                _sessionState[sessionId] = state with { IsMuted = true, LastMuteTimeUtc = now };
            }
            else if (!shouldMute && state.IsMuted)
            {
                _logger.LogInformation(
                    "ContentFilter: Session {SessionId} ({User}/{Device}) at {Position:mm\\:ss\\.ff}: unmuting — cue ended (muted for {Duration:0.00}s)",
                    sessionId, session.UserName, session.DeviceName, position, (now - state.LastMuteTimeUtc).TotalSeconds);
                await SendUnmuteToSessionOrGroupAsync(session, ct).ConfigureAwait(false);
                _sessionState[sessionId] = state with { IsMuted = false };
            }
        }
        else if (state.IsMuted)
        {
            _logger.LogInformation(
                "ContentFilter: Session {SessionId} ({User}/{Device}): clearing remote muted state (autonomous client or remote mute disabled)",
                sessionId, session.UserName, session.DeviceName);
            await SendUnmuteToSessionOrGroupAsync(session, ct).ConfigureAwait(false);
            _sessionState[sessionId] = state with { IsMuted = false };
        }
    }

    /// <summary>
    /// Determines whether a playback client handles content filtering autonomously (e.g., Swiftfin or Jellyfin Web).
    /// </summary>
    /// <param name="session">The playback session information.</param>
    /// <returns><c>true</c> if the client is self-filtering; otherwise, <c>false</c>.</returns>
    private static bool IsClientAutonomousFilterClient(SessionInfo session)
    {
        var client = session.Client ?? string.Empty;
        var deviceName = session.DeviceName ?? string.Empty;
        return client.Contains("Swiftfin", StringComparison.OrdinalIgnoreCase) ||
               client.Contains("Jellyfin Web", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("AppleTV", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Evaluates whether a session's declared client capabilities support remote volume muting commands.
    /// </summary>
    /// <param name="session">The playback session information.</param>
    /// <returns><c>true</c> if mute commands are supported or unconstrained; otherwise, <c>false</c>.</returns>
    private static bool CanSessionMute(SessionInfo session)
    {
        // If client capabilities declare supported commands, verify Mute is supported
        if (session.Capabilities?.SupportedCommands != null &&
            session.Capabilities.SupportedCommands.Count > 0 &&
            !session.Capabilities.SupportedCommands.Contains(GeneralCommandType.Mute))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves all candidate session identifiers that should receive playback control commands for a given session.
    /// </summary>
    /// <param name="session">The primary playback session.</param>
    /// <returns>A set of session IDs including the primary session and any linked companion or controlling sessions.</returns>
    private HashSet<string> GetTargetSessionIds(SessionInfo session)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(session.Id))
        {
            targets.Add(session.Id);
        }

        if (session.SessionControllers.Count > 0)
        {
            return targets;
        }

        // If the playing session has no active controllers, check other sessions with same device ID
        if (!string.IsNullOrEmpty(session.DeviceId))
        {
            foreach (var candidate in _sessionManager.Sessions)
            {
                if (candidate.SessionControllers.Count > 0 &&
                    string.Equals(candidate.DeviceId, session.DeviceId, StringComparison.Ordinal))
                {
                    targets.Add(candidate.Id);
                }
            }

            if (targets.Count > 0)
            {
                return targets;
            }
        }

        // Fallback: check other sessions for the same user
        if (session.UserId != Guid.Empty)
        {
            foreach (var candidate in _sessionManager.Sessions)
            {
                if (candidate.SessionControllers.Count > 0 && candidate.UserId == session.UserId)
                {
                    targets.Add(candidate.Id);
                }
            }
        }

        return targets;
    }

    /// <summary>
    /// Dispatches a playstate command (such as seek) to a session and any associated controlling sessions.
    /// </summary>
    /// <param name="session">The primary playback session.</param>
    /// <param name="request">The playstate command request.</param>
    /// <param name="ct">A cancellation token for the dispatch operation.</param>
    /// <returns><c>true</c> if the command was sent successfully to at least one target session; otherwise, <c>false</c>.</returns>
    private async Task<bool> SendPlaystateToSessionOrGroupAsync(SessionInfo session, PlaystateRequest request, CancellationToken ct)
    {
        var targetSessionIds = GetTargetSessionIds(session);
        var anySent = false;
        foreach (var targetId in targetSessionIds)
        {
            var sent = await SendPlaystateAsync(targetId, request, ct).ConfigureAwait(false);
            if (sent)
            {
                anySent = true;
            }
        }

        return anySent;
    }

    /// <summary>
    /// Dispatches a mute command to a session and any associated controller sessions.
    /// </summary>
    /// <param name="session">The target playback session.</param>
    /// <param name="ct">A cancellation token for the command dispatch.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task SendMuteToSessionOrGroupAsync(SessionInfo session, CancellationToken ct)
    {
        await SendGeneralToSessionOrGroupAsync(session, GeneralCommandType.Mute, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatches an unmute command to a session and any associated controller sessions.
    /// </summary>
    /// <param name="session">The target playback session.</param>
    /// <param name="ct">A cancellation token for the command dispatch.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task SendUnmuteToSessionOrGroupAsync(SessionInfo session, CancellationToken ct)
    {
        await SendGeneralToSessionOrGroupAsync(session, GeneralCommandType.Unmute, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatches a general command with optional arguments across all target session IDs for a session.
    /// </summary>
    /// <param name="session">The primary playback session.</param>
    /// <param name="commandType">The general command type to send.</param>
    /// <param name="arguments">Optional key-value arguments for the command.</param>
    /// <param name="ct">A cancellation token for the command dispatch.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task SendGeneralToSessionOrGroupAsync(SessionInfo session, GeneralCommandType commandType, Dictionary<string, string>? arguments, CancellationToken ct)
    {
        var targetSessionIds = GetTargetSessionIds(session);
        foreach (var targetId in targetSessionIds)
        {
            await SendGeneralAsync(targetId, commandType, arguments, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Dispatches a playstate command directly to a specific session ID using the Jellyfin session manager.
    /// </summary>
    /// <param name="sessionId">The target session ID.</param>
    /// <param name="request">The playstate command payload.</param>
    /// <param name="ct">A cancellation token for the command dispatch.</param>
    /// <returns><c>true</c> if the command was successfully dispatched; otherwise, <c>false</c>.</returns>
    private async Task<bool> SendPlaystateAsync(string sessionId, PlaystateRequest request, CancellationToken ct)
    {
        try
        {
            var session = _sessionManager.Sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
            if (session is null)
            {
                return false;
            }

            await _sessionManager.SendPlaystateCommand(string.Empty, sessionId, request, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send playstate command {Command} to session {SessionId}", request.Command, sessionId);
            return false;
        }
    }

    /// <summary>
    /// Dispatches a general command directly to a specific session ID using the Jellyfin session manager.
    /// </summary>
    /// <param name="sessionId">The target session ID.</param>
    /// <param name="commandType">The general command type.</param>
    /// <param name="arguments">Optional key-value arguments for the command.</param>
    /// <param name="ct">A cancellation token for the command dispatch.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
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

    /// <summary>
    /// Ephemeral state tracked for an active playback session during monitoring.
    /// </summary>
    /// <param name="ItemId">The unique identifier of the media item currently playing.</param>
    /// <param name="IsMuted">Indicates whether the session is currently muted by ContentFilter.</param>
    /// <param name="LastSeekTarget">The position ticks of the most recent seek command sent to the session.</param>
    /// <param name="LastSeekTime">The timestamp when the most recent seek command was issued.</param>
    /// <param name="SeekRetryCount">The count of retry attempts for the current seek target.</param>
    /// <param name="FilteredSubtitleIndex">The track index of the filtered subtitle stream, or -1 if default.</param>
    /// <param name="LastReportedTicks">The media position ticks last reported by the client.</param>
    /// <param name="LastReportedUtc">The UTC timestamp when position ticks were last reported.</param>
    /// <param name="LastMuteTimeUtc">The UTC timestamp when the session was last muted.</param>
    private sealed record SessionState(
        Guid ItemId,
        bool IsMuted,
        long LastSeekTarget,
        DateTime LastSeekTime,
        int SeekRetryCount,
        int FilteredSubtitleIndex,
        long LastReportedTicks,
        DateTime LastReportedUtc,
        DateTime LastMuteTimeUtc);
}
