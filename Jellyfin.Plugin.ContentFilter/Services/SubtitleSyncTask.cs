using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Scheduled background task that searches for missing subtitles in the default language,
/// cleans them, and sets them as the default subtitle track.
/// </summary>
public class SubtitleSyncTask : IScheduledTask
{
    /// <summary>
    /// The singleton subtitle sync service executing scan, download, and cleaning operations.
    /// </summary>
    private readonly SubtitleSyncService _syncService;

    /// <summary>
    /// Logger instance for scheduled task diagnostic events.
    /// </summary>
    private readonly ILogger<SubtitleSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleSyncTask"/> class.
    /// </summary>
    /// <param name="syncService">The subtitle sync service.</param>
    /// <param name="logger">The logger.</param>
    public SubtitleSyncTask(SubtitleSyncService syncService, ILogger<SubtitleSyncTask> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "Content Filter: Subtitle Auto-Download & Clean";

    /// <inheritdoc/>
    public string Key => "ContentFilterSubtitleSyncTask";

    /// <inheritdoc/>
    public string Description => "Searches and downloads subtitles in the default language for library items, generates cleaned versions with profanity masking, and sets them as default.";

    /// <inheritdoc/>
    public string Category => "Content Filter";

    /// <inheritdoc/>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting scheduled daily Content Filter subtitle sync task...");
        try
        {
            // Only process new/missing items during scheduled background run (forceAll = false)
            await _syncService.RunSyncAsync(forceAll: false, overrideLanguage: null, progress, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Completed scheduled Content Filter subtitle sync task.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scheduled Content Filter subtitle sync task was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled Content Filter subtitle sync task encountered an error.");
        }
    }

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var timeOfDay = ResolveScheduledTime();
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = timeOfDay.Ticks
            }
        ];
    }

    /// <summary>
    /// Resolves the configured time of day for the daily scheduled sync. Defaults to 3:00 AM server local time.
    /// </summary>
    /// <returns>The resolved <see cref="TimeSpan"/> time of day.</returns>
    public static TimeSpan ResolveScheduledTime()
    {
        var configTime = Plugin.Instance?.Configuration?.AutomatedSubtitleSyncTime;
        if (!string.IsNullOrWhiteSpace(configTime) && TimeSpan.TryParse(configTime, out var parsed))
        {
            return parsed;
        }

        return TimeSpan.FromHours(3);
    }
}
