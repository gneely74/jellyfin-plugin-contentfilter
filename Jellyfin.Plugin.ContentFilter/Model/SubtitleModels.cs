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
