using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ContentFilter.Models;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Service managing automated searching, downloading, cleaning, and default assignment of subtitles.
/// </summary>
public class SubtitleSyncService : IHostedService, IDisposable
{
    private sealed record NewMediaQueueItem(Guid ItemId, string ItemName, DateTime AvailableAt);

    private readonly ILogger<SubtitleSyncService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ISubtitleManager _subtitleManager;
    private readonly IServerConfigurationManager _serverConfigManager;
    private readonly SubtitleFilter _subtitleFilter;
    private readonly SubtitleWordScanner _subtitleWordScanner;
    private readonly FilterStore _filterStore;
    private readonly SqliteFilterRepository _sqliteRepository;
    private readonly IServiceProvider _serviceProvider;

    private readonly Channel<NewMediaQueueItem> _newMediaQueue = Channel.CreateBounded<NewMediaQueueItem>(new BoundedChannelOptions(500)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<Guid, DateTime> _enqueuedMedia = new();
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private bool _disposed;

    private readonly object _syncLock = new();
    private CancellationTokenSource? _activeSyncCts;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleSyncService"/> class.
    /// </summary>
    public SubtitleSyncService(
        ILogger<SubtitleSyncService> logger,
        ILibraryManager libraryManager,
        ISubtitleManager subtitleManager,
        IServerConfigurationManager serverConfigManager,
        SubtitleFilter subtitleFilter,
        SubtitleWordScanner subtitleWordScanner,
        FilterStore filterStore,
        SqliteFilterRepository sqliteRepository,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _subtitleManager = subtitleManager;
        _serverConfigManager = serverConfigManager;
        _subtitleFilter = subtitleFilter;
        _subtitleWordScanner = subtitleWordScanner;
        _filterStore = filterStore;
        _sqliteRepository = sqliteRepository;
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workerTask = Task.Run(() => ProcessNewMediaQueueAsync(_workerCts.Token), CancellationToken.None);
        _logger.LogInformation("ContentFilter SubtitleSyncService started and listening for new media additions.");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        if (_workerCts is not null)
        {
            _workerCts.Cancel();
            _newMediaQueue.Writer.TryComplete();
        }

        if (_workerTask is not null)
        {
            await Task.WhenAny(_workerTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }
        _logger.LogInformation("ContentFilter SubtitleSyncService stopped.");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes managed and unmanaged resources.
    /// </summary>
    /// <param name="disposing">Whether managed resources are being disposed.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _libraryManager.ItemAdded -= OnItemAdded;
            _workerCts?.Cancel();
            _workerCts?.Dispose();
            _activeSyncCts?.Dispose();
        }

        _disposed = true;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.AutoProcessNewMediaSubtitles != true)
        {
            return;
        }

        if (e.Item is Video { IsVirtualItem: false } video && !string.IsNullOrWhiteSpace(video.Path))
        {
            EnqueueNewMedia(video.Id, video.Name);
        }
    }

    /// <summary>
    /// Enqueues a newly added video item for automated subtitle downloading and cleaning.
    /// </summary>
    /// <param name="itemId">The video item ID.</param>
    /// <param name="itemName">The item display name.</param>
    /// <returns><see langword="true"/> if enqueued; <see langword="false"/> if already queued.</returns>
    public bool EnqueueNewMedia(Guid itemId, string itemName)
    {
        var availableAt = DateTime.UtcNow.AddSeconds(15);
        if (!_enqueuedMedia.TryAdd(itemId, availableAt))
        {
            return false;
        }

        var queueItem = new NewMediaQueueItem(itemId, itemName, availableAt);
        if (!_newMediaQueue.Writer.TryWrite(queueItem))
        {
            _enqueuedMedia.TryRemove(itemId, out _);
            return false;
        }

        Status.PendingNewMediaQueueCount = _enqueuedMedia.Count;
        AddLog($"[Auto-Process] Queued newly added media: \"{itemName}\" (settling for 15s)");
        _logger.LogInformation("Queued newly added video {ItemId} ({Name}) for subtitle processing.", itemId, itemName);
        return true;
    }

    private async Task ProcessNewMediaQueueAsync(CancellationToken ct)
    {
        var reader = _newMediaQueue.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var queueItem))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // 1. Settling delay: wait until AvailableAt
                    var delay = queueItem.AvailableAt - DateTime.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }

                    // 2. Check if library-wide sync is actively running; if so, wait briefly
                    while (Status.IsRunning)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    }

                    // 3. Process video
                    AddLog($"[Auto-Process] Processing subtitles for \"{queueItem.ItemName}\"...");
                    var result = await ProcessSingleVideoAsync(queueItem.ItemId, force: false, overrideLanguage: null, ct).ConfigureAwait(false);

                    if (result.Cleaned)
                    {
                        AddLog($"[Auto-Process] Cleaned subtitle generated and set default for \"{queueItem.ItemName}\".");
                    }
                    else if (result.Skipped)
                    {
                        AddLog($"[Auto-Process] Skipped \"{queueItem.ItemName}\" (already has clean subtitle or locked).");
                    }
                    else if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    {
                        AddLog($"[Auto-Process] Error on \"{queueItem.ItemName}\": {result.ErrorMessage}");
                    }

                    // 4. Rate-limit throttle between items to avoid hammering remote subtitle providers
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error auto-processing new media subtitle for {ItemId} ({Name})",
                        queueItem.ItemId, queueItem.ItemName);
                    AddLog($"[Auto-Process] Exception on \"{queueItem.ItemName}\": {ex.Message}");
                }
                finally
                {
                    _enqueuedMedia.TryRemove(queueItem.ItemId, out _);
                    Status.PendingNewMediaQueueCount = _enqueuedMedia.Count;
                }
            }
        }
    }

    /// <summary>
    /// Gets the current status of the subtitle sync service.
    /// </summary>
    public SubtitleSyncStatus Status { get; } = new();

    /// <summary>
    /// Resolves the effective target subtitle language (3-letter and 2-letter).
    /// </summary>
    /// <param name="overrideLanguage">Optional language override.</param>
    /// <returns>A tuple of (3-letter language code, 2-letter language code).</returns>
    public (string Lang3, string Lang2) ResolveTargetLanguage(string? overrideLanguage = null)
    {
        var config = Plugin.Instance?.Configuration;
        var lang = overrideLanguage;

        if (string.IsNullOrWhiteSpace(lang))
        {
            var configLang = config?.SubtitleDownloadLanguage;
            if (!string.IsNullOrWhiteSpace(configLang) && !configLang.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                lang = configLang;
            }
            else
            {
                lang = _serverConfigManager.Configuration.PreferredMetadataLanguage;
            }
        }

        if (string.IsNullOrWhiteSpace(lang))
        {
            lang = "en";
        }

        var lang2 = SubtitleFilter.ToTwoLetterLanguage(lang);
        var lang3 = lang2 switch
        {
            "en" => "eng",
            "es" => "spa",
            "fr" => "fra",
            "de" => "deu",
            "it" => "ita",
            "pt" => "por",
            "ru" => "rus",
            "ja" => "jpn",
            "zh" => "zho",
            "ko" => "kor",
            _ => lang.Length >= 3 ? lang[..3].ToLowerInvariant() : lang2
        };

        return (lang3, lang2);
    }

    /// <summary>
    /// Starts the library-wide automated subtitle download and clean sync job in the background.
    /// </summary>
    /// <param name="forceAll">Whether to reprocess items that already have clean subtitles.</param>
    /// <param name="overrideLanguage">Optional language override.</param>
    /// <param name="progress">Progress reporter (0-100%).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> if started; <see langword="false"/> if already running.</returns>
    public bool StartSync(bool forceAll, string? overrideLanguage = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        lock (_syncLock)
        {
            if (Status.IsRunning)
            {
                return false;
            }

            Task.Run(async () =>
            {
                try
                {
                    await RunSyncAsync(forceAll, overrideLanguage, progress, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                }
            }, CancellationToken.None);

            return true;
        }
    }

    /// <summary>
    /// Executes library-wide automated subtitle download and cleaning, completing when the run finishes.
    /// Used directly by scheduled tasks and test runners.
    /// </summary>
    /// <param name="forceAll">Whether to reprocess items that already have clean subtitles.</param>
    /// <param name="overrideLanguage">Optional language override.</param>
    /// <param name="progress">Progress reporter (0-100%).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RunSyncAsync(bool forceAll, string? overrideLanguage = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        lock (_syncLock)
        {
            if (Status.IsRunning)
            {
                throw new InvalidOperationException("A subtitle sync run is already in progress.");
            }

            _activeSyncCts?.Dispose();
            _activeSyncCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Status.State = SubtitleSyncState.Running;
            Status.StartedAt = DateTime.UtcNow;
            Status.CompletedAt = null;
            Status.ProcessedItems = 0;
            Status.TotalItems = 0;
            Status.SubtitlesDownloaded = 0;
            Status.SubtitlesCleaned = 0;
            Status.SubtitlesSkipped = 0;
            Status.ErrorCount = 0;
            Status.ProgressPercentage = 0;
            Status.LastErrorMessage = null;
            Status.CurrentItemName = "Initializing media query...";
            AddLog("Started automated subtitle sync.");
        }

        var token = _activeSyncCts.Token;
        try
        {
            await RunSyncInternalAsync(forceAll, overrideLanguage, progress, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Status.State = SubtitleSyncState.Cancelled;
            Status.CompletedAt = DateTime.UtcNow;
            AddLog("Subtitle sync was cancelled by user.");
            _logger.LogInformation("Automated subtitle sync cancelled.");
        }
        catch (Exception ex)
        {
            Status.State = SubtitleSyncState.Error;
            Status.CompletedAt = DateTime.UtcNow;
            Status.LastErrorMessage = ex.Message;
            AddLog($"Subtitle sync failed: {ex.Message}");
            _logger.LogError(ex, "Automated subtitle sync encountered a fatal error.");
            throw;
        }
    }

    /// <summary>
    /// Cancels any currently active subtitle sync run.
    /// </summary>
    public void CancelSync()
    {
        lock (_syncLock)
        {
            if (Status.IsRunning)
            {
                _activeSyncCts?.Cancel();
            }
        }
    }

    /// <summary>
    /// Internal execution loop for library-wide subtitle sync.
    /// </summary>
    private async Task RunSyncInternalAsync(bool forceAll, string? overrideLanguage, IProgress<double>? progress, CancellationToken ct)
    {
        var (targetLang3, targetLang2) = ResolveTargetLanguage(overrideLanguage);
        var config = Plugin.Instance?.Configuration;
        bool autoDownloadEnabled = config?.AutoDownloadSubtitles ?? true;
        bool autoMuteProfanity = config?.AutoMuteProfanityFromSubtitles ?? true;
        bool overwriteExisting = forceAll || (config?.OverwriteExistingCleanSubtitles ?? false);

        AddLog($"Target language: {targetLang3} ({targetLang2}). Remote auto-download enabled: {autoDownloadEnabled}.");
        _logger.LogInformation("Beginning subtitle sync. Language={Lang}, AutoDownload={AutoDL}, Overwrite={Overwrite}",
            targetLang3, autoDownloadEnabled, overwriteExisting);

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            IsVirtualItem = false,
            Recursive = true
        };

        var items = _libraryManager.GetItemList(query)
            .OfType<Video>()
            .Where(v => !string.IsNullOrWhiteSpace(v.Path))
            .ToList();

        Status.TotalItems = items.Count;
        AddLog($"Found {items.Count} media video item(s) in library.");

        var lockedItemIds = _sqliteRepository.GetAllLockedItemIds();

        for (int i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var video = items[i];
            Status.ProcessedItems = i + 1;
            Status.CurrentItemId = video.Id;
            Status.CurrentItemName = $"{video.Name} ({i + 1}/{items.Count})";
            Status.ProgressPercentage = Math.Round(((double)(i + 1) / items.Count) * 100, 1);
            progress?.Report(Status.ProgressPercentage);

            // 1. Skip items that user has locked
            if (lockedItemIds.Contains(video.Id))
            {
                Status.SubtitlesSkipped++;
                continue;
            }

            try
            {
                var res = await ProcessSingleVideoAsync(video.Id, forceAll, overrideLanguage, ct).ConfigureAwait(false);
                if (res.Downloaded)
                {
                    Status.SubtitlesDownloaded++;
                    AddLog($"Downloaded subtitle for: {video.Name}");
                }

                if (res.Cleaned)
                {
                    Status.SubtitlesCleaned++;
                }
                else if (res.Skipped)
                {
                    Status.SubtitlesSkipped++;
                }

                if (!string.IsNullOrWhiteSpace(res.ErrorMessage))
                {
                    Status.ErrorCount++;
                    AddLog($"Error on {video.Name}: {res.ErrorMessage}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Status.ErrorCount++;
                _logger.LogWarning(ex, "Error processing subtitles for item {ItemId} ({Name})", video.Id, video.Name);
                AddLog($"Error on {video.Name}: {ex.Message}");
            }
        }

        Status.State = SubtitleSyncState.Completed;
        Status.CompletedAt = DateTime.UtcNow;
        Status.CurrentItemName = null;
        Status.ProgressPercentage = 100;
        AddLog($"Subtitle sync finished. Cleaned: {Status.SubtitlesCleaned}, Downloaded: {Status.SubtitlesDownloaded}, Skipped: {Status.SubtitlesSkipped}, Errors: {Status.ErrorCount}");
        _logger.LogInformation("Subtitle sync completed. Cleaned={Cleaned}, Downloaded={Downloaded}, Skipped={Skipped}, Errors={Errors}",
            Status.SubtitlesCleaned, Status.SubtitlesDownloaded, Status.SubtitlesSkipped, Status.ErrorCount);
    }

    /// <summary>
    /// Processes subtitle download, profanity cue detection, and clean subtitle generation for a single video item.
    /// </summary>
    /// <param name="itemId">The video item ID.</param>
    /// <param name="force">Whether to overwrite existing clean subtitles even if already present.</param>
    /// <param name="overrideLanguage">Optional language override.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="SingleSubtitleProcessResult"/> detailing the outcome.</returns>
    public async Task<SingleSubtitleProcessResult> ProcessSingleVideoAsync(
        Guid itemId,
        bool force = false,
        string? overrideLanguage = null,
        CancellationToken cancellationToken = default)
    {
        var result = new SingleSubtitleProcessResult { ItemId = itemId };

        var video = _libraryManager.GetItemById(itemId) as Video;
        if (video is null || string.IsNullOrWhiteSpace(video.Path))
        {
            result.Skipped = true;
            result.ErrorMessage = "Media item not found or has no physical file path.";
            return result;
        }

        // 1. Skip items that user has locked
        if (_sqliteRepository.IsItemLocked(itemId))
        {
            result.Skipped = true;
            return result;
        }

        var (targetLang3, targetLang2) = ResolveTargetLanguage(overrideLanguage);
        var config = Plugin.Instance?.Configuration;
        bool autoDownloadEnabled = config?.AutoDownloadSubtitles ?? true;
        bool autoMuteProfanity = config?.AutoMuteProfanityFromSubtitles ?? true;
        bool overwriteExisting = force || (config?.OverwriteExistingCleanSubtitles ?? false);

        // 2. Skip if already has clean sidecar and not overwriting
        bool hasClean = _subtitleFilter.HasSidecarFilteredSrt(itemId, targetLang2);
        if (hasClean && !overwriteExisting)
        {
            result.Skipped = true;
            return result;
        }

        try
        {
            // 3. Search and download remote subtitle if external subtitle is missing
            bool hasExternalSource = HasExternalSrtFile(video, targetLang2, targetLang3);
            if (!hasExternalSource && autoDownloadEnabled)
            {
                var downloaded = await SearchAndDownloadBestSubtitleAsync(video, targetLang3, cancellationToken).ConfigureAwait(false);
                if (downloaded)
                {
                    result.Downloaded = true;
                }
            }

            // 4. Auto-generate mute cues from profanity dictionary if enabled
            if (autoMuteProfanity)
            {
                await EnsureItemHasWordFilterAsync(itemId, targetLang3, cancellationToken).ConfigureAwait(false);
            }

            // 5. Generate clean subtitles and set as default
            var filter = _filterStore.GetFilter(itemId);
            var generatedPath = await _subtitleFilter.RegenerateAsync(itemId, filter, targetLang3, cancellationToken).ConfigureAwait(false);
            if (generatedPath != null)
            {
                result.Cleaned = true;
            }
            else
            {
                result.Skipped = true;
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing subtitle for video {ItemId} ({Name})", video.Id, video.Name);
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// Checks whether an external SRT file exists adjacent to the media file on disk.
    /// </summary>
    private static bool HasExternalSrtFile(Video video, string lang2, string lang3)
    {
        if (string.IsNullOrWhiteSpace(video.Path)) return false;
        var dir = Path.GetDirectoryName(video.Path);
        var stem = Path.GetFileNameWithoutExtension(video.Path);
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(stem) || !Directory.Exists(dir))
        {
            return false;
        }

        string[] candidates =
        [
            Path.Combine(dir, $"{stem}.{lang2}.srt"),
            Path.Combine(dir, $"{stem}.{lang3}.srt"),
            Path.Combine(dir, $"{stem}.srt"),
            Path.Combine(dir, $"{stem}.{lang2}.WhisperSubs.srt"),
            Path.Combine(dir, $"{stem}.{lang3}.WhisperSubs.srt")
        ];

        return candidates.Any(File.Exists);
    }

    /// <summary>
    /// Searches remote subtitle providers for an item and downloads the best matching candidate.
    /// </summary>
    private async Task<bool> SearchAndDownloadBestSubtitleAsync(Video video, string language, CancellationToken ct)
    {
        try
        {
            var results = await _subtitleManager.SearchSubtitles(video, language, null, true, ct).ConfigureAwait(false);
            if (results == null || results.Length == 0)
            {
                return false;
            }

            // Prefer hash match, then highest rating / download count, then first non-forced
            var best = results.FirstOrDefault(r => r.IsHashMatch == true)
                       ?? results.Where(r => r.Forced != true)
                                 .OrderByDescending(r => r.CommunityRating ?? 0)
                                 .ThenByDescending(r => r.DownloadCount ?? 0)
                                 .FirstOrDefault()
                       ?? results[0];

            if (string.IsNullOrWhiteSpace(best.Id))
            {
                return false;
            }

            _logger.LogInformation("Found remote subtitle match for {ItemName}: {SubName} ({Provider})",
                video.Name, best.Name, best.ProviderName);

            await _subtitleManager.DownloadSubtitles(video, best.Id, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Remote subtitle search/download encountered an issue for item {ItemId}", video.Id);
            return false;
        }
    }

    /// <summary>
    /// Scans an item's dialogue and auto-generates mute cues for detected profanity if the item has no cues yet.
    /// </summary>
    private async Task EnsureItemHasWordFilterAsync(Guid itemId, string language, CancellationToken ct)
    {
        var filter = _filterStore.GetFilter(itemId);
        var existingCues = filter?.Cues ?? [];

        var scan = await _subtitleWordScanner.ScanWordsAsync(itemId, language, ct).ConfigureAwait(false);
        if (scan.Words.Count == 0)
        {
            return;
        }

        var config = Plugin.Instance?.Configuration;
        var action = config?.SubtitleWordAction ?? "mute";
        var channel = action.Equals("skip", StringComparison.OrdinalIgnoreCase) ? "both" : "audio";
        var cuesToAdd = new List<FilterCue>();

        foreach (var group in scan.Words)
        {
            foreach (var occ in group.Occurrences)
            {
                var occStart = TimeSpan.FromSeconds(occ.StartSeconds);
                var occEnd = TimeSpan.FromSeconds(occ.EndSeconds);

                // Skip if this occurrence is already covered by an existing mute or skip cue
                if (existingCues.Any(c =>
                    (c.Action.Equals("mute", StringComparison.OrdinalIgnoreCase) ||
                     c.Action.Equals("skip", StringComparison.OrdinalIgnoreCase)) &&
                    occStart < c.End && occEnd > c.Start))
                {
                    continue;
                }

                var wordLabel = !string.IsNullOrWhiteSpace(occ.MatchedWord) ? occ.MatchedWord : group.Word;
                cuesToAdd.Add(new FilterCue
                {
                    Start = occStart,
                    End = occEnd,
                    Category = group.Category,
                    Channel = channel,
                    Action = action,
                    Description = $"Spoken: \"{wordLabel}\""
                });
            }
        }

        if (cuesToAdd.Count > 0)
        {
            await _filterStore.AddCuesAsync(itemId, cuesToAdd, ct).ConfigureAwait(false);
            _logger.LogDebug("Auto-generated {Count} profanity mute cues for item {ItemId}", cuesToAdd.Count, itemId);
        }
    }

    /// <summary>
    /// Searches remote subtitle providers for an item and returns DTOs for the UI.
    /// </summary>
    public async Task<List<RemoteSubtitleDto>> SearchRemoteSubtitlesAsync(Guid itemId, string? language, CancellationToken ct)
    {
        var item = _libraryManager.GetItemById(itemId) as Video;
        if (item is null)
        {
            return [];
        }

        var (lang3, _) = ResolveTargetLanguage(language);
        var results = await _subtitleManager.SearchSubtitles(item, lang3, null, false, ct).ConfigureAwait(false);
        if (results == null || results.Length == 0)
        {
            return [];
        }

        return results.Select(r => new RemoteSubtitleDto
        {
            Id = r.Id,
            Name = !string.IsNullOrWhiteSpace(r.Name) ? r.Name : $"{r.ProviderName} Subtitle ({r.Format})",
            ProviderName = r.ProviderName ?? "Unknown",
            Format = r.Format ?? "srt",
            Language = r.ThreeLetterISOLanguageName ?? lang3,
            IsHashMatch = r.IsHashMatch ?? false,
            CommunityRating = r.CommunityRating,
            DownloadCount = r.DownloadCount,
            IsForced = r.Forced ?? false,
            IsHearingImpaired = r.HearingImpaired ?? false
        }).ToList();
    }

    /// <summary>
    /// Downloads a specific chosen remote subtitle candidate, cleans it, sets it as default, and locks the item.
    /// </summary>
    public async Task<bool> DownloadRemoteSubtitleAsync(Guid itemId, string subtitleId, CancellationToken ct)
    {
        var item = _libraryManager.GetItemById(itemId) as Video;
        if (item is null || string.IsNullOrWhiteSpace(subtitleId))
        {
            return false;
        }

        await _subtitleManager.DownloadSubtitles(item, subtitleId, ct).ConfigureAwait(false);

        // Lock item against automated sync overwrite
        _sqliteRepository.SetSubtitleOverride(itemId, 0, true, $"ProviderId: {subtitleId}");

        var (lang3, _) = ResolveTargetLanguage();
        var filter = _filterStore.GetFilter(itemId);
        var generated = await _subtitleFilter.RegenerateAsync(itemId, filter, lang3, ct).ConfigureAwait(false);
        return generated != null;
    }

    /// <summary>
    /// Applies a time shift offset (in seconds) to an item's subtitle file and JCF cues.
    /// </summary>
    public async Task<bool> ApplySubtitleOffsetAsync(Guid itemId, double offsetSeconds, CancellationToken ct)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return false;
        }

        int offsetMs = (int)Math.Round(offsetSeconds * 1000);
        _sqliteRepository.SetSubtitleOffset(itemId, offsetMs);

        // Shift cues in sync so audio muting matches shifted subtitle timing
        await _filterStore.ShiftCuesAsync(itemId, offsetSeconds, "all", ct).ConfigureAwait(false);

        var (lang3, _) = ResolveTargetLanguage();
        var filter = _filterStore.GetFilter(itemId);
        var generated = await _subtitleFilter.RegenerateAsync(itemId, filter, lang3, ct).ConfigureAwait(false);
        return generated != null;
    }

    /// <summary>
    /// Saves a user-uploaded custom SRT subtitle file, cleans it, and sets it as default.
    /// </summary>
    public async Task<bool> SaveCustomSubtitleAsync(Guid itemId, string srtContent, CancellationToken ct)
    {
        var item = _libraryManager.GetItemById(itemId) as Video;
        if (item is null || string.IsNullOrWhiteSpace(srtContent) || string.IsNullOrWhiteSpace(item.Path))
        {
            return false;
        }

        var (lang3, lang2) = ResolveTargetLanguage();
        var dir = Path.GetDirectoryName(item.Path);
        var stem = Path.GetFileNameWithoutExtension(item.Path);

        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && !string.IsNullOrWhiteSpace(stem))
        {
            // Save as standard external srt
            var destPath = Path.Combine(dir, $"{stem}.{lang2}.srt");
            await File.WriteAllTextAsync(destPath, srtContent, System.Text.Encoding.UTF8, ct).ConfigureAwait(false);
        }

        // Lock against automated sync overwrite
        _sqliteRepository.SetSubtitleOverride(itemId, 0, true, "Custom Upload");

        var filter = _filterStore.GetFilter(itemId);
        var generated = await _subtitleFilter.RegenerateAsync(itemId, filter, lang3, ct).ConfigureAwait(false);
        return generated != null;
    }

    private void AddLog(string message)
    {
        var entry = $"[{DateTime.UtcNow:HH:mm:ss}] {message}";
        lock (Status.RecentLogs)
        {
            Status.RecentLogs.Add(entry);
            if (Status.RecentLogs.Count > 100)
            {
                Status.RecentLogs.RemoveAt(0);
            }
        }
    }
}
