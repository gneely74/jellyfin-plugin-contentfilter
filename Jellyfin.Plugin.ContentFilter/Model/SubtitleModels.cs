using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ContentFilter.Models;

/// <summary>
/// Lifecycle state of a subtitle sync job.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleSyncState
{
    /// <summary>No sync is running.</summary>
    Idle,

    /// <summary>Sync is currently running.</summary>
    Running,

    /// <summary>Sync completed successfully.</summary>
    Completed,

    /// <summary>Sync was cancelled by user.</summary>
    Cancelled,

    /// <summary>Sync encountered an error.</summary>
    Error
}

/// <summary>
/// Status report for library-wide automated subtitle download and clean sync.
/// </summary>
public sealed class SubtitleSyncStatus
{
    /// <summary>Gets a value indicating whether a sync is currently in progress.</summary>
    public bool IsRunning => State == SubtitleSyncState.Running;

    /// <summary>Gets or sets the current sync state.</summary>
    public SubtitleSyncState State { get; set; } = SubtitleSyncState.Idle;

    /// <summary>Gets or sets the title of the item currently being processed.</summary>
    public string? CurrentItemName { get; set; }

    /// <summary>Gets or sets the item ID currently being processed.</summary>
    public Guid? CurrentItemId { get; set; }

    /// <summary>Gets or sets the number of items processed so far.</summary>
    public int ProcessedItems { get; set; }

    /// <summary>Gets or sets the total number of items to process.</summary>
    public int TotalItems { get; set; }

    /// <summary>Gets or sets the count of subtitles successfully downloaded from remote providers.</summary>
    public int SubtitlesDownloaded { get; set; }

    /// <summary>Gets or sets the count of clean subtitles generated.</summary>
    public int SubtitlesCleaned { get; set; }

    /// <summary>Gets or sets the count of items skipped (e.g. locked or already up-to-date).</summary>
    public int SubtitlesSkipped { get; set; }

    /// <summary>Gets or sets the count of processing errors encountered.</summary>
    public int ErrorCount { get; set; }

    /// <summary>Gets or sets the progress percentage (0 to 100).</summary>
    public double ProgressPercentage { get; set; }

    /// <summary>Gets or sets the job start timestamp.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Gets or sets the job completion timestamp.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets the last error message, if any.</summary>
    public string? LastErrorMessage { get; set; }

    /// <summary>Gets or sets recent log messages for display in the UI.</summary>
    public List<string> RecentLogs { get; set; } = [];

    /// <summary>Gets or sets the number of pending items in the new media processing queue.</summary>
    public int PendingNewMediaQueueCount { get; set; }

    /// <summary>Gets or sets the start timestamp of the last run.</summary>
    public DateTime? LastRunStartedAt { get; set; }

    /// <summary>Gets or sets the completion timestamp of the last run.</summary>
    public DateTime? LastRunCompletedAt { get; set; }

    /// <summary>Gets or sets the outcome state of the last run.</summary>
    public SubtitleSyncState? LastRunStatus { get; set; }

    /// <summary>Gets or sets the total items processed in the last run.</summary>
    public int? LastRunProcessedCount { get; set; }

    /// <summary>Gets or sets the error count in the last run.</summary>
    public int? LastRunErrorCount { get; set; }

    /// <summary>Gets or sets the next scheduled automated run timestamp, if scheduled.</summary>
    public DateTime? NextScheduledRun { get; set; }
}

/// <summary>
/// Result of processing subtitles for a single video item.
/// </summary>
public sealed class SingleSubtitleProcessResult
{
    /// <summary>Gets or sets the item identifier.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets a value indicating whether a remote subtitle was downloaded.</summary>
    public bool Downloaded { get; set; }

    /// <summary>Gets or sets a value indicating whether audio was transcribed via local AI / Whisper.</summary>
    public bool Transcribed { get; set; }

    /// <summary>Gets or sets a value indicating whether clean subtitles were generated.</summary>
    public bool Cleaned { get; set; }

    /// <summary>Gets or sets a value indicating whether the item was skipped (e.g. locked or already clean).</summary>
    public bool Skipped { get; set; }

    /// <summary>Gets or sets the path of the generated filtered subtitle stream.</summary>
    public string? FilteredPath { get; set; }

    /// <summary>Gets or sets the path of the generated unfiltered subtitle stream.</summary>
    public string? UnfilteredPath { get; set; }

    /// <summary>Gets or sets the count of profanity mute cues generated.</summary>
    public int CuesAdded { get; set; }

    /// <summary>Gets or sets an error message if processing failed.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Subtitle override settings for an individual media item.
/// </summary>
public sealed class SubtitleOverrideInfo
{
    /// <summary>Gets or sets the item ID.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the time offset in seconds.</summary>
    public double OffsetSeconds { get; set; }

    /// <summary>Gets or sets the time offset in milliseconds.</summary>
    public int OffsetMs { get; set; }

    /// <summary>Gets or sets a value indicating whether this item's subtitle is locked against automated sync overwrites.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Gets or sets the selected source or edition description.</summary>
    public string? SelectedSource { get; set; }

    /// <summary>Gets or sets the last updated timestamp.</summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Representation of a remote subtitle candidate returned from Jellyfin subtitle providers.
/// </summary>
public sealed class RemoteSubtitleDto
{
    /// <summary>Gets or sets the remote subtitle identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the subtitle or release name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider name (e.g. OpenSubtitles).</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>Gets or sets the format (e.g. srt).</summary>
    public string Format { get; set; } = "srt";

    /// <summary>Gets or sets the 3-letter language code.</summary>
    public string Language { get; set; } = "eng";

    /// <summary>Gets or sets a value indicating whether this subtitle was a cryptographic hash match.</summary>
    public bool IsHashMatch { get; set; }

    /// <summary>Gets or sets the community rating, if available.</summary>
    public float? CommunityRating { get; set; }

    /// <summary>Gets or sets the total download count, if available.</summary>
    public int? DownloadCount { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a forced subtitle track.</summary>
    public bool IsForced { get; set; }

    /// <summary>Gets or sets a value indicating whether this subtitle is for the hearing impaired.</summary>
    public bool IsHearingImpaired { get; set; }
}

/// <summary>
/// Request payload for shifting an item's subtitle timing.
/// </summary>
public sealed class SubtitleShiftRequest
{
    /// <summary>Gets or sets the offset in seconds (positive or negative).</summary>
    public double OffsetSeconds { get; set; }
}

/// <summary>
/// Request payload for locking/unlocking an item's subtitle against automated sync.
/// </summary>
public sealed class SubtitleLockRequest
{
    /// <summary>Gets or sets a value indicating whether the item is locked.</summary>
    public bool IsLocked { get; set; }
}

/// <summary>
/// Request payload for downloading a specific remote subtitle candidate.
/// </summary>
public sealed class RemoteSubtitleDownloadRequest
{
    /// <summary>Gets or sets the provider's subtitle ID.</summary>
    public required string SubtitleId { get; set; }
}

/// <summary>
/// Request payload for initiating an on-demand subtitle sync run.
/// </summary>
public sealed class StartSubtitleSyncRequest
{
    /// <summary>Gets or sets a value indicating whether to reprocess items that already have clean subtitles.</summary>
    public bool ForceAll { get; set; }

    /// <summary>Gets or sets an optional language override.</summary>
    public string? Language { get; set; }
}

/// <summary>
/// Record of a completed local transcription for a media item.
/// Used to detect media file upgrades, replacements, and avoid redundant re-transcriptions.
/// </summary>
public sealed class ItemTranscriptionRecord
{
    /// <summary>Gets or sets the item identifier.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the physical file path of the media file at the time of transcription.</summary>
    public string MediaPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the file size in bytes at the time of transcription.</summary>
    public long FileSize { get; set; }

    /// <summary>Gets or sets the UTC last write time of the media file at the time of transcription.</summary>
    public DateTime LastModifiedUtc { get; set; }

    /// <summary>Gets or sets the UTC timestamp when transcription completed.</summary>
    public DateTime TranscribedAt { get; set; }

    /// <summary>Gets or sets the Whisper model used.</summary>
    public string? Model { get; set; }

    /// <summary>Gets or sets the count of profanity mute cues generated.</summary>
    public int CueCount { get; set; }
}

/// <summary>
/// Result of an audio transcription operation.
/// </summary>
public sealed class TranscriptionResult
{
    /// <summary>Gets or sets the item identifier.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the unfilitered raw subtitle content (SRT format).</summary>
    public string UnfilteredSrt { get; set; } = string.Empty;

    /// <summary>Gets or sets the filtered/cleaned subtitle content (SRT format).</summary>
    public string FilteredSrt { get; set; } = string.Empty;

    /// <summary>Gets or sets the detected spoken language code.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Gets or sets the audio duration in seconds.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Gets or sets the word-level timestamp entries (if available from verbose_json).</summary>
    public List<WhisperWordDto> Words { get; set; } = [];

    /// <summary>Gets or sets the transcribed segments.</summary>
    public List<WhisperSegmentDto> Segments { get; set; } = [];

    /// <summary>Gets or sets the execution time in milliseconds.</summary>
    public long ExecutionTimeMs { get; set; }
}

/// <summary>
/// DTO representing the response from an OpenAI-compatible Whisper /v1/audio/transcriptions endpoint.
/// </summary>
public sealed class WhisperResponseDto
{
    /// <summary>Gets or sets the full transcribed text.</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the detected language.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>Gets or sets the duration in seconds.</summary>
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    /// <summary>Gets or sets the segments.</summary>
    [JsonPropertyName("segments")]
    public List<WhisperSegmentDto>? Segments { get; set; }

    /// <summary>Gets or sets word-level timestamps if returned at top-level.</summary>
    [JsonPropertyName("words")]
    public List<WhisperWordDto>? Words { get; set; }
}

/// <summary>
/// DTO representing a transcribed segment with start/end timestamps.
/// </summary>
public sealed class WhisperSegmentDto
{
    /// <summary>Gets or sets the segment identifier.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the start time in seconds.</summary>
    [JsonPropertyName("start")]
    public double Start { get; set; }

    /// <summary>Gets or sets the end time in seconds.</summary>
    [JsonPropertyName("end")]
    public double End { get; set; }

    /// <summary>Gets or sets the segment dialogue text.</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the word-level timestamps within this segment.</summary>
    [JsonPropertyName("words")]
    public List<WhisperWordDto>? Words { get; set; }
}

/// <summary>
/// DTO representing an individual transcribed word with exact start/end timestamps.
/// </summary>
public sealed class WhisperWordDto
{
    /// <summary>Gets or sets the word text.</summary>
    [JsonPropertyName("word")]
    public string Word { get; set; } = string.Empty;

    /// <summary>Gets or sets the start time in seconds.</summary>
    [JsonPropertyName("start")]
    public double Start { get; set; }

    /// <summary>Gets or sets the end time in seconds.</summary>
    [JsonPropertyName("end")]
    public double End { get; set; }

    /// <summary>Gets or sets the confidence probability if provided.</summary>
    [JsonPropertyName("probability")]
    public double? Probability { get; set; }
}
