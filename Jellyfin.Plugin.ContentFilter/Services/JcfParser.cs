using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ContentFilter.Models;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Parses JCF and legacy MCF WEBVTT payloads into <see cref="JcfFilter"/> instances.
/// </summary>
public static class JcfParser
{
    private static readonly Regex TimecodeRegex = new(
        @"^(\d+):(\d{2}):(\d{2})\.(\d{3}) --> (\d+):(\d{2}):(\d{2})\.(\d{3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses a reader containing JCF or legacy MCF content.
    /// </summary>
    /// <param name="reader">The source reader.</param>
    /// <returns>The parsed <see cref="JcfFilter"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="reader"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">Thrown when the content is not a supported WEBVTT filter file.</exception>
    public static JcfFilter Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lines.Add(line);
        }

        var index = 0;
        while (index < lines.Count && string.IsNullOrWhiteSpace(lines[index]))
        {
            index++;
        }

        if (index >= lines.Count || !lines[index].StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            throw new FormatException("JCF file must start with WEBVTT.");
        }

        var header = lines[index];
        var isClassicMcf = header.Contains("MovieContentFilter", StringComparison.OrdinalIgnoreCase);
        index++;

        var result = new JcfFilter
        {
            Cues = [],
        };

        while (index < lines.Count)
        {
            while (index < lines.Count && string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
            }

            if (index >= lines.Count)
            {
                break;
            }

            var currentLine = lines[index];
            if (currentLine.Equals("NOTE", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                ParseNoteBlock(lines, ref index, result);
                continue;
            }

            if (!TryParseTimecode(currentLine, out var start, out var end))
            {
                index++;
                continue;
            }

            index++;
            var payloadLines = new List<string>();
            while (index < lines.Count && !string.IsNullOrWhiteSpace(lines[index]))
            {
                payloadLines.Add(lines[index]);
                index++;
            }

            if (isClassicMcf)
            {
                ParseClassicMcfCueLines(payloadLines, start, end, result.Cues);
            }
            else
            {
                ParseJcfCueLines(payloadLines, start, end, result.Cues);
            }
        }

        return result;
    }

    private static void ParseNoteBlock(IReadOnlyList<string> lines, ref int index, JcfFilter filter)
    {
        while (index < lines.Count && !string.IsNullOrWhiteSpace(lines[index]))
        {
            var line = lines[index];
            if (line.StartsWith("TITLE ", StringComparison.OrdinalIgnoreCase))
            {
                filter.Title = line["TITLE ".Length..].Trim();
            }
            else if (line.StartsWith("YEAR ", StringComparison.OrdinalIgnoreCase))
            {
                filter.Year = line["YEAR ".Length..].Trim();
            }
            else if (line.StartsWith("IMDB ", StringComparison.OrdinalIgnoreCase))
            {
                filter.ImdbUrl = line["IMDB ".Length..].Trim();
            }
            else if (line.StartsWith("SOURCE ", StringComparison.OrdinalIgnoreCase))
            {
                filter.Source = line["SOURCE ".Length..].Trim();
            }

            index++;
        }
    }

    private static void ParseJcfCueLines(
        IReadOnlyList<string> payloadLines,
        TimeSpan start,
        TimeSpan end,
        ICollection<FilterCue> cues)
    {
        string? description = null;
        string? category = null;
        var channel = "both";
        var action = "none";

        foreach (var payloadLine in payloadLines)
        {
            var separatorIndex = payloadLine.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = payloadLine[..separatorIndex].Trim();
            var value = payloadLine[(separatorIndex + 1)..].Trim();
            if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
            {
                description = value;
            }
            else if (key.Equals("category", StringComparison.OrdinalIgnoreCase))
            {
                category = value;
            }
            else if (key.Equals("channel", StringComparison.OrdinalIgnoreCase))
            {
                channel = string.IsNullOrWhiteSpace(value) ? "both" : value;
            }
            else if (key.Equals("action", StringComparison.OrdinalIgnoreCase))
            {
                action = string.IsNullOrWhiteSpace(value) ? "none" : value;
            }
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            return;
        }

        cues.Add(new FilterCue
        {
            Start = start,
            End = end,
            Description = description,
            Category = category,
            Channel = channel,
            Action = action,
        });
    }

    private static void ParseClassicMcfCueLines(
        IReadOnlyList<string> payloadLines,
        TimeSpan start,
        TimeSpan end,
        ICollection<FilterCue> cues)
    {
        foreach (var payloadLine in payloadLines)
        {
            if (string.IsNullOrWhiteSpace(payloadLine))
            {
                continue;
            }

            var parts = payloadLine.Split('=', StringSplitOptions.TrimEntries);
            var category = parts.Length > 0 ? parts[0] : string.Empty;
            if (string.IsNullOrWhiteSpace(category))
            {
                continue;
            }

            var channel = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : "both";
            cues.Add(new FilterCue
            {
                Start = start,
                End = end,
                Category = category,
                Channel = channel,
                Action = "skip",
            });
        }
    }

    private static bool TryParseTimecode(string value, out TimeSpan start, out TimeSpan end)
    {
        start = default;
        end = default;

        var match = TimecodeRegex.Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        start = ParseTimestamp(match, 1);
        end = ParseTimestamp(match, 5);
        return true;
    }

    private static TimeSpan ParseTimestamp(Match match, int offset)
    {
        var hours = int.Parse(match.Groups[offset].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[offset + 1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[offset + 2].Value, CultureInfo.InvariantCulture);
        var milliseconds = int.Parse(match.Groups[offset + 3].Value, CultureInfo.InvariantCulture);
        return new TimeSpan(0, hours, minutes, seconds, milliseconds);
    }
}
