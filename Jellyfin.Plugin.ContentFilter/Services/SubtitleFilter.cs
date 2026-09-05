using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ContentFilter.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Generates filtered subtitle outputs for mute-action cue ranges and blanket filter words.
/// </summary>
public class SubtitleFilter
{
    private static readonly Regex SrtTsRegex = new(
        @"^(?<h>\d+):(?<m>\d{2}):(?<s>\d{2})[,.](?<ms>\d{3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger<SubtitleFilter> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleFilter"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="serviceProvider">The service provider.</param>
    public SubtitleFilter(ILogger<SubtitleFilter> logger, ILibraryManager libraryManager, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _serviceProvider = serviceProvider;
    }

    private string SubtitlesPath => Path.Combine(Plugin.Instance!.DataFolderPath, "subtitles");

    /// <summary>
    /// Normalizes a language string to a 2-letter ISO-639-1 code (e.g. "eng" -> "en").
    /// </summary>
    /// <param name="lang">The language code or name.</param>
    /// <returns>A 2-letter language code.</returns>
    public static string ToTwoLetterLanguage(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
        {
            return "en";
        }

        var l = lang.Trim().ToLowerInvariant();
        if (l.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return "en";
        }

        if (l.StartsWith("es", StringComparison.OrdinalIgnoreCase) || l == "spa")
        {
            return "es";
        }

        if (l.StartsWith("fr", StringComparison.OrdinalIgnoreCase) || l == "fra" || l == "fre")
        {
            return "fr";
        }

        if (l.StartsWith("de", StringComparison.OrdinalIgnoreCase) || l == "deu" || l == "ger")
        {
            return "de";
        }

        if (l.StartsWith("it", StringComparison.OrdinalIgnoreCase) || l == "ita")
        {
            return "it";
        }

        if (l.StartsWith("pt", StringComparison.OrdinalIgnoreCase) || l == "por")
        {
            return "pt";
        }

        return l.Length > 2 ? l[..2] : l;
    }

    /// <summary>
    /// Gets the sidecar filtered SRT path adjacent to the media file on disk.
    /// Example: /data/shows/GOT_S01E01.mkv -> /data/shows/GOT_S01E01.en.filtered.srt
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="language">The subtitle language code.</param>
    /// <returns>The sidecar path, or <see langword="null"/> if not resolvable.</returns>
    public static string? GetSidecarFilteredSrtPath(BaseItem? item, string language = "en")
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(item.Path);
        var stem = Path.GetFileNameWithoutExtension(item.Path);
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        var langCode = ToTwoLetterLanguage(language);
        return Path.Combine(dir, $"{stem}.{langCode}.filtered.srt");
    }

    /// <summary>
    /// Gets the standard Jellyfin default sidecar SRT path adjacent to the media file on disk.
    /// Example: /data/shows/GOT_S01E01.mkv -> /data/shows/GOT_S01E01.en.default.srt
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="language">The subtitle language code.</param>
    /// <returns>The default sidecar path, or <see langword="null"/> if not resolvable.</returns>
    public static string? GetSidecarDefaultSrtPath(BaseItem? item, string language = "en")
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(item.Path);
        var stem = Path.GetFileNameWithoutExtension(item.Path);
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        var langCode = ToTwoLetterLanguage(language);
        return Path.Combine(dir, $"{stem}.{langCode}.default.srt");
    }

    /// <summary>
    /// Determines whether an adjacent sidecar filtered or default SRT exists for an item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="language">The subtitle language code.</param>
    /// <returns><see langword="true"/> when either sidecar exists; otherwise <see langword="false"/>.</returns>
    public bool HasSidecarFilteredSrt(Guid itemId, string language = "en")
    {
        var item = _libraryManager.GetItemById(itemId);
        var defaultPath = GetSidecarDefaultSrtPath(item, language);
        var filteredPath = GetSidecarFilteredSrtPath(item, language);
        return (defaultPath is not null && File.Exists(defaultPath)) ||
               (filteredPath is not null && File.Exists(filteredPath));
    }

    /// <summary>
    /// Shifts all timestamps in an SRT subtitle content string by a given offset in seconds.
    /// </summary>
    /// <param name="srtContent">The source SRT subtitle content.</param>
    /// <param name="offsetSeconds">The offset in seconds (can be positive or negative).</param>
    /// <returns>The adjusted SRT content.</returns>
    public static string ShiftSrtTimecodes(string srtContent, double offsetSeconds)
    {
        if (string.IsNullOrWhiteSpace(srtContent) || Math.Abs(offsetSeconds) < 0.0001)
        {
            return srtContent;
        }

        var offset = TimeSpan.FromSeconds(offsetSeconds);
        var blocks = SplitSrtBlocks(srtContent);
        var shiftedBlocks = new List<string>(blocks.Count);
        int newIndex = 1;

        foreach (var block in blocks)
        {
            var parsed = ParseSrtBlock(block);
            if (parsed is null)
            {
                continue;
            }

            var (_, start, end, text) = parsed.Value;
            var newStart = start + offset;
            var newEnd = end + offset;

            if (newEnd <= TimeSpan.Zero)
            {
                continue; // Cue shifted entirely before start of video
            }

            if (newStart < TimeSpan.Zero)
            {
                newStart = TimeSpan.Zero;
            }

            var rebuilt = $"{newIndex++}{Environment.NewLine}{FormatSrtTimecode(newStart)} --> {FormatSrtTimecode(newEnd)}{Environment.NewLine}{text}";
            shiftedBlocks.Add(rebuilt);
        }

        return string.Join($"{Environment.NewLine}{Environment.NewLine}", shiftedBlocks);
    }

    /// <summary>
    /// Regenerates filtered subtitle output for an item in a given language.
    /// Writes to both the plugin cache and adjacent media sidecars (.default.srt and .filtered.srt) if possible.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="filter">The active filter (optional, loaded from store if null).</param>
    /// <param name="language">The requested subtitle language.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The path of the generated subtitle file, or null if no source subtitles exist.</returns>
    public async Task<string?> RegenerateAsync(Guid itemId, JcfFilter? filter, string language, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            _logger.LogDebug("Item {ItemId} not found; skipping subtitle regeneration.", itemId);
            return null;
        }

        filter ??= _serviceProvider.GetRequiredService<FilterStore>().GetFilter(itemId);

        // Extract or retrieve original SRT
        var scanner = _serviceProvider.GetRequiredService<SubtitleWordScanner>();
        var srtContent = await scanner.GetSrtContentAsync(itemId, language, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(srtContent))
        {
            _logger.LogDebug("No SRT content available for item {ItemId} (language {Lang}).", itemId, language);
            return null;
        }

        // Apply item time shift offset if configured in SQLite repository
        var repo = _serviceProvider.GetService<SqliteFilterRepository>();
        var overrideInfo = repo?.GetSubtitleOverride(itemId);
        if (overrideInfo != null && Math.Abs(overrideInfo.OffsetSeconds) > 0.0001)
        {
            srtContent = ShiftSrtTimecodes(srtContent, overrideInfo.OffsetSeconds);
        }

        var filteredOutput = ApplyWordBlanking(srtContent, filter);

        // 1. Save to plugin data folder cache
        Directory.CreateDirectory(SubtitlesPath);
        var pluginCachePath = GetFilteredSrtPath(itemId);
        await File.WriteAllTextAsync(pluginCachePath, filteredOutput, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        // 2. Save to adjacent sidecars if media directory is accessible
        var defaultPath = GetSidecarDefaultSrtPath(item, language);
        var filteredPath = GetSidecarFilteredSrtPath(item, language);
        if (defaultPath is not null)
        {
            try
            {
                var dir = Path.GetDirectoryName(defaultPath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    // Write both .default.srt (Jellyfin standard for default subtitle) and .filtered.srt
                    await File.WriteAllTextAsync(defaultPath, filteredOutput, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                    if (filteredPath is not null)
                    {
                        await File.WriteAllTextAsync(filteredPath, filteredOutput, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                    }

                    _logger.LogInformation("Saved clean subtitle sidecars to {Path}", defaultPath);

                    // Trigger Jellyfin item refresh so it immediately registers the external track
                    RefreshItemSubtitles(item);

                    // Set default subtitle stream for active server users if enabled
                    var config = Plugin.Instance?.Configuration;
                    if (config == null || config.SetSubtitlesAsDefault)
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(1200, CancellationToken.None).ConfigureAwait(false);
                            SetDefaultSubtitleForUsers(item, defaultPath);
                        });
                    }

                    return defaultPath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write sidecar subtitle to {Path}; plugin cache remains available.", defaultPath);
            }
        }

        return pluginCachePath;
    }

    /// <summary>
    /// Regenerates filtered subtitle output for an item (default English).
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="filter">The active filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when regeneration is complete.</returns>
    public async Task RegenerateAsync(Guid itemId, JcfFilter filter, CancellationToken cancellationToken)
    {
        await RegenerateAsync(itemId, filter, "eng", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the clean default subtitle track as the active selection for all Jellyfin users for an item.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="sidecarPath">The written subtitle file path.</param>
    public void SetDefaultSubtitleForUsers(BaseItem item, string? sidecarPath)
    {
        if (item is not Video video)
        {
            return;
        }

        try
        {
            var userDataManager = _serviceProvider.GetService<IUserDataManager>();
            var userManager = _serviceProvider.GetService<IUserManager>();
            if (userDataManager is null || userManager is null)
            {
                return;
            }

            var streams = video.GetMediaStreams();
            var cleanStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle &&
                s.IsExternal &&
                !string.IsNullOrWhiteSpace(s.Path) &&
                (s.Path.EndsWith(".default.srt", StringComparison.OrdinalIgnoreCase) ||
                 s.Path.EndsWith(".filtered.srt", StringComparison.OrdinalIgnoreCase)));

            if (cleanStream == null && sidecarPath != null)
            {
                cleanStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle &&
                    s.IsExternal &&
                    string.Equals(s.Path, sidecarPath, StringComparison.OrdinalIgnoreCase));
            }

            if (cleanStream != null)
            {
                foreach (var user in userManager.GetUsers())
                {
                    try
                    {
                        var data = userDataManager.GetUserData(user, video);
                        if (data != null && data.SubtitleStreamIndex != cleanStream.Index)
                        {
                            data.SubtitleStreamIndex = cleanStream.Index;
                            userDataManager.SaveUserData(user, video, data, UserDataSaveReason.UpdateUserData, CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to set default subtitle stream for user {UserName} on item {ItemId}", user.Username, video.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error setting user default subtitle selections for item {ItemId}", item.Id);
        }
    }

    /// <summary>
    /// Refreshes item metadata in Jellyfin to pick up newly written or removed sidecar subtitle files.
    /// </summary>
    /// <param name="item">The media item to refresh.</param>
    public void RefreshItemSubtitles(BaseItem item)
    {
        try
        {
            var fileSystem = _serviceProvider.GetService<MediaBrowser.Model.IO.IFileSystem>();
            var directoryService = fileSystem != null ? new DirectoryService(fileSystem) : new DirectoryService(null!);
            var options = new MetadataRefreshOptions(directoryService)
            {
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ImageRefreshMode = MetadataRefreshMode.None,
                ReplaceAllMetadata = false
            };

            Task.Run(async () =>
            {
                try
                {
                    await item.RefreshMetadata(options, CancellationToken.None).ConfigureAwait(false);
                    _logger.LogInformation("Triggered metadata refresh for item {ItemName} ({ItemId})", item.Name, item.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Background metadata refresh for item {ItemId} encountered an error.", item.Id);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to initiate item refresh for item {ItemId}.", item.Id);
        }
    }

    /// <summary>
    /// Deletes the generated filtered subtitle for an item (both plugin cache and sidecars).
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    public void DeleteFilteredSubtitle(Guid itemId)
    {
        var path = GetFilteredSrtPath(itemId);
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is not null)
        {
            var sidecars = new[]
            {
                GetSidecarDefaultSrtPath(item),
                GetSidecarFilteredSrtPath(item)
            };

            bool anyDeleted = false;
            foreach (var sidecar in sidecars)
            {
                if (sidecar is not null && File.Exists(sidecar))
                {
                    try
                    {
                        File.Delete(sidecar);
                        anyDeleted = true;
                    }
                    catch
                    {
                    }
                }
            }

            if (anyDeleted)
            {
                RefreshItemSubtitles(item);
            }
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
        return File.Exists(GetFilteredSrtPath(itemId)) || HasSidecarFilteredSrt(itemId);
    }

    /// <summary>
    /// Masks a word or phrase, preserving the first letter of each word and replacing remaining letters with asterisks.
    /// Example: "bastard" -> "b******", "damn" -> "d***", "son of a bitch" -> "s** o* a b****".
    /// </summary>
    /// <param name="word">The word or phrase to mask.</param>
    /// <returns>The masked string.</returns>
    public static string MaskLeavingFirstLetter(string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return word;
        }

        return Regex.Replace(word, @"\b\w+", match =>
        {
            var val = match.Value;
            if (val.Length <= 1)
            {
                return val;
            }

            return val[0] + new string('*', val.Length - 1);
        });
    }

    /// <summary>
    /// Redacts specific target phrases within text, replacing each with first-letter masking.
    /// </summary>
    /// <param name="text">The source text.</param>
    /// <param name="phrases">The collection of words or phrases to redact.</param>
    /// <returns>The redacted text.</returns>
    public static string RedactPhrases(string text, IEnumerable<string> phrases)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var sortedPhrases = phrases
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(static p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static p => p.Length);

        var output = text;
        foreach (var phrase in sortedPhrases)
        {
            var pattern = FilterDictionary.BuildWordPattern(phrase);
            if (string.IsNullOrEmpty(pattern))
            {
                continue;
            }

            output = Regex.Replace(
                output,
                pattern,
                match => MaskLeavingFirstLetter(match.Value),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return output;
    }

    /// <summary>
    /// Applies word blanking / masking to SRT subtitle content based on filter cues and blanket words.
    /// </summary>
    /// <param name="srtContent">The source SRT content.</param>
    /// <param name="filter">The active filter (optional).</param>
    /// <returns>The filtered SRT content with words masked leaving the first letter.</returns>
    public static string ApplyWordBlanking(string srtContent, JcfFilter? filter)
    {
        if (string.IsNullOrWhiteSpace(srtContent))
        {
            return srtContent;
        }

        var muteCues = filter?.Cues
            .Where(c => c.Action.Equals("mute", StringComparison.OrdinalIgnoreCase) ||
                        c.Action.Equals("skip", StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];

        // Collect global blanket filter words
        var config = Plugin.Instance?.Configuration;
        var globalBlanketWords = new HashSet<string>(
            config?.BlanketFilterWords ?? [],
            StringComparer.OrdinalIgnoreCase);

        // Collect specific words from cue descriptions (e.g. Spoken: "bastard")
        var cueWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cue in muteCues)
        {
            if (string.IsNullOrWhiteSpace(cue.Description))
            {
                continue;
            }

            var m = Regex.Match(cue.Description, "\"([^\"]+)\"");
            if (m.Success)
            {
                cueWords.Add(m.Groups[1].Value.Trim());
            }
            else if (!cue.Description.Contains(' ') && cue.Description.Length < 30)
            {
                cueWords.Add(cue.Description.Trim());
            }
        }

        // Full dictionary words
        var dictWords = FilterDictionary.GetWordLists()
            .SelectMany(kvp => kvp.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Target phrases for cues (cue words + dictionary words + global blanket words)
        var allCuePhrases = cueWords
            .Concat(globalBlanketWords)
            .Concat(dictWords)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => p.Length)
            .ToList();

        // Blanket phrases to redact everywhere even outside cues
        var blanketPhrases = globalBlanketWords
            .Concat(cueWords)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => p.Length)
            .ToList();

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

            var redactedText = text;
            if (overlapsMute)
            {
                // In mute/skip window: redact all dictionary words, cue words, and blanket words
                redactedText = RedactPhrases(redactedText, allCuePhrases);
            }
            else if (blanketPhrases.Count > 0)
            {
                // Outside mute window: redact configured blanket words
                redactedText = RedactPhrases(redactedText, blanketPhrases);
            }

            var rebuiltBlock = $"{index}{Environment.NewLine}{FormatSrtTimecode(start)} --> {FormatSrtTimecode(end)}{Environment.NewLine}{redactedText}";
            processedBlocks.Add(rebuiltBlock);
        }

        return string.Join($"{Environment.NewLine}{Environment.NewLine}", processedBlocks);
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
        timestamp = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    private static string FormatSrtTimecode(TimeSpan timestamp)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)timestamp.TotalHours:00}:{timestamp.Minutes:00}:{timestamp.Seconds:00},{timestamp.Milliseconds:000}");
    }
}
