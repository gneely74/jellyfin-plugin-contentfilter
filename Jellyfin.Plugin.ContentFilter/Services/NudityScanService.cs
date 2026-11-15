using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ContentFilter.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Orchestrates library-wide and single-item NudeNet visual nudity scans,
/// skipping already-scanned media unless upgraded or forced, and persisting cues to <see cref="FilterStore"/>.
/// </summary>
public class NudityScanService
{
    /// <summary>
    /// Maximum number of recent log lines retained in <see cref="NudityScanStatus.RecentLogs"/>.
    /// </summary>
    private const int MaxLogEntries = 200;

    /// <summary>
    /// Logger instance for nudity scan orchestration.
    /// </summary>
    private readonly ILogger<NudityScanService> _logger;

    /// <summary>
    /// Jellyfin library manager for enumerating movies and episodes.
    /// </summary>
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Frame extraction and NudeNet HTTP inference service.
    /// </summary>
    private readonly NudityDetectionService _nudityDetectionService;

    /// <summary>
    /// Filter store for persisting generated nudity cues.
    /// </summary>
    private readonly FilterStore _filterStore;

    /// <summary>
    /// SQLite repository for tracking per-item NudeNet scan history.
    /// </summary>
    private readonly SqliteFilterRepository _repository;

    /// <summary>
    /// Whisper transcription service used for shared GPU VRAM arbitration locks.
    /// </summary>
    private readonly WhisperTranscriptionService _whisperService;

    /// <summary>
    /// Synchronization lock guarding scan startup, cancellation, and status updates.
    /// </summary>
    private readonly object _scanLock = new();

    /// <summary>
    /// Cancellation token source for the currently active scan run.
    /// </summary>
    private CancellationTokenSource? _activeScanCts;

    /// <summary>
    /// Initializes a new instance of the <see cref="NudityScanService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="nudityDetectionService">The NudeNet detection service.</param>
    /// <param name="filterStore">The filter store.</param>
    /// <param name="repository">The SQLite filter repository.</param>
    /// <param name="whisperService">The Whisper service (for GPU VRAM arbitration).</param>
    public NudityScanService(
        ILogger<NudityScanService> logger,
        ILibraryManager libraryManager,
        NudityDetectionService nudityDetectionService,
        FilterStore filterStore,
        SqliteFilterRepository repository,
        WhisperTranscriptionService whisperService)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _nudityDetectionService = nudityDetectionService;
        _filterStore = filterStore;
        _repository = repository;
        _whisperService = whisperService;
        RefreshNextScheduledRun();
    }

    /// <summary>
    /// Gets the live status of the library-wide NudeNet nudity scan.
    /// </summary>
    public NudityScanStatus Status { get; } = new();

    /// <summary>
    /// Starts a background library-wide NudeNet nudity scan if one is not already running.
    /// </summary>
    /// <param name="forceAll">When <see langword="true"/>, rescans all items even if previously scanned.</param>
    /// <returns><see langword="true"/> if the scan started; <see langword="false"/> if a scan is already running.</returns>
    public bool StartScan(bool forceAll = false)
    {
        lock (_scanLock)
        {
            if (Status.IsRunning)
            {
                return false;
            }

            _ = Task.Run(() => RunScanAsync(forceAll, progress: null, CancellationToken.None));
            return true;
        }
    }

    /// <summary>
    /// Cancels any currently running library-wide NudeNet scan.
    /// </summary>
    public void CancelScan()
    {
        lock (_scanLock)
        {
            if (Status.IsRunning)
            {
                _activeScanCts?.Cancel();
            }
        }
    }

    /// <summary>
    /// Recomputes <see cref="NudityScanStatus.NextScheduledRun"/> in UTC from configuration.
    /// </summary>
    public void RefreshNextScheduledRun()
    {
        var timeOfDay = NudityScanTask.ResolveScheduledTime();
        var nowLocal = DateTime.Now;
        var nextLocal = nowLocal.Date.Add(timeOfDay);
        if (nextLocal <= nowLocal)
        {
            nextLocal = nextLocal.AddDays(1);
        }

        Status.NextScheduledRun = nextLocal.ToUniversalTime();
    }

    /// <summary>
    /// Executes the library-wide NudeNet visual nudity scan synchronously until completion or cancellation.
    /// </summary>
    /// <param name="forceAll">Whether to rescan items that already have scan history.</param>
    /// <param name="progress">Optional progress reporter (0.0 to 100.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RunScanAsync(
        bool forceAll,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        lock (_scanLock)
        {
            if (Status.IsRunning)
            {
                throw new InvalidOperationException("A NudeNet nudity scan is already in progress.");
            }

            _activeScanCts?.Dispose();
            _activeScanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Status.State = NudityScanState.Running;
            Status.StartedAt = DateTime.UtcNow;
            Status.CompletedAt = null;
            Status.TotalItems = 0;
            Status.ProcessedItems = 0;
            Status.ItemsScanned = 0;
            Status.ItemsSkipped = 0;
            Status.CuesGenerated = 0;
            Status.ErrorCount = 0;
            Status.ProgressPercentage = 0;
            Status.LastErrorMessage = null;
            Status.CurrentItemName = "Querying library items...";
            AddLog($"Started NudeNet visual nudity scan (forceAll={forceAll}).");
        }

        var token = _activeScanCts.Token;
        bool gpuLocked = false;

        try
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                IsVirtualItem = false,
                Recursive = true
            };

            var items = _libraryManager.GetItemList(query)
                .OfType<Video>()
                .Where(static v => !string.IsNullOrWhiteSpace(v.Path))
                .ToList();

            Status.TotalItems = items.Count;
            AddLog($"Found {items.Count} video items in library.");

            if (items.Count > 0)
            {
                gpuLocked = await _whisperService.AcquireBatchGpuLockAsync(token).ConfigureAwait(false);
            }

            var config = Plugin.Instance?.Configuration;
            var autoRegenerate = config?.AutoRegenerateNudityOnMediaUpgrade ?? true;

            foreach (var video in items)
            {
                token.ThrowIfCancellationRequested();
                Status.CurrentItemName = video.Name ?? Path.GetFileName(video.Path) ?? video.Id.ToString("N");

                try
                {
                    if (!File.Exists(video.Path))
                    {
                        Status.ItemsSkipped++;
                        continue;
                    }

                    var fileInfo = new FileInfo(video.Path);
                    var history = _repository.GetNudityScanHistory(video.Id);
                    var upgraded = autoRegenerate && _repository.HasMediaChangedSinceNudityScan(
                        video.Id,
                        video.Path,
                        fileInfo.Length,
                        fileInfo.LastWriteTimeUtc);

                    if (!forceAll && history is not null && !upgraded)
                    {
                        Status.ItemsSkipped++;
                        continue;
                    }

                    var cuesCreated = await ScanVideoInternalAsync(video, fileInfo, token).ConfigureAwait(false);
                    Status.ItemsScanned++;
                    Status.CuesGenerated += cuesCreated;
                    AddLog($"[NudeNet] \"{Status.CurrentItemName}\": {cuesCreated} nudity cue(s) saved.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Status.ErrorCount++;
                    Status.LastErrorMessage = ex.Message;
                    AddLog($"[Error] \"{Status.CurrentItemName}\": {ex.Message}");
                    _logger.LogWarning(ex, "NudeNet scan failed for item {ItemId} ({Path})", video.Id, video.Path);
                }
                finally
                {
                    Status.ProcessedItems++;
                    var pct = Status.TotalItems > 0
                        ? (double)Status.ProcessedItems / Status.TotalItems * 100.0
                        : 100.0;
                    Status.ProgressPercentage = Math.Round(pct, 1);
                    progress?.Report(Status.ProgressPercentage);
                }
            }

            Status.State = NudityScanState.Complete;
            Status.CompletedAt = DateTime.UtcNow;
            Status.CurrentItemName = "Complete";
            Status.ProgressPercentage = 100.0;
            progress?.Report(100.0);
            AddLog($"NudeNet scan complete. Scanned: {Status.ItemsScanned}, Skipped: {Status.ItemsSkipped}, Cues: {Status.CuesGenerated}, Errors: {Status.ErrorCount}.");
        }
        catch (OperationCanceledException)
        {
            Status.State = NudityScanState.Cancelled;
            Status.CompletedAt = DateTime.UtcNow;
            Status.CurrentItemName = "Cancelled";
            AddLog("NudeNet scan was cancelled.");
            _logger.LogInformation("NudeNet visual nudity scan was cancelled.");
        }
        catch (Exception ex)
        {
            Status.State = NudityScanState.Error;
            Status.CompletedAt = DateTime.UtcNow;
            Status.LastErrorMessage = ex.Message;
            AddLog($"NudeNet scan failed: {ex.Message}");
            _logger.LogError(ex, "NudeNet visual nudity scan encountered a fatal error.");
            throw;
        }
        finally
        {
            if (gpuLocked)
            {
                _whisperService.ReleaseBatchGpuLock();
            }

            RefreshNextScheduledRun();
        }
    }

    /// <summary>
    /// Scans a single library video item on demand via <c>ffmpeg</c> + NudeNet and persists detected cues.
    /// </summary>
    /// <param name="itemId">The Jellyfin item GUID.</param>
    /// <param name="force">When <see langword="true"/>, rescans even if scan history already exists.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="SingleNudityScanResult"/> describing the outcome.</returns>
    public async Task<SingleNudityScanResult> ScanSingleVideoAsync(
        Guid itemId,
        bool force = true,
        CancellationToken cancellationToken = default)
    {
        if (_libraryManager.GetItemById(itemId) is not Video video || string.IsNullOrWhiteSpace(video.Path) || !File.Exists(video.Path))
        {
            return new SingleNudityScanResult
            {
                ItemId = itemId,
                Success = false,
                Message = "Media item not found or video file does not exist on disk."
            };
        }

        var fileInfo = new FileInfo(video.Path);
        if (!force && _repository.GetNudityScanHistory(itemId) is not null)
        {
            return new SingleNudityScanResult
            {
                ItemId = itemId,
                Success = true,
                CuesCreated = 0,
                Message = "Item already scanned (pass force=true to rescan)."
            };
        }

        var gpuLocked = await _whisperService.AcquireBatchGpuLockAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var count = await ScanVideoInternalAsync(video, fileInfo, cancellationToken).ConfigureAwait(false);
            return new SingleNudityScanResult
            {
                ItemId = itemId,
                Success = true,
                CuesCreated = count,
                Message = $"NudeNet scan complete: {count} cue(s) saved."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Single-item NudeNet scan failed for {ItemId}", itemId);
            return new SingleNudityScanResult
            {
                ItemId = itemId,
                Success = false,
                Message = ex.Message
            };
        }
        finally
        {
            if (gpuLocked)
            {
                _whisperService.ReleaseBatchGpuLock();
            }
        }
    }

    /// <summary>
    /// Runs NudeNet detection on <paramref name="video"/>, replaces prior auto-generated NudeNet cues,
    /// saves any newly detected cues, and updates <c>item_nudity_scan_history</c>.
    /// </summary>
    /// <param name="video">The video item.</param>
    /// <param name="fileInfo">Physical file metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of cues saved.</returns>
    private async Task<int> ScanVideoInternalAsync(Video video, FileInfo fileInfo, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var fps = config?.NudityScanFps is > 0 ? config.NudityScanFps : 0.5;
        var threshold = config?.NudityConfidenceThreshold is > 0 and <= 1.0 ? config.NudityConfidenceThreshold : 0.60;

        var cues = await _nudityDetectionService.DetectNudityCuesAsync(video, cancellationToken).ConfigureAwait(false);

        await _filterStore.RemoveAutoNudityCuesAsync(video.Id, cancellationToken).ConfigureAwait(false);

        if (cues.Count > 0)
        {
            // Ensure a filter header exists before adding cues
            if (_filterStore.GetFilter(video.Id) is null)
            {
                var filter = new JcfFilter
                {
                    Title = video.Name ?? Path.GetFileNameWithoutExtension(video.Path) ?? "Unknown",
                    Year = video.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    Source = "NudeNet Visual Scan"
                };
                await _filterStore.SaveFilterAsync(video.Id, filter, cancellationToken).ConfigureAwait(false);
            }

            await _filterStore.AddCuesAsync(video.Id, cues, cancellationToken).ConfigureAwait(false);
        }

        _repository.SaveNudityScanHistory(
            video.Id,
            video.Path,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc,
            fps,
            threshold,
            cues.Count);

        return cues.Count;
    }

    /// <summary>
    /// Appends a timestamped log line to <see cref="NudityScanStatus.RecentLogs"/>.
    /// </summary>
    /// <param name="message">The log message.</param>
    private void AddLog(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (Status.RecentLogs)
        {
            Status.RecentLogs.Add(entry);
            while (Status.RecentLogs.Count > MaxLogEntries)
            {
                Status.RecentLogs.RemoveAt(0);
            }
        }
    }
}
