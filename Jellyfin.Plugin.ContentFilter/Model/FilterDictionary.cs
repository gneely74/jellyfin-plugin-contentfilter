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
        // --- 1. LANGUAGE & PROFANITY (word/phrase match against subtitles) ---
        ["Language.GeneralProfanity"] =
        [
            "arse", "ass", "bastard", "bitch", "bloody", "bollocks", "bugger",
            "cock", "crap", "cunt", "damn", "dick", "douche", "fuck", "fucking",
            "fucker", "hell", "piss", "prick", "pussy", "screw", "shit", "twat", "wank",
        ],

        ["Language.Blasphemy"] =
        [
            "Jesus Christ", " Oh God",
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

        // --- 2. SEXUAL REFERENCES & ANATOMY (word/phrase match against subtitles) ---
        ["SexualReferences.ExplicitWords"] =
        [
            "anus", "balls", "beastial", "blowjob", "clit", "cock", "condom", "cum",
            "cunillingus", "dick", "dildo", "dink", "douche", "ejaculate", "fag",
            "fellatio", "gangbang", "hard on", "horniest", "hump", "jerk", "kooch",
            "masturbate", "nuts", "orgasm", "picker", "penis", "porn", "prick",
            "piss", "pussy", "queer", "rimjob", "scrotum", "sex", "skeet", "slut",
            "testicle", "tits", "twat", "vagina", "wank", "whore",
        ],

        // Contextual sexual dialogue — matched against transcript, not sent to vision model.
        ["SexualReferences.ContextualDialogue"] =
        [
            "sexy", "horny", "aroused", "turned on", "get laid", "getting laid",
            "hook up", "hooked up", "one night stand", "sleep together", "slept together",
            "sleeping with", "make love", "making love", "in bed with",
            "come on to", "hitting on", "hit on", "come-on",
            "booty call", "booty", "sexually", "naughty", "kinky", "fetish",
            "sexual remark", "sexual reference", "dirty joke",
        ],

        // --- 3. VISUAL CATEGORIES (descriptions sent to Ollama vision model) ---

        ["SexualReferences.Visuals"] =
        [
            "Vulgar Gestures",
        ],

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

        ["Violence.Tiers"] =
        [
            "Gore",
            "Graphic Violence",
            "Non-graphic Violence",
            "Implied Violence",
            "Disturbing Images",
            "Objectionable, Disturbing, or Scary",
        ],

        // Substance use — matched against transcript, not sent to vision model.
        ["Substances.Usage"] =
        [
            // Illegal drugs
            "joint", "blunt", "weed", "pot", "marijuana", "cannabis",
            "cocaine", "coke", "crack", "heroin", "meth", "methamphetamine",
            "ecstasy", "molly", "mdma", "ketamine", "opioid", "fentanyl",
            "xanax", "adderall", "oxy", "syringe", "shoot up", "overdose",
            "stoned", "blazed", "strung out", "drug deal", "dealer", "bong",
            // Alcohol
            "drunk", "wasted", "hammered", "plastered", "sloshed", "booze",
            "hangover", "blackout", "tequila", "vodka", "whiskey", "whisky",
            "bourbon", "scotch", "rum", "gin", "champagne", "beer", "ale", "lager",
            // Tobacco / smoking
            "cigarette", "cigar", "tobacco", "nicotine", "vape", "vaping",
        ],

        ["Medical.Events"] =
        [
            "Medical Graphic",
            "Medical Procedures",
            "Life Events",
            "Bodily Functions/Jokes",
        ],

        ["Structural.Timestamps"] =
        [
            "Opening Credits",
            "Closing Credits",
            "Episode Recap/Outtakes",
        ],
    };

    private static readonly HashSet<string> WordListKeys =
    [
        "Language.GeneralProfanity",
        "Language.Blasphemy",
        "Language.RacialAndBigotedSlurs",
        "Language.ChildishLanguage",
        "Language.CaptionsWithProfanity",
        "SexualReferences.ExplicitWords",
        // Moved from vision — better detected in transcript than from video frames
        "SexualReferences.ContextualDialogue",
        "Substances.Usage",
    ];

    private static readonly IReadOnlyDictionary<string, string> DefaultChannels = new Dictionary<string, string>
    {
        ["Language.GeneralProfanity"]           = "audio",
        ["Language.Blasphemy"]                  = "audio",
        ["Language.RacialAndBigotedSlurs"]      = "audio",
        ["Language.ChildishLanguage"]           = "audio",
        ["Language.CaptionsWithProfanity"]      = "both",
        ["SexualReferences.ExplicitWords"]      = "audio",
        ["SexualReferences.ContextualDialogue"] = "audio",
        ["SexualReferences.Visuals"]            = "video",
        ["SexAndNudity.OnscreenActivity"]       = "video",
        ["SexAndNudity.NudityProfiles"]         = "video",
        ["SexAndNudity.PhysicalIntimacy"]       = "video",
        ["Violence.Tiers"]                      = "video",
        ["Substances.Usage"]                    = "audio",
        ["Medical.Events"]                      = "both",
        ["Structural.Timestamps"]               = "both",
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
