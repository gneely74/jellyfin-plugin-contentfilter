namespace Jellyfin.Plugin.ContentFilter.Models;

/// <summary>
/// Represents a single cue in a Jellyfin Content Filter (JCF) file.
/// </summary>
public sealed class FilterCue
{
    /// <summary>
    /// Gets or sets the cue start time.
    /// </summary>
    public TimeSpan Start { get; set; }

    /// <summary>
    /// Gets or sets the cue end time.
    /// </summary>
    public TimeSpan End { get; set; }

    /// <summary>
    /// Gets or sets an optional human-readable description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the cue category key.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the affected channel.
    /// </summary>
    public string Channel { get; set; } = "both";

    /// <summary>
    /// Gets or sets the cue action.
    /// </summary>
    public string Action { get; set; } = "none";

    /// <summary>
    /// Gets the unique cue key composed of time range and category.
    /// </summary>
    public string Key => $"{Start:hh\\:mm\\:ss\\.fff}-{End:hh\\:mm\\:ss\\.fff}-{Category}";
}

/// <summary>
/// Represents a full Jellyfin Content Filter (JCF) document.
/// </summary>
public sealed class JcfFilter
{
    /// <summary>
    /// Gets or sets the media title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the release year.
    /// </summary>
    public string Year { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the IMDb URL.
    /// </summary>
    public string ImdbUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the attribution source.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of cues.
    /// </summary>
    public List<FilterCue> Cues { get; init; } = [];
}
