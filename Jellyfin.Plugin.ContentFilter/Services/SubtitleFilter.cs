using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ContentFilter.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Generates filtered subtitle outputs for mute-action cue ranges.
/// </summary>
public class SubtitleFilter
{
    private static readonly Regex SrtTsRegex = new(
        @"^(?<h>\d{2}):(?<m>\d{2}):(?<s>\d{2})[,.](?<ms>\d{3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger<SubtitleFilter> _logger;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleFilter"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="libraryManager">The library manager.</param>
    public SubtitleFilter(ILogger<SubtitleFilter> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    private string SubtitlesPath => Path.Combine(Plugin.Instance!.DataFolderPath, "subtitles");

    /// <summary>
    /// Regenerates filtered subtitle output for an item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="filter">The active filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when regeneration is complete.</returns>
    public async Task RegenerateAsync(Guid itemId, JcfFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var item = _libraryManager.GetItemById(itemId);
        var mediaPath = item?.Path;
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            _logger.LogDebug("No media path available for item {ItemId}; skipping subtitle regeneration.", itemId);
            return;
        }

        var candidatePaths = new[]
        {
            Path.ChangeExtension(mediaPath, ".srt"),
            Path.ChangeExtension(mediaPath, ".en.srt"),
        };

        var srtPath = candidatePaths.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(srtPath))
        {
            _logger.LogDebug("No adjacent SRT subtitle found for item {ItemId}.", itemId);
            return;
        }

        var input = await File.ReadAllTextAsync(srtPath, cancellationToken).ConfigureAwait(false);
        var output = ApplyWordBlanking(input, filter);
        Directory.CreateDirectory(SubtitlesPath);

        var outputPath = GetFilteredSrtPath(itemId);
        await File.WriteAllTextAsync(outputPath, output, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the generated filtered subtitle for an item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    public void DeleteFilteredSubtitle(Guid itemId)
    {
        var path = GetFilteredSrtPath(itemId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Gets the generated filtered subtitle path for an item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <returns>The filtered subtitle path.</returns>
    public string GetFilteredSrtPath(Guid itemId)
    {
        return Path.Combine(SubtitlesPath, $"{itemId:N}.filtered.srt");
    }

    /// <summary>
    /// Determines whether a filtered subtitle output exists for an item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <returns><see langword="true"/> when a filtered subtitle exists; otherwise <see langword="false"/>.</returns>
    public bool HasFilteredSubtitle(Guid itemId)
    {
        return File.Exists(GetFilteredSrtPath(itemId));
    }

    private static string ApplyWordBlanking(string srtContent, JcfFilter filter)
    {
        var muteCues = filter.Cues
            .Where(c => c.Action.Equals("mute", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (muteCues.Length == 0)
        {
            return srtContent;
        }

        var phrases = FilterDictionary.GetWordLists()
            .SelectMany(kvp => kvp.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length)
            .ToList();

        if (phrases.Count == 0)
        {
            return srtContent;
        }

        var blocks = SplitSrtBlocks(srtContent);
        var processedBlocks = new List<string>(blocks.Count);
        foreach (var block in blocks)
        {
            var parsed = ParseSrtBlock(block);
            if (parsed is null)
            {
                processedBlocks.Add(block);
                continue;
            }

            var (index, start, end, text) = parsed.Value;
            var overlapsMute = muteCues.Any(cue => start < cue.End && end > cue.Start);
            if (!overlapsMute)
            {
                processedBlocks.Add(block);
                continue;
            }

            var blanked = BlankPhrases(text, phrases);
            var rebuiltBlock = $"{index}{Environment.NewLine}{FormatSrtTimecode(start)} --> {FormatSrtTimecode(end)}{Environment.NewLine}{blanked}";
            processedBlocks.Add(rebuiltBlock);
        }

        return string.Join($"{Environment.NewLine}{Environment.NewLine}", processedBlocks);
    }

    private static string BlankPhrases(string text, List<string> phrases)
    {
        var output = text;
        foreach (var phrase in phrases)
        {
            output = Regex.Replace(
                output,
                Regex.Escape(phrase),
                match =>
                {
                    var value = match.Value;
                    var prefixLength = value.TakeWhile(char.IsWhiteSpace).Count();
                    var prefix = value[..prefixLength];
                    var replacementLength = value.Length - prefixLength;
                    return replacementLength <= 0
                        ? value
                        : $"{prefix}{new string('*', replacementLength)}";
                },
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return output;
    }

    private static List<string> SplitSrtBlocks(string srtContent)
    {
        return Regex.Split(srtContent.Trim(), @"\r?\n\r?\n")
            .Where(block => !string.IsNullOrWhiteSpace(block))
            .ToList();
    }

    private static (int index, TimeSpan start, TimeSpan end, string text)? ParseSrtBlock(string block)
    {
        var lines = block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 3 || !int.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return null;
        }

        var separatorIndex = lines[1].IndexOf("-->", StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return null;
        }

        var startRaw = lines[1][..separatorIndex].Trim();
        var endRaw = lines[1][(separatorIndex + 3)..].Trim();
        if (!TryParseSrtTs(startRaw, out var start) || !TryParseSrtTs(endRaw, out var end))
        {
            return null;
        }

        var text = string.Join(Environment.NewLine, lines.Skip(2));
        return (index, start, end, text);
    }

    private static bool TryParseSrtTs(string value, out TimeSpan timestamp)
    {
        timestamp = default;
        var match = SrtTsRegex.Match(value);
        if (!match.Success)
        {
            return false;
        }

        var hours = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        var milliseconds = int.Parse(match.Groups["ms"].Value, CultureInfo.InvariantCulture);
        timestamp = new TimeSpan(0, hours, minutes, seconds, milliseconds);
        return true;
    }

    private static string FormatSrtTimecode(TimeSpan timestamp)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)timestamp.TotalHours:00}:{timestamp.Minutes:00}:{timestamp.Seconds:00},{timestamp.Milliseconds:000}");
    }
}
