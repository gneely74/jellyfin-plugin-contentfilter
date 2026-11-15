using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Jellyfin.Plugin.ContentFilter.Configuration;
using Jellyfin.Plugin.ContentFilter.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Service responsible for extracting video frames via Jellyfin's <c>ffmpeg</c> binary,
/// analyzing them with an external NudeNet HTTP detection service, and generating merged
/// <see cref="FilterCue"/> intervals for nudity scenes.
/// </summary>
public class NudityDetectionService
{
    /// <summary>
    /// Number of extracted JPEG frames uploaded per HTTP multipart batch request.
    /// </summary>
    private const int BatchSize = 16;

    /// <summary>
    /// Shared JSON serializer options configured for case-insensitive property matching.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// NudeNet class labels representing explicit/exposed nudity, mapped to <c>SexAndNudity.FullNudity</c>.
    /// </summary>
    private static readonly HashSet<string> FullNudityLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "FEMALE_GENITALIA_EXPOSED",
        "MALE_GENITALIA_EXPOSED",
        "ANUS_EXPOSED",
        "FEMALE_BREAST_EXPOSED",
        "BUTTOCKS_EXPOSED"
    };

    /// <summary>
    /// NudeNet class labels representing covered/partial nudity, mapped to <c>SexAndNudity.PartialNudity</c>
    /// when <see cref="PluginConfiguration.NudityDetectPartial"/> is enabled.
    /// </summary>
    private static readonly HashSet<string> PartialNudityLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "FEMALE_BREAST_COVERED",
        "BUTTOCKS_COVERED",
        "FEMALE_GENITALIA_COVERED",
        "MALE_GENITALIA_COVERED"
    };

    /// <summary>
    /// Logger instance for nudity detection diagnostics.
    /// </summary>
    private readonly ILogger<NudityDetectionService> _logger;

    /// <summary>
    /// Media encoder providing the path to the Jellyfin <c>ffmpeg</c> executable.
    /// </summary>
    private readonly IMediaEncoder _mediaEncoder;

    /// <summary>
    /// HTTP client factory used to communicate with the NudeNet detection server.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="NudityDetectionService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="mediaEncoder">The media encoder providing <c>ffmpeg</c>.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public NudityDetectionService(
        ILogger<NudityDetectionService> logger,
        IMediaEncoder mediaEncoder,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _mediaEncoder = mediaEncoder;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Extracts frames from the given <paramref name="video"/> using <c>ffmpeg</c>, submits them
    /// in batches to the configured NudeNet endpoint, and returns merged <see cref="FilterCue"/>s.
    /// </summary>
    /// <param name="video">The Jellyfin video item to scan.</param>
    /// <param name="cancellationToken">Cancellation token to abort frame extraction or HTTP requests.</param>
    /// <returns>A list of merged <see cref="FilterCue"/> instances detected in the video.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <c>ffmpeg</c> or the NudeNet API fails.</exception>
    public async Task<List<FilterCue>> DetectNudityCuesAsync(
        Video video,
        CancellationToken cancellationToken)
    {
        if (video == null || string.IsNullOrWhiteSpace(video.Path) || !File.Exists(video.Path))
        {
            _logger.LogWarning("Cannot run NudeNet scan: video file does not exist on disk.");
            return [];
        }

        var config = Plugin.Instance?.Configuration;
        var apiUrl = config?.NudeNetApiUrl;
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            _logger.LogWarning("NudeNet API URL is not configured.");
            return [];
        }

        var fps = config?.NudityScanFps is > 0 ? config.NudityScanFps : 0.5;
        var threshold = config?.NudityConfidenceThreshold is > 0 and <= 1.0 ? config.NudityConfidenceThreshold : 0.60;
        var detectPartial = config?.NudityDetectPartial ?? false;
        var mergeGap = TimeSpan.FromSeconds(Math.Max(0, config?.NudityMergeGapSeconds ?? 4.0));
        var preRoll = TimeSpan.FromSeconds(Math.Max(0, config?.NudityPreRollSeconds ?? 0.5));
        var postRoll = TimeSpan.FromSeconds(Math.Max(0, config?.NudityPostRollSeconds ?? 0.5));
        var action = string.IsNullOrWhiteSpace(config?.NudityCueAction) ? "skip" : config.NudityCueAction;

        var maxDebugSeconds = _logger.IsEnabled(LogLevel.Debug) && (config?.DebugScanMaxSeconds ?? 0) > 0
            ? config!.DebugScanMaxSeconds
            : 0;

        var tempDir = Path.Combine(Path.GetTempPath(), $"cf_nudenet_{video.Id:N}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await ExtractFramesAsync(video.Path, tempDir, fps, maxDebugSeconds, cancellationToken).ConfigureAwait(false);

            var frameFiles = Directory.GetFiles(tempDir, "*.jpg")
                .OrderBy(static f => f, StringComparer.Ordinal)
                .ToArray();

            if (frameFiles.Length == 0)
            {
                _logger.LogWarning("ffmpeg extracted 0 frames for NudeNet scan of {Path}.", video.Path);
                return [];
            }

            _logger.LogInformation("Extracted {Count} frames at {Fps} fps for NudeNet scan of \"{Name}\".", frameFiles.Length, fps, video.Name);

            var rawCues = new List<FilterCue>();
            var frameDuration = TimeSpan.FromSeconds(1.0 / fps);

            for (int offset = 0; offset < frameFiles.Length; offset += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batchPaths = frameFiles.Skip(offset).Take(BatchSize).ToArray();
                var batchResponse = await SendBatchAsync(apiUrl, config?.NudeNetApiKey, batchPaths, cancellationToken).ConfigureAwait(false);

                for (int i = 0; i < batchResponse.Count; i++)
                {
                    var frameIndex = offset + i;
                    var frameStart = TimeSpan.FromSeconds(frameIndex / fps);
                    var frameEnd = frameStart + frameDuration;

                    var detections = batchResponse[i].Detections;
                    if (detections == null || detections.Count == 0)
                    {
                        continue;
                    }

                    // Filter detections meeting the confidence threshold and enabled category rules
                    var matchedLabels = new List<string>();
                    bool hasFull = false;
                    bool hasPartial = false;

                    foreach (var det in detections)
                    {
                        if (det.Score < threshold || string.IsNullOrWhiteSpace(det.Class))
                        {
                            continue;
                        }

                        if (FullNudityLabels.Contains(det.Class))
                        {
                            hasFull = true;
                            matchedLabels.Add($"{det.Class} ({det.Score:P0})");
                        }
                        else if (detectPartial && PartialNudityLabels.Contains(det.Class))
                        {
                            hasPartial = true;
                            matchedLabels.Add($"{det.Class} ({det.Score:P0})");
                        }
                    }

                    if (!hasFull && !hasPartial)
                    {
                        continue;
                    }

                    var category = hasFull ? "SexAndNudity.FullNudity" : "SexAndNudity.PartialNudity";
                    var paddedStart = frameStart > preRoll ? frameStart - preRoll : TimeSpan.Zero;
                    var paddedEnd = frameEnd + postRoll;

                    rawCues.Add(new FilterCue
                    {
                        Start = paddedStart,
                        End = paddedEnd,
                        Category = category,
                        Channel = "video",
                        Action = action,
                        Precision = "nudenet",
                        Description = $"NudeNet: {string.Join(", ", matchedLabels.Distinct())}"
                    });
                }
            }

            return MergeCues(rawCues, mergeGap);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete temporary NudeNet frame directory {TempDir}", tempDir);
            }
        }
    }

    /// <summary>
    /// Invokes Jellyfin's <c>ffmpeg</c> binary to extract JPEG frames at the configured FPS.
    /// </summary>
    /// <param name="mediaPath">Path to the video file.</param>
    /// <param name="tempDir">Temporary directory to store extracted <c>.jpg</c> frames.</param>
    /// <param name="fps">Frame extraction rate in frames per second.</param>
    /// <param name="maxSeconds">Optional duration cap in seconds (0 for full video).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ExtractFramesAsync(string mediaPath, string tempDir, double fps, int maxSeconds, CancellationToken ct)
    {
        var encoderPath = _mediaEncoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(encoderPath) || !File.Exists(encoderPath))
        {
            throw new InvalidOperationException($"Jellyfin ffmpeg executable not found at: {encoderPath}");
        }

        var outputPattern = Path.Combine(tempDir, "%06d.jpg");
        var durationArg = maxSeconds > 0
            ? FormattableString.Invariant($"-t {maxSeconds} ")
            : string.Empty;

        // Scale width to 640px (-2 preserves even height) for fast NudeNet 320n/640m inference.
        // Try CUDA hardware-accelerated decode first; automatically fall back to CPU if unsupported.
        var cpuArgs = FormattableString.Invariant($"{durationArg}-i \"{mediaPath}\" -vf fps={fps:0.###},scale=640:-2 -q:v 3 \"{outputPattern}\" -y");
        var cudaArgs = FormattableString.Invariant($"-hwaccel cuda {cpuArgs}");

        var (cudaExit, cudaStderr) = await RunFfmpegAsync(encoderPath, cudaArgs, ct).ConfigureAwait(false);
        if (cudaExit == 0)
        {
            return;
        }

        _logger.LogDebug("ffmpeg CUDA decode exited {Code}; falling back to CPU decode. stderr: {Stderr}", cudaExit, cudaStderr);
        // Clean any partial frames before retrying on CPU
        foreach (var partial in Directory.GetFiles(tempDir, "*.jpg"))
        {
            try { File.Delete(partial); } catch { /* best-effort */ }
        }

        var (cpuExit, cpuStderr) = await RunFfmpegAsync(encoderPath, cpuArgs, ct).ConfigureAwait(false);
        if (cpuExit != 0)
        {
            throw new InvalidOperationException($"ffmpeg frame extraction failed (exit {cpuExit}): {cpuStderr}");
        }
    }

    /// <summary>
    /// Executes <c>ffmpeg</c> with the specified argument string and returns its exit code and stderr output.
    /// </summary>
    /// <param name="encoderPath">Full path to the <c>ffmpeg</c> executable.</param>
    /// <param name="args">Command-line arguments passed to <c>ffmpeg</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of <c>(ExitCode, Stderr)</c>.</returns>
    private async Task<(int ExitCode, string Stderr)> RunFfmpegAsync(string encoderPath, string args, CancellationToken ct)
    {
        _logger.LogDebug("NudeNet ffmpeg command: {Bin} {Args}", encoderPath, args);

        var startInfo = new ProcessStartInfo
        {
            FileName = encoderPath,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start ffmpeg process for NudeNet frame extraction.");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stderr);
    }

    /// <summary>
    /// Uploads a batch of JPEG frame files to the NudeNet <c>/detect</c> HTTP endpoint.
    /// </summary>
    /// <param name="apiUrl">The NudeNet <c>/detect</c> endpoint URL.</param>
    /// <param name="apiKey">Optional Bearer API key.</param>
    /// <param name="framePaths">Array of local JPEG file paths to upload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered list of per-frame detection results matching <paramref name="framePaths"/>.</returns>
    private async Task<List<NudeNetFrameResult>> SendBatchAsync(
        string apiUrl,
        string? apiKey,
        string[] framePaths,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(nameof(NudityDetectionService));

        using var form = new MultipartFormDataContent();
        var streams = new List<FileStream>(framePaths.Length);

        try
        {
            foreach (var path in framePaths)
            {
                var stream = File.OpenRead(path);
                streams.Add(stream);
                var fileContent = new StreamContent(stream);
                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");
                form.Add(fileContent, "files", Path.GetFileName(path));
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = form
            };

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            }

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"NudeNet API returned {(int)response.StatusCode}: {body}");
            }

            var parsed = JsonSerializer.Deserialize<NudeNetBatchResponse>(body, JsonOptions);
            return parsed?.Results ?? [];
        }
        finally
        {
            foreach (var s in streams)
            {
                await s.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Merges adjacent or overlapping nudity cues of the same category within <paramref name="mergeGap"/>.
    /// </summary>
    /// <param name="cues">Raw per-frame cues.</param>
    /// <param name="mergeGap">Maximum gap between adjacent cues to merge.</param>
    /// <returns>Sorted list of merged <see cref="FilterCue"/> intervals.</returns>
    internal static List<FilterCue> MergeCues(List<FilterCue> cues, TimeSpan mergeGap)
    {
        if (cues.Count <= 1)
        {
            return cues;
        }

        var ordered = cues.OrderBy(static c => c.Start).ThenBy(static c => c.End).ToList();
        var merged = new List<FilterCue> { ordered[0] };

        for (int i = 1; i < ordered.Count; i++)
        {
            var prev = merged[^1];
            var curr = ordered[i];

            if (string.Equals(prev.Category, curr.Category, StringComparison.Ordinal)
                && curr.Start - prev.End <= mergeGap)
            {
                if (curr.End > prev.End)
                {
                    prev.End = curr.End;
                }
            }
            else
            {
                merged.Add(curr);
            }
        }

        return merged;
    }

    /// <summary>
    /// JSON response wrapper returned by the NudeNet <c>/detect</c> endpoint.
    /// </summary>
    private sealed class NudeNetBatchResponse
    {
        /// <summary>Gets or sets the per-frame detection results.</summary>
        public List<NudeNetFrameResult> Results { get; set; } = [];
    }

    /// <summary>
    /// Per-frame detection result returned by the NudeNet <c>/detect</c> endpoint.
    /// </summary>
    private sealed class NudeNetFrameResult
    {
        /// <summary>Gets or sets the uploaded frame filename.</summary>
        public string Filename { get; set; } = string.Empty;

        /// <summary>Gets or sets the list of body-part detections in this frame.</summary>
        public List<NudeNetDetection> Detections { get; set; } = [];
    }

    /// <summary>
    /// Single NudeNet bounding-box detection entry.
    /// </summary>
    private sealed class NudeNetDetection
    {
        /// <summary>Gets or sets the NudeNet class label (e.g. <c>FEMALE_BREAST_EXPOSED</c>).</summary>
        public string Class { get; set; } = string.Empty;

        /// <summary>Gets or sets the detection confidence score (0.0 to 1.0).</summary>
        public double Score { get; set; }
    }
}
