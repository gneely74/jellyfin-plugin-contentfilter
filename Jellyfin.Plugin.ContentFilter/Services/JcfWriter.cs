using System.Globalization;
using System.Text;
using Jellyfin.Plugin.ContentFilter.Models;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Serializes <see cref="JcfFilter"/> instances to JCF WEBVTT content.
/// </summary>
public static class JcfWriter
{
    /// <summary>
    /// Serializes a filter into JCF text.
    /// </summary>
    /// <param name="filter">The filter to serialize.</param>
    /// <returns>The serialized JCF string.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="filter"/> is <see langword="null"/>.</exception>
    public static string Serialize(JcfFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var builder = new StringBuilder();
        builder.AppendLine("WEBVTT JellyfinContentFilter 1.0.0");
        builder.AppendLine();

        var hasMetadata =
            !string.IsNullOrWhiteSpace(filter.Title)
            || !string.IsNullOrWhiteSpace(filter.Year)
            || !string.IsNullOrWhiteSpace(filter.ImdbUrl)
            || !string.IsNullOrWhiteSpace(filter.Source);

        if (hasMetadata)
        {
            builder.AppendLine("NOTE");

            if (!string.IsNullOrWhiteSpace(filter.Title))
            {
                builder.AppendLine($"TITLE {filter.Title}");
            }

            if (!string.IsNullOrWhiteSpace(filter.Year))
            {
                builder.AppendLine($"YEAR {filter.Year}");
            }

            if (!string.IsNullOrWhiteSpace(filter.ImdbUrl))
            {
                builder.AppendLine($"IMDB {filter.ImdbUrl}");
            }

            if (!string.IsNullOrWhiteSpace(filter.Source))
            {
                builder.AppendLine($"SOURCE {filter.Source}");
            }
        }

        foreach (var cue in filter.Cues)
        {
            builder.AppendLine();
            builder.AppendLine($"{FormatTs(cue.Start)} --> {FormatTs(cue.End)}");

            if (!string.IsNullOrWhiteSpace(cue.Description))
            {
                builder.AppendLine($"description: {cue.Description}");
            }

            builder.AppendLine($"category: {cue.Category}");
            builder.AppendLine($"channel: {cue.Channel}");
            builder.AppendLine($"action: {cue.Action}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats a timestamp in JCF cue format.
    /// </summary>
    /// <param name="value">The timestamp value.</param>
    /// <returns>The formatted timestamp.</returns>
    public static string FormatTs(TimeSpan value)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}");
    }
}
