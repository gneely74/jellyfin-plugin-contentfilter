using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ContentFilter.Models;

/// <summary>
/// Represents the state of a content scan.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScanState
{
    /// <summary>
    /// No scan is running.
    /// </summary>
    Idle,

    /// <summary>
    /// A scan is in progress.
    /// </summary>
    Scanning,

    /// <summary>
    /// The scan completed successfully.
    /// </summary>
    Complete,

    /// <summary>
    /// The scan was cancelled.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The scan failed.
    /// </summary>
    Error,
}

/// <summary>
/// Represents scan status for a media item.
/// </summary>
public sealed class ScanStatus
{
    /// <summary>
    /// Gets or sets the item identifier.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the current scan state.
    /// </summary>
    public ScanState State { get; set; } = ScanState.Idle;

    /// <summary>
    /// Gets or sets the scan progress from 0.0 to 1.0.
    /// </summary>
    public double Progress { get; set; }

    /// <summary>
    /// Gets or sets the current time marker.
    /// </summary>
    public string CurrentTime { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of cues found.
    /// </summary>
    public int CuesFound { get; set; }

    /// <summary>
    /// Gets or sets the error message, when applicable.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets the scan start timestamp.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// Gets or sets the scan completion timestamp.
    /// </summary>
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Represents a queued scan request.
/// </summary>
public sealed class ScanJob
{
    /// <summary>
    /// Gets or sets the item identifier.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the cancellation token source for the scan.
    /// </summary>
    public CancellationTokenSource Cts { get; init; } = new();
}

/// <summary>
/// Represents a cue DTO used by HTTP APIs.
/// </summary>
public sealed class CueDto
{
    /// <summary>
    /// Gets or sets the cue key.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cue start time string.
    /// </summary>
    public string Start { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cue end time string.
    /// </summary>
    public string End { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the cue category.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cue channel.
    /// </summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cue action.
    /// </summary>
    public string Action { get; set; } = string.Empty;
}
