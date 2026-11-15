using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Scheduled daily background task that extracts video frames via <c>ffmpeg</c> and scans
/// newly added or upgraded library items for nudity scenes using NudeNet.
/// </summary>
public class NudityScanTask : IScheduledTask
{
    /// <summary>
    /// The singleton nudity scan orchestration service.
    /// </summary>
    private readonly NudityScanService _scanService;

    /// <summary>
    /// Logger instance for scheduled task diagnostic events.
    /// </summary>
    private readonly ILogger<NudityScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NudityScanTask"/> class.
    /// </summary>
    /// <param name="scanService">The nudity scan service.</param>
    /// <param name="logger">The logger.</param>
    public NudityScanTask(NudityScanService scanService, ILogger<NudityScanTask> logger)
    {
        _scanService = scanService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "Content Filter: NudeNet Visual Nudity Scan";

    /// <inheritdoc/>
    public string Key => "ContentFilterNudityScanTask";

    /// <inheritdoc/>
    public string Description => "Extracts video frames with ffmpeg and scans new or upgraded library items for nudity scenes via NudeNet.";

    /// <inheritdoc/>
    public string Category => "Content Filter";

    /// <inheritdoc/>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableNudityScan != true)
        {
            _logger.LogDebug("Skipping scheduled NudeNet visual nudity scan because EnableNudityScan is disabled.");
            return;
        }

        _logger.LogInformation("Starting scheduled daily Content Filter NudeNet visual nudity scan...");
        try
        {
            await _scanService.RunScanAsync(forceAll: false, progress, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Completed scheduled Content Filter NudeNet visual nudity scan.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scheduled Content Filter NudeNet visual nudity scan was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled Content Filter NudeNet visual nudity scan encountered an error.");
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
    /// Resolves the configured time of day for the daily scheduled NudeNet scan. Defaults to 4:00 AM server local time.
    /// </summary>
    /// <returns>The resolved <see cref="TimeSpan"/> time of day.</returns>
    public static TimeSpan ResolveScheduledTime()
    {
        var configTime = Plugin.Instance?.Configuration?.AutomatedNudityScanTime;
        if (!string.IsNullOrWhiteSpace(configTime) && TimeSpan.TryParse(configTime, out var parsed))
        {
            return parsed;
        }

        return TimeSpan.FromHours(4);
    }
}
