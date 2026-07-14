namespace Jellyfin.Plugin.ContentFilter.Models;

/// <summary>
/// Defines all content filter categories and related metadata.
/// </summary>
public static class FilterDictionary
{
    /// <summary>
    /// Gets the full set of filter categories and their terms or descriptions.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Categories { get; } = new Dictionary<string, string[]>
    {
        // --- 1. LANGUAGE & PROFANITY (subtitle word match) ---
        ["Language.GeneralProfanity"] =
        [
            "arse", "ass", "bastard", "bitch", "bloody", "bollocks", "bugger",
            "cock", "crap", "cunt", "damn", "dick", "douche", "fuck", "fucking",
            "fucker", "hell", "piss", "prick", "pussy", "screw", "shit", "twat", "wank",
        ],

        ["Language.Blasphemy"] =
        [
            "Jesus Christ", "Oh God",
        ],

        ["Language.RacialAndBigotedSlurs"] =
        [
            "chink", "cracker", "heeb", "jap", "jiz", "kike", "kraut", "nigger",
            "pollack", "wetback", "wop",
        ],

        ["Language.ChildishLanguage"] =
        [
            "bum", "butt", "dumb", "fart", "stupid",
        ],

        ["Language.CaptionsWithProfanity"] =
        [
            "arse", "ass", "bastard", "bitch", "bloody", "bollocks", "bugger",
            "crap", "damn", "fuck", "fucking", "fucker", "hell", "shit",
        ],

        // --- 2. SEXUAL REFERENCES (subtitle word match + Ollama visual) ---
        ["SexualReferences.ExplicitWords"] =
        [
            "anus", "balls", "beastial", "blowjob", "clit", "cock", "condom", "cum",
            "cunillingus", "dick", "dildo", "dink", "douche", "ejaculate", "fag",
            "fellatio", "gangbang", "hard on", "horniest", "hump", "jerk", "kooch",
            "masturbate", "nuts", "orgasm", "picker", "penis", "porn", "prick",
            "piss", "pussy", "queer", "rimjob", "scrotum", "sex", "skeet", "slut",
            "testicle", "tits", "twat", "vagina", "wank", "whore",
        ],

        ["SexualReferences.ContextualDialogue"] =
        [
            "A man makes a sexual remark to a man.",
            "A man makes a sexual remark to a woman.",
            "A woman makes a sexual remark to a man.",
            "A woman makes a sexual remark to a woman.",
        ],

        ["SexualReferences.Visuals"] =
        [
            "Vulgar Gestures",
        ],

        // --- 3. SEX & NUDITY (Ollama visual) ---
        ["SexAndNudity.OnscreenActivity"] =
        [
            "Sex with Nudity",
            "Sex without Nudity",
            "Sexual Assault",
            "Implied Sex",
            "Sexually Suggestive",
        ],

        ["SexAndNudity.NudityProfiles"] =
        [
            "Female Nudity",
            "Male Nudity",
            "Implied Nudity",
            "Female Immodesty",
            "Male Immodesty",
            "Male & Female Immodesty",
            "Nude Statues & Paintings",
        ],

        ["SexAndNudity.PhysicalIntimacy"] =
        [
            "Heterosexual Kissing (Normal)",
            "Heterosexual Kissing (Passionate)",
            "Homosexual Kissing (Normal)",
            "Homosexual Kissing (Passionate)",
        ],

        // --- 4. VIOLENCE & HORROR (Ollama visual) ---
        ["Violence.Tiers"] =
        [
            "Gore",
            "Graphic Violence",
            "Non-graphic Violence",
            "Implied Violence",
            "Disturbing Images",
            "Objectionable, Disturbing, or Scary",
        ],

        // --- 5. SUBSTANCE USE (Ollama visual) ---
        ["Substances.Usage"] =
        [
            "Illegal Usage",
            "Legal Usage",
            "Implied Usage",
        ],

        // --- 6. MEDICAL & BIOLOGICAL (Ollama visual) ---
        ["Medical.Events"] =
        [
            "Medical Graphic",
            "Medical Procedures",
            "Life Events",
            "Bodily Functions/Jokes",
        ],

        // --- 7. STRUCTURAL TIMESTAMPS (Ollama visual) ---
        ["Structural.Timestamps"] =
        [
            "Opening Credits",
            "Closing Credits",
            "Episode Recap/Outtakes",
        ],
    };

    // Only Language.* and SexualReferences.ExplicitWords are subtitle word-match categories.
    // Everything else (including ContextualDialogue and Substances.Usage) is sent to Ollama.
    private static readonly HashSet<string> WordListKeys =
    [
        "Language.GeneralProfanity",
        "Language.Blasphemy",
        "Language.RacialAndBigotedSlurs",
        "Language.ChildishLanguage",
        "Language.CaptionsWithProfanity",
        "SexualReferences.ExplicitWords",
    ];

    private static readonly IReadOnlyDictionary<string, string> DefaultChannels = new Dictionary<string, string>
    {
        ["Language.GeneralProfanity"]              = "audio",
        ["Language.Blasphemy"]                     = "audio",
        ["Language.RacialAndBigotedSlurs"]         = "audio",
        ["Language.ChildishLanguage"]              = "audio",
        ["Language.CaptionsWithProfanity"]         = "both",
        ["SexualReferences.ExplicitWords"]         = "audio",
        ["SexualReferences.ContextualDialogue"]    = "video",
        ["SexualReferences.Visuals"]               = "video",
        ["SexAndNudity.OnscreenActivity"]          = "video",
        ["SexAndNudity.NudityProfiles"]            = "video",
        ["SexAndNudity.PhysicalIntimacy"]          = "video",
        ["Violence.Tiers"]                         = "video",
        ["Substances.Usage"]                       = "video",
        ["Medical.Events"]                         = "both",
        ["Structural.Timestamps"]                  = "both",
    };

    /// <summary>
    /// Gets the categories that use direct word and phrase matching.
    /// </summary>
    /// <returns>A dictionary containing only word-list categories.</returns>
    public static IReadOnlyDictionary<string, string[]> GetWordLists()
    {
        return Categories
            .Where(static pair => WordListKeys.Contains(pair.Key))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);
    }

    /// <summary>
    /// Gets the categories used for visual or contextual description analysis.
    /// </summary>
    /// <returns>A dictionary containing non-word-list categories.</returns>
    public static IReadOnlyDictionary<string, string[]> GetVisualDescriptions()
    {
        return Categories
            .Where(static pair => !WordListKeys.Contains(pair.Key))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);
    }

    /// <summary>
    /// Gets the default channel for a given category.
    /// </summary>
    /// <param name="category">The category key.</param>
    /// <returns>The default channel name.</returns>
    public static string GetDefaultChannel(string category)
    {
        if (DefaultChannels.TryGetValue(category, out var channel))
        {
            return channel;
        }

        return "both";
    }

    /// <summary>
    /// Gets the group portion of a category key.
    /// </summary>
    /// <param name="category">The category key.</param>
    /// <returns>The group name, or an empty string when unavailable.</returns>
    public static string GetGroup(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return string.Empty;
        }

        var split = category.Split('.', 2, StringSplitOptions.TrimEntries);
        return split.Length > 0 ? split[0] : string.Empty;
    }

    /// <summary>
    /// Gets all sub-categories for a group.
    /// </summary>
    /// <param name="group">The group name.</param>
    /// <returns>The category keys for the requested group.</returns>
    public static IReadOnlyCollection<string> GetSubCategories(string group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return [];
        }

        var prefix = group + ".";
        return Categories.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }
}
