using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ContentFilter.Configuration;
using Jellyfin.Plugin.ContentFilter.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Subtitle track information.
/// </summary>
public class SubtitleTrackInfo
{
    /// <summary>Gets or sets the stream index.</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets the ISO language code (e.g. "eng", "spa").</summary>
    public string Language { get; set; } = "eng";

    /// <summary>Gets or sets the user-friendly display name.</summary>
    public string DisplayName { get; set; } = "English";

    /// <summary>Gets or sets a value indicating whether this is an external file.</summary>
    public bool IsExternal { get; set; }

    /// <summary>Gets or sets the format/codec (e.g. "srt", "ass").</summary>
    public string? Codec { get; set; }
}

/// <summary>
/// Represents a single occurrence of a filterable word in dialogue.
/// </summary>
public class SubtitleWordOccurrence
{
    /// <summary>Gets or sets the occurrence identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the subtitle cue start timecode.</summary>
    public string Start { get; set; } = string.Empty;

    /// <summary>Gets or sets the subtitle cue end timecode.</summary>
    public string End { get; set; } = string.Empty;

    /// <summary>Gets or sets the start time in seconds.</summary>
    public double StartSeconds { get; set; }

    /// <summary>Gets or sets the end time in seconds.</summary>
    public double EndSeconds { get; set; }

    /// <summary>Gets or sets the full subtitle sentence context.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the calculated audio mute start timecode.</summary>
    public string CueStart { get; set; } = string.Empty;

    /// <summary>Gets or sets the calculated audio mute end timecode.</summary>
    public string CueEnd { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this occurrence is currently filtered.</summary>
    public bool IsFiltered { get; set; }

    /// <summary>Gets or sets the exact matched word in dialogue (e.g. "bastards" when term is "bastard").</summary>
    public string MatchedWord { get; set; } = string.Empty;
}

/// <summary>
/// Represents a group of occurrences for a single detected word.
/// </summary>
public class SubtitleWordGroup
{
    /// <summary>Gets or sets the detected word or phrase.</summary>
    public string Word { get; set; } = string.Empty;

    /// <summary>Gets or sets the category (e.g. Language.GeneralProfanity).</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Gets or sets the total count in this media item.</summary>
    public int Count { get; set; }

    /// <summary>Gets or sets a value indicating whether all occurrences are filtered.</summary>
    public bool IsFiltered { get; set; }

    /// <summary>Gets or sets a value indicating whether this word is in the global blanket filter list.</summary>
    public bool IsGlobalBlanket { get; set; }

    /// <summary>Gets or sets the list of occurrences.</summary>
    public List<SubtitleWordOccurrence> Occurrences { get; set; } = [];
}

/// <summary>
/// Result of scanning a media item's subtitles.
/// </summary>
public class SubtitleScanResult
{
    /// <summary>Gets or sets the media item identifier.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the selected language code.</summary>
    public string SelectedLanguage { get; set; } = "eng";

    /// <summary>Gets or sets the list of available subtitle tracks.</summary>
    public List<SubtitleTrackInfo> AvailableTracks { get; set; } = [];

    /// <summary>Gets or sets the count of unique filterable words detected.</summary>
    public int TotalUniqueWords { get; set; }

    /// <summary>Gets or sets the total count of word occurrences.</summary>
    public int TotalOccurrences { get; set; }

    /// <summary>Gets or sets the list of word groups.</summary>
    public List<SubtitleWordGroup> Words { get; set; } = [];
}

/// <summary>
/// Service that discovers, extracts, and scans subtitle tracks for filterable words.
/// </summary>
public class SubtitleWordScanner
{
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>|\{[^}]+\}", RegexOptions.Compiled);
    private readonly ILogger<SubtitleWordScanner> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly FilterStore _filterStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleWordScanner"/> class.
    /// </summary>
    public SubtitleWordScanner(
        ILogger<SubtitleWordScanner> logger,
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        FilterStore filterStore)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _filterStore = filterStore;
    }

    private string CacheDir => Path.Combine(Plugin.Instance?.DataFolderPath ?? "/tmp", "subtitles", "cache");

    /// <summary>
    /// Gets all available subtitle tracks for an item.
    /// </summary>
    public List<SubtitleTrackInfo> GetAvailableTracks(Guid itemId)
    {
        var result = new List<SubtitleTrackInfo>();
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return result;
        }

        var mediaPath = item.Path;
        if (!string.IsNullOrWhiteSpace(mediaPath))
        {
            var dir = Path.GetDirectoryName(mediaPath);
            var stem = Path.GetFileNameWithoutExtension(mediaPath);
            if (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(stem) && Directory.Exists(dir))
            {
                var externalSrts = Directory.GetFiles(dir, $"{stem}*.srt");
                int extIdx = -100;
                foreach (var srt in externalSrts)
                {
                    var fn = Path.GetFileName(srt);
                    var lang = "eng";
                    var parts = fn.Split('.');
                    if (parts.Length >= 3 && parts[^1].Equals("srt", StringComparison.OrdinalIgnoreCase))
                    {
                        lang = parts[^2].ToLowerInvariant();
                        if (lang.Equals("whispersubs", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
                        {
                            lang = parts[^3].ToLowerInvariant();
                        }
                    }

                    result.Add(new SubtitleTrackInfo
                    {
                        Index = extIdx--,
                        Language = NormalizeLanguage(lang),
                        DisplayName = $"{GetLanguageDisplayName(lang)} (External File)",
                        IsExternal = true,
                        Codec = "srt"
                    });
                }
            }
        }

        if (item is Video video)
        {
            var streams = video.GetMediaStreams();
            foreach (var s in streams.Where(st => st.Type == MediaStreamType.Subtitle))
            {
                var lang = NormalizeLanguage(s.Language ?? "eng");
                var name = !string.IsNullOrWhiteSpace(s.DisplayTitle)
                    ? s.DisplayTitle
                    : $"{GetLanguageDisplayName(lang)} (Embedded {s.Codec?.ToUpperInvariant() ?? "Subtitle"})";

                result.Add(new SubtitleTrackInfo
                {
                    Index = s.Index,
                    Language = lang,
                    DisplayName = name,
                    IsExternal = s.IsExternal,
                    Codec = s.Codec
                });
            }
        }

        // Sort English tracks first, then alphabetical
        return result
            .OrderByDescending(t => t.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            .ThenBy(t => t.Language)
            .ToList();
    }

    /// <summary>
    /// Retrieves or extracts SRT content for a given item and language or stream index.
    /// </summary>
    public async Task<string?> GetSrtContentAsync(Guid itemId, string language, CancellationToken ct)
    {
        bool isNumericIndex = int.TryParse(language, out var reqIndex);
        var normLang = isNumericIndex ? "eng" : NormalizeLanguage(language);
        Directory.CreateDirectory(CacheDir);
        var cacheFile = isNumericIndex
            ? Path.Combine(CacheDir, $"{itemId:N}_idx{reqIndex}.srt")
            : Path.Combine(CacheDir, $"{itemId:N}_{normLang}.srt");

        if (File.Exists(cacheFile) && new FileInfo(cacheFile).Length > 0)
        {
            return await File.ReadAllTextAsync(cacheFile, ct).ConfigureAwait(false);
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || string.IsNullOrWhiteSpace(item.Path))
        {
            return null;
        }

        var mediaPath = item.Path;
        var dir = Path.GetDirectoryName(mediaPath);
        var stem = Path.GetFileNameWithoutExtension(mediaPath);

        // 1. If not a specific embedded index, check adjacent external SRTs
        if (!isNumericIndex && !string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(stem) && Directory.Exists(dir))
        {
            string[] candidateFiles =
            [
                Path.Combine(dir, $"{stem}.{normLang}.WhisperSubs.srt"),
                Path.Combine(dir, $"{stem}.{normLang}.generated.srt"),
                Path.Combine(dir, $"{stem}.{normLang}.srt"),
                Path.Combine(dir, $"{stem}.WhisperSubs.srt"),
                Path.Combine(dir, $"{stem}.generated.srt"),
                Path.Combine(dir, $"{stem}.srt")
            ];

            foreach (var cand in candidateFiles)
            {
                if (File.Exists(cand))
                {
                    var text = await File.ReadAllTextAsync(cand, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        await File.WriteAllTextAsync(cacheFile, text, Encoding.UTF8, ct).ConfigureAwait(false);
                        return text;
                    }
                }
            }
        }

        // 2. Extract from embedded subtitle streams using ffmpeg
        if (item is Video video)
        {
            var streams = video.GetMediaStreams()
                .Where(s => s.Type == MediaStreamType.Subtitle)
                .ToList();

            MediaStream? matchedStream = null;
            if (isNumericIndex)
            {
                matchedStream = streams.FirstOrDefault(s => s.Index == reqIndex);
            }

            if (matchedStream == null)
            {
                var matching = streams.Where(s => (s.Language ?? "eng").StartsWith(normLang, StringComparison.OrdinalIgnoreCase)).ToList();
                // Prioritize full dialogue subtitles over forced/commentary
                matchedStream = matching.FirstOrDefault(s => (s.Title?.Contains("full", StringComparison.OrdinalIgnoreCase) == true) ||
                                                             (s.DisplayTitle?.Contains("full", StringComparison.OrdinalIgnoreCase) == true))
                                ?? matching.FirstOrDefault(s => !s.IsForced &&
                                                             (s.Title == null || !s.Title.Contains("forced", StringComparison.OrdinalIgnoreCase)) &&
                                                             (s.DisplayTitle == null || !s.DisplayTitle.Contains("forced", StringComparison.OrdinalIgnoreCase)))
                                ?? matching.FirstOrDefault()
                                ?? streams.FirstOrDefault(s => !s.IsForced && (s.Language ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase))
                                ?? streams.FirstOrDefault();
            }

            if (matchedStream != null)
            {
                var extracted = await ExtractSubtitleStreamAsync(mediaPath, matchedStream.Index, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    var cleaned = CleanSrtText(extracted);
                    await File.WriteAllTextAsync(cacheFile, cleaned, Encoding.UTF8, ct).ConfigureAwait(false);
                    return cleaned;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Scans an item's subtitle track for filterable words.
    /// </summary>
    public async Task<SubtitleScanResult> ScanWordsAsync(Guid itemId, string language, CancellationToken ct)
    {
        bool isNumericIndex = int.TryParse(language, out _);
        var normLang = isNumericIndex ? language : NormalizeLanguage(language);
        var tracks = GetAvailableTracks(itemId);
        var srt = await GetSrtContentAsync(itemId, normLang, ct).ConfigureAwait(false);

        var result = new SubtitleScanResult
        {
            ItemId = itemId,
            SelectedLanguage = normLang,
            AvailableTracks = tracks
        };

        if (string.IsNullOrWhiteSpace(srt))
        {
            return result;
        }

        var activeFilter = _filterStore.GetFilter(itemId);
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var globalBlanketWords = new HashSet<string>(config.BlanketFilterWords ?? [], StringComparer.OrdinalIgnoreCase);

        // Gather all dictionary word lists
        var dictionaryWords = FilterDictionary.GetWordLists();
        var wordsToScan = new Dictionary<string, (string Category, Regex Pattern)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (category, list) in dictionaryWords)
        {
            foreach (var term in list)
            {
                if (string.IsNullOrWhiteSpace(term)) continue;
                if (!wordsToScan.ContainsKey(term))
                {
                    var pattern = new Regex(FilterDictionary.BuildWordPattern(term), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    wordsToScan[term] = (category, pattern);
                }
            }
        }

        // Add any global blanket words not yet in dictionary
        foreach (var w in globalBlanketWords)
        {
            if (!wordsToScan.ContainsKey(w))
            {
                var pattern = new Regex(FilterDictionary.BuildWordPattern(w), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                wordsToScan[w] = ("Custom.BlanketWord", pattern);
            }
        }

        var orderedWordsToScan = wordsToScan
            .OrderByDescending(kvp => kvp.Key.Length)
            .ToList();

        var wordGroupMap = new Dictionary<string, SubtitleWordGroup>(StringComparer.OrdinalIgnoreCase);
        var blocks = SplitSrtBlocks(srt);
        int occCounter = 0;

        foreach (var block in blocks)
        {
            if (!TryParseSrtBlock(block, out var start, out var end, out var dialogueText))
            {
                continue;
            }

            var cleanDialogue = HtmlTagRegex.Replace(dialogueText, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleanDialogue)) continue;

            // Track matched character spans in this block to avoid overlapping duplicate cues
            var matchedSpans = new List<(int Start, int End)>();

            foreach (var (term, (category, regex)) in orderedWordsToScan)
            {
                var matches = regex.Matches(cleanDialogue);
                if (matches.Count == 0) continue;

                foreach (Match m in matches)
                {
                    var mStart = m.Index;
                    var mEnd = m.Index + m.Length;

                    // Skip if this character range overlaps with an already matched longer term
                    if (matchedSpans.Any(span => mStart < span.End && mEnd > span.Start))
                    {
                        continue;
                    }

                    matchedSpans.Add((mStart, mEnd));

                    if (!wordGroupMap.TryGetValue(term, out var group))
                    {
                        group = new SubtitleWordGroup
                        {
                            Word = term,
                            Category = category,
                            IsGlobalBlanket = globalBlanketWords.Contains(term),
                            Occurrences = []
                        };
                        wordGroupMap[term] = group;
                    }

                    occCounter++;
                    var blockDur = (end - start).TotalSeconds;
                    var ratio = cleanDialogue.Length <= 1 ? 0.0 : (double)m.Index / cleanDialogue.Length;
                    var wordStart = start + TimeSpan.FromSeconds(ratio * blockDur);
                    var wordEnd = wordStart + TimeSpan.FromMilliseconds(Math.Max(350, m.Length * 90));

                    // Mute window padding: 80ms before, 120ms after
                    var muteStart = TimeSpan.FromTicks(Math.Max((wordStart - TimeSpan.FromMilliseconds(80)).Ticks, start.Ticks));
                    var muteEnd = TimeSpan.FromTicks(Math.Min((wordEnd + TimeSpan.FromMilliseconds(120)).Ticks, end.Ticks));

                    // Check if already filtered by existing mute/skip cue
                    var isFiltered = activeFilter?.Cues != null && activeFilter.Cues.Any(c =>
                        (c.Action.Equals("mute", StringComparison.OrdinalIgnoreCase) || c.Action.Equals("skip", StringComparison.OrdinalIgnoreCase)) &&
                        muteStart < c.End && muteEnd > c.Start);

                    var occ = new SubtitleWordOccurrence
                    {
                        Id = $"occ_{occCounter}",
                        Start = FormatTimecode(start),
                        End = FormatTimecode(end),
                        StartSeconds = start.TotalSeconds,
                        EndSeconds = end.TotalSeconds,
                        Text = cleanDialogue,
                        CueStart = FormatTimecode(muteStart),
                        CueEnd = FormatTimecode(muteEnd),
                        IsFiltered = isFiltered,
                        MatchedWord = m.Value
                    };

                    group.Occurrences.Add(occ);
                }
            }
        }

        // Finalize groups
        foreach (var group in wordGroupMap.Values)
        {
            group.Count = group.Occurrences.Count;
            group.IsFiltered = group.Occurrences.Count > 0 && group.Occurrences.All(o => o.IsFiltered);
        }

        result.Words = wordGroupMap.Values
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Word)
            .ToList();

        result.TotalUniqueWords = result.Words.Count;
        result.TotalOccurrences = result.Words.Sum(w => w.Count);

        return result;
    }

    private async Task<string?> ExtractSubtitleStreamAsync(string mediaPath, int streamIndex, CancellationToken ct)
    {
        var encoderPath = _mediaEncoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(encoderPath) || !File.Exists(encoderPath))
        {
            _logger.LogWarning("ffmpeg encoder path not found: {Path}", encoderPath);
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = encoderPath,
            Arguments = $"-v error -i \"{mediaPath}\" -map 0:{streamIndex} -f srt -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var output = await stdoutTask.ConfigureAwait(false);
            var error = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
            {
                _logger.LogWarning("ffmpeg subtitle extraction failed (exit {Code}): {Error}", proc.ExitCode, error);
                return null;
            }

            return output;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing ffmpeg subtitle extraction on {Path} stream {Index}", mediaPath, streamIndex);
            return null;
        }
    }

    private static string CleanSrtText(string raw)
    {
        // Strip font and ASS styling markup while keeping dialogue text
        return HtmlTagRegex.Replace(raw, string.Empty);
    }

    private static IEnumerable<string> SplitSrtBlocks(string srtContent)
    {
        return srtContent.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool TryParseSrtBlock(string block, out TimeSpan start, out TimeSpan end, out string text)
    {
        start = default;
        end = default;
        text = string.Empty;

        var lines = block.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return false;

        var timeLineIdx = -1;
        for (int i = 0; i < Math.Min(lines.Length, 3); i++)
        {
            if (lines[i].Contains("-->", StringComparison.Ordinal))
            {
                timeLineIdx = i;
                break;
            }
        }

        if (timeLineIdx == -1) return false;

        var tokens = lines[timeLineIdx].Split("-->", StringSplitOptions.TrimEntries);
        if (tokens.Length != 2) return false;

        if (!TryParseSrtTime(tokens[0], out start) || !TryParseSrtTime(tokens[1], out end))
        {
            return false;
        }

        text = string.Join(" ", lines.Skip(timeLineIdx + 1));
        return true;
    }

    private static bool TryParseSrtTime(string raw, out TimeSpan value)
    {
        value = default;
        var parts = raw.Split([':', ',', '.'], StringSplitOptions.TrimEntries);
        if (parts.Length < 4) return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ||
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
        {
            return false;
        }

        value = new TimeSpan(0, h, m, s, ms);
        return true;
    }

    private static string FormatTimecode(TimeSpan ts)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}");
    }

    private static string NormalizeLanguage(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return "eng";
        var l = lang.Trim().ToLowerInvariant();
        if (l == "en" || l == "eng" || l == "english") return "eng";
        if (l == "es" || l == "spa" || l == "spanish") return "spa";
        if (l == "fr" || l == "fra" || l == "fre" || l == "french") return "fra";
        if (l == "de" || l == "deu" || l == "ger" || l == "german") return "deu";
        if (l == "it" || l == "ita" || l == "italian") return "ita";
        if (l == "pt" || l == "por" || l == "portuguese") return "por";
        return l.Length > 3 ? l[..3] : l;
    }

    private static string GetLanguageDisplayName(string lang)
    {
        return NormalizeLanguage(lang) switch
        {
            "eng" => "English",
            "spa" => "Spanish",
            "fra" => "French",
            "deu" => "German",
            "ita" => "Italian",
            "por" => "Portuguese",
            "rus" => "Russian",
            "jpn" => "Japanese",
            "zho" => "Chinese",
            "kor" => "Korean",
            "ara" => "Arabic",
            "tur" => "Turkish",
            _ => char.ToUpperInvariant(lang[0]) + (lang.Length > 1 ? lang[1..] : "")
        };
    }
}
