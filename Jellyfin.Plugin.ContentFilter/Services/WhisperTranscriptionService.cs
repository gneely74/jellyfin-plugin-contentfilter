using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ContentFilter.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Service responsible for extracting audio from video media and generating accurate speech-to-text
/// transcriptions and word-level timestamps via an OpenAI-compatible Whisper API.
/// </summary>
public class WhisperTranscriptionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<WhisperTranscriptionService> _logger;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="WhisperTranscriptionService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="mediaEncoder">The media encoder (ffmpeg) provider.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public WhisperTranscriptionService(
        ILogger<WhisperTranscriptionService> logger,
        IMediaEncoder mediaEncoder,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _mediaEncoder = mediaEncoder;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Extracts the optimal audio track from a video and transcribes it via the configured Whisper API endpoint.
    /// </summary>
    /// <param name="video">The video media item.</param>
    /// <param name="targetLanguage">The target language ISO code (e.g. "en" or "eng").</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="TranscriptionResult"/> on success, or <see langword="null"/> on failure.</returns>
    public async Task<TranscriptionResult?> TranscribeVideoAsync(
        Video video,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (video == null || string.IsNullOrWhiteSpace(video.Path) || !File.Exists(video.Path))
        {
            _logger.LogWarning("Cannot transcribe video: file does not exist on disk.");
            return null;
        }

        var config = Plugin.Instance?.Configuration;
        var apiUrl = config?.TranscriptionApiUrl;
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            _logger.LogWarning("Whisper transcription API URL is not configured.");
            return null;
        }

        var model = !string.IsNullOrWhiteSpace(config?.TranscriptionModel)
            ? config.TranscriptionModel
            : "whisper-1";

        var lang2 = SubtitleFilter.ToTwoLetterLanguage(targetLanguage);
        var sw = Stopwatch.StartNew();

        // 1. Select optimal audio stream
        var audioStream = SelectOptimalAudioStream(video, lang2);
        var audioIndex = audioStream?.Index ?? 1;

        // 2. Extract 16kHz mono WAV to temporary file
        var tempWavPath = Path.Combine(Path.GetTempPath(), $"whisper_{video.Id:N}_{Guid.NewGuid():N}.wav");
        try
        {
            _logger.LogInformation("Extracting 16kHz mono audio from \"{VideoPath}\" stream index {Index} to \"{TempPath}\"...",
                video.Path, audioIndex, tempWavPath);

            var extracted = await ExtractAudioWavAsync(video.Path, audioIndex, tempWavPath, cancellationToken).ConfigureAwait(false);
            if (!extracted || !File.Exists(tempWavPath) || new FileInfo(tempWavPath).Length == 0)
            {
                _logger.LogError("Failed to extract audio track from video {ItemId} ({Name})", video.Id, video.Name);
                return null;
            }

            var audioFileInfo = new FileInfo(tempWavPath);
            _logger.LogInformation("Audio extracted ({SizeMb:F2} MB). Posting to Whisper API ({ApiUrl}, model: {Model})...",
                audioFileInfo.Length / (1024.0 * 1024.0), apiUrl, model);

            // 3. Post to OpenAI-compatible /v1/audio/transcriptions endpoint
            var responseDto = await CallWhisperApiAsync(apiUrl, model, lang2, tempWavPath, config?.TranscriptionApiKey, cancellationToken).ConfigureAwait(false);
            if (responseDto == null)
            {
                _logger.LogError("Whisper API returned an empty or invalid response for {ItemId}", video.Id);
                return null;
            }

            sw.Stop();
            _logger.LogInformation("Whisper transcription completed in {ElapsedMs}ms for \"{ItemName}\"", sw.ElapsedMilliseconds, video.Name);

            // 4. Build segments and word timestamps
            var segments = responseDto.Segments ?? [];
            var words = new List<WhisperWordDto>();

            if (responseDto.Words != null && responseDto.Words.Count > 0)
            {
                words.AddRange(responseDto.Words);
            }
            else
            {
                foreach (var seg in segments)
                {
                    if (seg.Words != null && seg.Words.Count > 0)
                    {
                        words.AddRange(seg.Words);
                    }
                }
            }

            // 5. Build raw unfiltered SRT
            var srt = ConvertSegmentsToSrt(segments);
            if (string.IsNullOrWhiteSpace(srt) && !string.IsNullOrWhiteSpace(responseDto.Text))
            {
                srt = $"1\r\n00:00:00,000 --> 00:00:10,000\r\n{responseDto.Text.Trim()}\r\n\r\n";
            }

            return new TranscriptionResult
            {
                ItemId = video.Id,
                UnfilteredSrt = srt,
                Language = responseDto.Language ?? lang2,
                DurationSeconds = responseDto.Duration ?? 0,
                Segments = segments,
                Words = words,
                ExecutionTimeMs = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Transcription cancelled for video {ItemId}", video.Id);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Whisper transcription failed for video {ItemId} ({Name})", video.Id, video.Name);
            return null;
        }
        finally
        {
            // 6. Clean up temp WAV file immediately
            try
            {
                if (File.Exists(tempWavPath))
                {
                    File.Delete(tempWavPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to clean up temporary audio file {Path}", tempWavPath);
            }
        }
    }

    /// <summary>
    /// Selects the optimal dialogue audio stream from a video.
    /// Prefers streams matching the target language, avoiding commentary/descriptive tracks,
    /// and preferring higher channel counts and default tracks.
    /// </summary>
    public static MediaStream? SelectOptimalAudioStream(Video video, string targetLanguage)
    {
        var streams = video.GetMediaStreams()
            .Where(s => s.Type == MediaStreamType.Audio)
            .ToList();

        if (streams.Count == 0)
        {
            return null;
        }

        var lang2 = SubtitleFilter.ToTwoLetterLanguage(targetLanguage);

        // Filter out obvious commentary or descriptive audio
        var dialogueStreams = streams.Where(s =>
        {
            var title = (s.Title ?? string.Empty).ToLowerInvariant();
            var commentKeywords = new[] { "commentary", "director", "description", "descriptive", "dvs" };
            return !commentKeywords.Any(title.Contains);
        }).ToList();

        if (dialogueStreams.Count == 0)
        {
            dialogueStreams = streams;
        }

        // 1. Language match
        var matchingLang = dialogueStreams.Where(s =>
        {
            if (string.IsNullOrWhiteSpace(s.Language)) return false;
            var streamLang2 = SubtitleFilter.ToTwoLetterLanguage(s.Language);
            return string.Equals(streamLang2, lang2, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        var candidates = matchingLang.Count > 0 ? matchingLang : dialogueStreams;

        // 2. Prefer Default, then highest Channels
        return candidates
            .OrderByDescending(s => s.IsDefault)
            .ThenByDescending(s => s.Channels ?? 0)
            .FirstOrDefault() ?? streams[0];
    }

    /// <summary>
    /// Extracts audio from the video file into a 16kHz mono 16-bit PCM WAV using Jellyfin's ffmpeg.
    /// </summary>
    private async Task<bool> ExtractAudioWavAsync(
        string videoPath,
        int streamIndex,
        string outputPath,
        CancellationToken ct)
    {
        var encoderPath = _mediaEncoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(encoderPath) || !File.Exists(encoderPath))
        {
            _logger.LogError("Jellyfin ffmpeg encoder executable not found at: {Path}", encoderPath);
            return false;
        }

        // -vn: ignore video
        // -map 0:{streamIndex}: pick selected audio track
        // -ac 1: mono
        // -ar 16000: 16kHz sample rate optimal for Whisper models
        // -c:a pcm_s16le: 16-bit PCM WAV
        var psi = new ProcessStartInfo
        {
            FileName = encoderPath,
            Arguments = $"-v error -y -i \"{videoPath}\" -map 0:{streamIndex} -vn -ac 1 -ar 16000 -c:a pcm_s16le \"{outputPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return false;
            }

            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var errorOutput = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("ffmpeg audio extraction exited with code {Code}: {Error}", proc.ExitCode, errorOutput);
                return false;
            }

            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception running ffmpeg audio extraction on {Path}", videoPath);
            return false;
        }
    }

    /// <summary>
    /// Sends a multipart form-data request to the OpenAI-compatible audio transcriptions API.
    /// </summary>
    private async Task<WhisperResponseDto?> CallWhisperApiAsync(
        string apiUrl,
        string model,
        string language,
        string audioFilePath,
        string? apiKey,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(nameof(WhisperTranscriptionService));

        using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var content = new MultipartFormDataContent();

        // Audio file stream
        using var fileStream = File.OpenRead(audioFilePath);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", Path.GetFileName(audioFilePath));

        // Form parameters
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent("verbose_json"), "response_format");
        if (!string.IsNullOrWhiteSpace(language))
        {
            content.Add(new StringContent(language), "language");
        }

        content.Add(new StringContent("word"), "timestamp_granularities[]");
        content.Add(new StringContent("segment"), "timestamp_granularities[]");

        request.Content = content;

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Whisper API call failed with status {StatusCode}: {Body}", response.StatusCode, responseBody);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WhisperResponseDto>(responseBody, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Whisper API JSON response: {Body}", responseBody);
            return null;
        }
    }

    /// <summary>
    /// Converts a list of transcribed Whisper segments into standard SRT subtitle format.
    /// </summary>
    /// <param name="segments">The Whisper segments.</param>
    /// <returns>A formatted SRT string.</returns>
    public static string ConvertSegmentsToSrt(IReadOnlyList<WhisperSegmentDto> segments)
    {
        if (segments == null || segments.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        int index = 1;

        foreach (var seg in segments)
        {
            var text = seg.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var startStr = FormatSrtTimestamp(seg.Start);
            var endStr = FormatSrtTimestamp(seg.End);

            sb.AppendLine(index.ToString(CultureInfo.InvariantCulture));
            sb.Append(startStr).Append(" --> ").AppendLine(endStr);
            sb.AppendLine(text);
            sb.AppendLine();

            index++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats a time in seconds to standard SRT format (HH:mm:ss,fff).
    /// </summary>
    public static string FormatSrtTimestamp(double seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        var ts = TimeSpan.FromSeconds(seconds);
        return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2},{3:D3}",
            (int)ts.TotalHours,
            ts.Minutes,
            ts.Seconds,
            ts.Milliseconds);
    }
}
