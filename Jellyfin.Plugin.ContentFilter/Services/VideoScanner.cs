using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Jellyfin.Plugin.ContentFilter.Configuration;
using Jellyfin.Plugin.ContentFilter.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Background scanner that extracts visual and transcript cues and produces JCF filter files.
/// </summary>
public class VideoScanner : IHostedService
{
    private static readonly TimeSpan PartialFlushInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MergeGap = TimeSpan.FromSeconds(3);
    private readonly ILogger<VideoScanner> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly FilterStore _filterStore;
    private readonly OllamaClient _ollamaClient;
    private readonly Channel<ScanJob> _queue;
    private readonly ConcurrentDictionary<Guid, ScanStatus> _statusByItem = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobTokens = new();
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoScanner"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaEncoder">The media encoder provider.</param>
    /// <param name="filterStore">The filter store.</param>
    /// <param name="ollamaClient">The Ollama client.</param>
    public VideoScanner(
        ILogger<VideoScanner> logger,
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        FilterStore filterStore,
        OllamaClient ollamaClient)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _filterStore = filterStore;
        _ollamaClient = ollamaClient;
        _queue = Channel.CreateBounded<ScanJob>(new BoundedChannelOptions(10)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>
    /// Tries to enqueue a media item for scanning.
    /// </summary>
    /// <param name="itemId">The media item ID.</param>
    /// <returns><see langword="true"/> when queued; otherwise <see langword="false"/>.</returns>
    public bool TryEnqueue(Guid itemId)
    {
        var status = _statusByItem.GetOrAdd(itemId, static id => new ScanStatus { ItemId = id });
        if (status.State == ScanState.Scanning)
        {
            return false;
        }

        var jobCts = new CancellationTokenSource();
        var job = new ScanJob
        {
            ItemId = itemId,
            Cts = jobCts
        };

        if (!_queue.Writer.TryWrite(job))
        {
            jobCts.Dispose();
            return false;
        }

        _jobTokens[itemId] = jobCts;
        status.State = ScanState.Scanning;
        status.Progress = 0;
        status.CurrentTime = "00:00:00.000";
        status.CuesFound = 0;
        status.Error = null;
        status.StartedAt = DateTime.UtcNow;
        status.CompletedAt = null;
        return true;
    }

    /// <summary>
    /// Tries to enqueue a media item for scanning.
    /// </summary>
    /// <param name="itemId">The media item ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when queued; otherwise <see langword="false"/>.</returns>
    public Task<bool> TryEnqueueAsync(Guid itemId, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.FromResult(TryEnqueue(itemId));
    }

    /// <summary>
    /// Gets the current scan status for an item.
    /// </summary>
    /// <param name="itemId">The media item ID.</param>
    /// <returns>The scan status.</returns>
    public ScanStatus GetStatus(Guid itemId)
    {
        return _statusByItem.GetOrAdd(itemId, static id => new ScanStatus
        {
            ItemId = id,
            State = ScanState.Idle,
            Progress = 0,
            CurrentTime = "00:00:00.000",
            CuesFound = 0
        });
    }

    /// <summary>
    /// Cancels an active scan job for a media item.
    /// </summary>
    /// <param name="itemId">The media item ID.</param>
    public void Cancel(Guid itemId)
    {
        if (_jobTokens.TryGetValue(itemId, out var cts))
        {
            cts.Cancel();
        }

        var status = GetStatus(itemId);
        if (status.State == ScanState.Scanning)
        {
            status.State = ScanState.Cancelled;
            status.CompletedAt = DateTime.UtcNow;
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workerTask = Task.Run(() => WorkerLoopAsync(_workerCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_workerCts is null || _workerTask is null)
        {
            return;
        }

        _workerCts.Cancel();
        _queue.Writer.TryComplete();
        await Task.WhenAny(_workerTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            using (job.Cts)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, job.Cts.Token);
                await RunScanAsync(job.ItemId, linked.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task RunScanAsync(Guid itemId, CancellationToken ct)
    {
        var status = GetStatus(itemId);
        var cues = new List<FilterCue>(1024);
        string? tempDir = null;
        try
        {
            var item = _libraryManager.GetItemById(itemId) as BaseItem;
            if (item is null || string.IsNullOrWhiteSpace(item.Path))
            {
                _logger.LogError("Scan aborted for {ItemId}: item not found or has no path", itemId);
                status.State = ScanState.Error;
                status.Error = "Media item not found or has no file path.";
                status.CompletedAt = DateTime.UtcNow;
                return;
            }

            _logger.LogInformation("Starting scan for item {ItemId} — path: {Path}", itemId, item.Path);

            tempDir = Path.Combine(Path.GetTempPath(), $"jcf_{itemId:N}");
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }

            Directory.CreateDirectory(tempDir);
            _logger.LogDebug("Temp dir created: {TempDir}", tempDir);

            var config = Plugin.Instance?.Configuration;

            var fps = config?.ScanFramesPerSecond ?? 1.0d;
            if (fps <= 0)
            {
                fps = 1.0d;
            }

            _logger.LogInformation("Extracting frames at {Fps} fps from {Path}", fps, item.Path);
            await ExtractFramesAsync(item.Path, tempDir, fps, ct).ConfigureAwait(false);

            var frameFiles = Directory.GetFiles(tempDir, "*.jpg", SearchOption.TopDirectoryOnly);
            Array.Sort(frameFiles, StringComparer.Ordinal);
            _logger.LogInformation("Frame extraction complete — {Count} frames extracted", frameFiles.Length);

            var model = string.IsNullOrWhiteSpace(config?.OllamaVisionModel)
                ? "llava"
                : config.OllamaVisionModel.Trim('"', '\'', ' ');
            var visualDescriptions = GetEnabledVisualDescriptions(config).ToArray();
            _logger.LogInformation("Analyzing {Count} frames with Ollama model '{Model}' ({Categories} categories enabled)",
                frameFiles.Length, model, visualDescriptions.Length);

            var frameDuration = TimeSpan.FromSeconds(1d / fps);
            var partialFlushAt = DateTimeOffset.UtcNow + PartialFlushInterval;

            for (var frameIndex = 0; frameIndex < frameFiles.Length; frameIndex++)
            {
                ct.ThrowIfCancellationRequested();

                var start = TimeSpan.FromSeconds(frameIndex / fps);

                if (frameIndex % 10 == 0)
                {
                    _logger.LogDebug("Ollama: analyzing frame {Frame}/{Total} at {Time}",
                        frameIndex + 1, frameFiles.Length, FormatTs(start));
                }

                var jpeg = await File.ReadAllBytesAsync(frameFiles[frameIndex], ct).ConfigureAwait(false);
                var (detected, description) = await _ollamaClient
                    .AnalyzeFrameAsync(jpeg, model, visualDescriptions, ct)
                    .ConfigureAwait(false);

                if (detected.Count > 0)
                {
                    _logger.LogDebug("Frame {Frame}: detected {Categories} — {Description}",
                        frameIndex + 1, string.Join(", ", detected), description);
                }

                foreach (var category in detected)
                {
                    var group = FilterDictionary.GetGroup(category);
                    if (!IsGroupEnabled(config, group))
                    {
                        continue;
                    }

                    cues.Add(new FilterCue
                    {
                        Start = start,
                        End = start + frameDuration,
                        Description = description,
                        Category = category,
                        Channel = FilterDictionary.GetDefaultChannel(category),
                        Action = GetGroupAction(config, group)
                    });
                }

                status.Progress = frameFiles.Length == 0 ? 0 : Math.Clamp((frameIndex + 1d) / frameFiles.Length, 0, 0.999);
                status.CurrentTime = FormatTs(start);
                status.CuesFound = cues.Count;

                if (DateTimeOffset.UtcNow >= partialFlushAt)
                {
                    var partialFilter = BuildFilter(item, cues, "JCF scan partial");
                    await _filterStore.SaveFilterAsync(itemId, partialFilter, ct).ConfigureAwait(false);
                    partialFlushAt = DateTimeOffset.UtcNow + PartialFlushInterval;
                }
            }

            if (config?.ScanAnalyzeAudio ?? true)
            {
                cues.AddRange(ScanSrtWordMatches(item.Path, config));
            }

            var merged = MergeCues(cues);
            var finalFilter = BuildFilter(item, merged, "Jellyfin Content Filter scan");
            await _filterStore.SaveFilterAsync(itemId, finalFilter, ct).ConfigureAwait(false);

            _logger.LogInformation("Scan complete for {ItemId} — {Cues} cues written", itemId, merged.Count);
            status.State = ScanState.Complete;
            status.Progress = 1.0;
            status.CuesFound = merged.Count;
            status.CompletedAt = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Scan cancelled for item {ItemId} (progress was {Progress:P0})", itemId, status.Progress);
            status.State = ScanState.Cancelled;
            status.CompletedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed for item {ItemId}", itemId);
            status.State = ScanState.Error;
            status.Error = ex.Message;
            status.CompletedAt = DateTime.UtcNow;
        }
        finally
        {
            _jobTokens.TryRemove(itemId, out _);
            if (!string.IsNullOrWhiteSpace(tempDir) && Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean temporary directory {TempDir}", tempDir);
                }
            }
        }
    }

    private async Task ExtractFramesAsync(string mediaPath, string tempDir, double fps, CancellationToken ct)
    {
        var args = $"-i \"{mediaPath}\" -vf fps={fps:0.###} -q:v 3 \"{Path.Combine(tempDir, "%06d.jpg")}\" -y";
        _logger.LogInformation("ffmpeg command: {Bin} {Args}", _mediaEncoder.EncoderPath, args);

        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start ffmpeg process.");
        }

        _logger.LogDebug("ffmpeg started — PID {Pid}", process.Id);

        // Read stderr concurrently — if the buffer fills while we wait, WaitForExitAsync deadlocks.
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ffmpeg wait cancelled — killing PID {Pid}", process.Id);
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        _logger.LogDebug("ffmpeg exited with code {ExitCode}", process.ExitCode);
        if (process.ExitCode != 0)
        {
            _logger.LogError("ffmpeg stderr: {Stderr}", stderr);
            throw new InvalidOperationException($"ffmpeg frame extraction failed (exit {process.ExitCode}): {stderr}");
        }
        else if (!string.IsNullOrWhiteSpace(stderr))
        {
            _logger.LogDebug("ffmpeg stderr (ok): {Stderr}", stderr[..Math.Min(stderr.Length, 500)]);
        }
    }

    private static IEnumerable<KeyValuePair<string, string[]>> GetEnabledVisualDescriptions(PluginConfiguration? config)
    {
        foreach (var pair in FilterDictionary.GetVisualDescriptions())
        {
            if (IsGroupEnabled(config, FilterDictionary.GetGroup(pair.Key)))
            {
                yield return pair;
            }
        }
    }

    private static List<FilterCue> MergeCues(List<FilterCue> cues)
    {
        if (cues.Count == 0)
        {
            return [];
        }

        var ordered = cues
            .OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Start)
            .ToList();

        var merged = new List<FilterCue>(ordered.Count);
        var current = CloneCue(ordered[0]);

        for (var i = 1; i < ordered.Count; i++)
        {
            var next = ordered[i];
            var sameCategory = string.Equals(current.Category, next.Category, StringComparison.OrdinalIgnoreCase);
            var gap = next.Start - current.End;
            if (sameCategory && gap <= MergeGap)
            {
                if (next.End > current.End)
                {
                    current.End = next.End;
                }

                if (!string.IsNullOrWhiteSpace(next.Description) && !string.Equals(next.Description, current.Description, StringComparison.Ordinal))
                {
                    current.Description = string.IsNullOrWhiteSpace(current.Description)
                        ? next.Description
                        : $"{current.Description}; {next.Description}";
                }

                continue;
            }

            merged.Add(current);
            current = CloneCue(next);
        }

        merged.Add(current);
        return merged;
    }

    private static FilterCue CloneCue(FilterCue cue)
    {
        return new FilterCue
        {
            Start = cue.Start,
            End = cue.End,
            Description = cue.Description,
            Category = cue.Category,
            Channel = cue.Channel,
            Action = cue.Action
        };
    }

    /// <summary>
    /// Resolves the best available SRT file for a media path.
    /// Checks WhisperSubs-generated files first, then falls back to a plain .srt.
    /// </summary>
    private static string? FindSrtPath(string mediaPath, string preferredLanguage)
    {
        var dir = Path.GetDirectoryName(mediaPath);
        var stem = Path.GetFileNameWithoutExtension(mediaPath);
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        // Check WhisperSubs naming first (stem.{lang}.WhisperSubs.srt),
        // then the generated.srt convention, then a plain .srt fallback.
        string[] candidates =
        [
            Path.Combine(dir, $"{stem}.{preferredLanguage}.WhisperSubs.srt"),
            Path.Combine(dir, $"{stem}.{preferredLanguage}.generated.srt"),
            Path.Combine(dir, $"{stem}.WhisperSubs.srt"),
            Path.Combine(dir, $"{stem}.generated.srt"),
            Path.Combine(dir, $"{stem}.srt"),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<FilterCue> ScanSrtWordMatches(string mediaPath, PluginConfiguration? config)
    {
        var preferredLanguage = config?.PreferredAudioLanguage ?? "en";
        var srtPath = FindSrtPath(mediaPath, preferredLanguage);
        if (string.IsNullOrWhiteSpace(srtPath))
        {
            yield break;
        }

        var content = File.ReadAllText(srtPath);
        foreach (var block in SplitSrtBlocks(content))
        {
            if (!TryParseSrtBlock(block, out var start, out var end, out var text))
            {
                continue;
            }

            foreach (var (category, phrases) in FilterDictionary.GetWordLists())
            {
                var group = FilterDictionary.GetGroup(category);
                if (!IsGroupEnabled(config, group))
                {
                    continue;
                }

                foreach (var phrase in phrases)
                {
                    if (!string.IsNullOrWhiteSpace(phrase) &&
                        text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return new FilterCue
                        {
                            Start = start,
                            End = end,
                            Description = $"Matched phrase: {phrase}",
                            Category = category,
                            Channel = FilterDictionary.GetDefaultChannel(category),
                            Action = GetGroupAction(config, group)
                        };
                    }
                }
            }
        }
    }

    private static bool TryParseSrtBlock(string block, out TimeSpan start, out TimeSpan end, out string text)
    {
        start = TimeSpan.Zero;
        end = TimeSpan.Zero;
        text = string.Empty;
        if (string.IsNullOrWhiteSpace(block))
        {
            return false;
        }

        var lines = block
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length < 3)
        {
            return false;
        }

        var timestampLine = lines[1];
        var tokens = timestampLine.Split(" --> ", StringSplitOptions.TrimEntries);
        if (tokens.Length != 2)
        {
            return false;
        }

        if (!TryParseSrtTime(tokens[0], out start) || !TryParseSrtTime(tokens[1], out end))
        {
            return false;
        }

        text = string.Join(' ', lines.Skip(2));
        return true;
    }

    private static IEnumerable<string> SplitSrtBlocks(string srtContent)
    {
        return srtContent.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool TryParseSrtTime(string raw, out TimeSpan value)
    {
        var normalized = raw.Replace(',', '.');
        return TimeSpan.TryParse(normalized, out value);
    }

    private static bool IsGroupEnabled(PluginConfiguration? config, string group)
    {
        if (config is null)
        {
            return true;
        }

        return group switch
        {
            "Language" => config.LanguageEnabled,
            "SexualReferences" => config.SexualReferencesEnabled,
            "SexAndNudity" => config.SexAndNudityEnabled,
            "Violence" => config.ViolenceEnabled,
            "Substances" => config.SubstancesEnabled,
            "Medical" => config.MedicalEnabled,
            "Structural" => config.StructuralEnabled,
            _ => true
        };
    }

    private static string GetGroupAction(PluginConfiguration? config, string group)
    {
        if (config is null)
        {
            return "skip";
        }

        var action = group switch
        {
            "Language" => config.LanguageAction,
            "SexualReferences" => config.SexualReferencesAction,
            "SexAndNudity" => config.SexAndNudityAction,
            "Violence" => config.ViolenceAction,
            "Substances" => config.SubstancesAction,
            "Medical" => config.MedicalAction,
            "Structural" => config.StructuralAction,
            _ => "skip"
        };

        return action is "skip" or "mute" ? action : "skip";
    }

    private static JcfFilter BuildFilter(BaseItem item, List<FilterCue> cues, string source)
    {
        var imdbId = item.GetProviderId(MetadataProvider.Imdb);
        return new JcfFilter
        {
            Title = item.Name,
            Year = item.ProductionYear?.ToString() ?? string.Empty,
            ImdbUrl = string.IsNullOrWhiteSpace(imdbId) ? string.Empty : $"https://www.imdb.com/title/{imdbId}",
            Source = source,
            Cues = cues
        };
    }

    private static string FormatTs(TimeSpan value)
    {
        return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
    }
}
