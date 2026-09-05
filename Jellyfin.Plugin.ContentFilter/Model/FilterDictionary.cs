using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ContentFilter.Models;

/// <summary>
/// Defines all content filter categories and related metadata.
/// </summary>
public static class FilterDictionary
{
    /// <summary>
    /// Builds a regular expression pattern that matches a word or phrase, including common English plural variants (e.g. "bastard" -> "bastards", "bitch" -> "bitches", "asshole" -> "assholes").
    /// </summary>
    /// <param name="term">The word or phrase.</param>
    /// <returns>A regex pattern string with whole-word boundary assertions.</returns>
    public static string BuildWordPattern(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return string.Empty;
        }

        term = term.Trim();
        if (term.Contains(' '))
        {
            return $@"\b{Regex.Escape(term)}\b";
        }

        var escaped = Regex.Escape(term);

        // Words ending in consonant + y: pussy -> pussies | pussy
        if (term.Length > 2 &&
            term.EndsWith("y", StringComparison.OrdinalIgnoreCase) &&
            !"aeiouAEIOU".Contains(term[^2]))
        {
            var stem = Regex.Escape(term[..^1]);
            return $@"\b(?:{stem}ies|{escaped})\b";
        }

        // Sibilant endings ending in ss (ass, piss, jackass) -> asses, pisses
        if (term.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
        {
            return $@"\b(?:{escaped}es|{escaped})\b";
        }

        // Words ending in already plural s (bastards, bitches, tits) -> match as-is
        if (term.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return $@"\b{escaped}\b";
        }

        // Words ending in sh, ch, x, z (bitch -> bitches)
        if (term.EndsWith("sh", StringComparison.OrdinalIgnoreCase) ||
            term.EndsWith("ch", StringComparison.OrdinalIgnoreCase) ||
            term.EndsWith("x", StringComparison.OrdinalIgnoreCase) ||
            term.EndsWith("z", StringComparison.OrdinalIgnoreCase))
        {
            return $@"\b(?:{escaped}es|{escaped})\b";
        }

        // Standard words (bastard, asshole, cunt, prick, douche, etc.) -> bastard, bastards
        return $@"\b(?:{escaped}s|{escaped})\b";
    }

    /// <summary>
    /// Gets the full set of filter categories and their terms or descriptions.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Categories { get; } = new Dictionary<string, string[]>
    {
        // --- 1. LANGUAGE & PROFANITY (subtitle word match) ---
        ["Language.GeneralProfanity"] =
        [
            "arse", "arses", "ass", "asses", "asshole", "assholes", "bastard", "bastards",
            "bitch", "bitches", "bloody", "bollocks", "bugger", "buggers", "bullshit", "bullshits",
            "crap", "craps", "cunt", "cunts", "damn", "damns", "dick", "dicks", "dickhead", "dickheads",
            "dipshit", "dipshits", "douche", "douches", "douchebag", "douchebags", "fuck", "fucks",
            "fucking", "fucker", "fuckers", "hell", "horseshit", "jackass", "jackasses",
            "motherfucker", "motherfuckers", "motherfucking", "piss", "pisses", "prick", "pricks",
            "screw", "screws", "shit", "shits", "wank", "wanks", "wanker", "wankers",
        ],

        ["Language.Blasphemy"] =
        [
            "Jesus Christ", "Oh God", "God damn", "Holy shit",
        ],

        ["Language.RacialAndBigotedSlurs"] =
        [
            "chink", "chinks", "cracker", "crackers", "fag", "fags", "heeb", "heebs",
            "jap", "japs", "jiz", "kike", "kikes", "kraut", "krauts", "nigger", "niggers",
            "pollack", "pollacks", "wetback", "wetbacks", "wop", "wops",
        ],

        ["Language.ChildishLanguage"] =
        [
            "bum", "bums", "butt", "butts", "dumb", "fart", "farts", "poop", "poops", "stupid",
        ],

        // --- 2. SEXUAL REFERENCES (subtitle word match + Ollama visual) ---
        ["SexualReferences.ExplicitWords"] =
        [
            "anus", "anuses", "balls", "beastial", "blowjob", "blowjobs", "clit", "clits",
            "cock", "cocks", "condom", "condoms", "cum", "cums", "cunillingus", "dick", "dicks",
            "dildo", "dildos", "dink", "dinks", "douche", "douches", "ejaculate", "ejaculates",
            "fag", "fags", "fellatio", "gangbang", "gangbangs", "hard on", "horniest", "hump", "humps",
            "jerk", "jerks", "kooch", "masturbate", "nuts", "orgasm", "orgasms", "picker",
            "penis", "penises", "porn", "prick", "pricks", "piss", "pussy", "pussies",
            "queer", "queers", "rimjob", "rimjobs", "scrotum", "sex", "skeet", "slut", "sluts",
            "testicle", "testicles", "tits", "twat", "twats", "vagina", "vaginas", "wank", "wanks",
            "whore", "whores",
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
        ["SexAndNudity.Graphic"] =
        [
            "Explicit Sexual Intercourse",
            "Sex with Nudity",
            "Oral Sex",
            "Masturbation",
            "Erotic Acts",
        ],

        ["SexAndNudity.ImpliedSex"] =
        [
            "Sex without Nudity",
            "Implied Sex",
            "Sexually Suggestive Activity",
            "Sensual Bedroom Scenes",
            "Suggestive Dancing",
        ],

        ["SexAndNudity.SexualAssault"] =
        [
            "Sexual Assault",
            "Rape",
            "Non-consensual Sexual Behavior",
            "Molestation",
        ],

        ["SexAndNudity.FullNudity"] =
        [
            "Female Frontal Nudity",
            "Male Frontal Nudity",
            "Full Nudity",
            "Unclad Exposure",
        ],

        ["SexAndNudity.PartialNudity"] =
        [
            "Underwear or Lingerie",
            "Topless Silhouette",
            "Bare Buttocks",
            "Revealing Attire or Cleavage",
            "Swimwear & Beach Attire",
            "Immodest Exposure",
        ],

        ["SexAndNudity.PhysicalIntimacy"] =
        [
            "Passionate Kissing",
            "Prolonged Making Out",
            "Sensual Caressing",
        ],

        ["SexAndNudity.Mild"] =
        [
            "Swimwear & Beach Attire",
            "Mild Immodesty",
            "Brief Non-sexual Kissing",
            "Nude Statues & Paintings",
        ],

        // Legacy aliases:
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

        // --- 4. VIOLENCE & HORROR (Ollama visual) ---
        ["Violence.Mild"] =
        [
            "Comic Action",
            "Slaps",
            "Bloodless Fistfights",
            "Playful Wrestling or Shoving",
        ],

        ["Violence.Moderate"] =
        [
            "Realistic Combat",
            "Shootouts",
            "Stabbings",
            "Non-graphic Violence",
            "Blood on Clothing",
            "Car Crashes and Explosions",
        ],

        ["Violence.Graphic"] =
        [
            "Visceral Wounds",
            "Severe Physical Trauma",
            "Graphic Violence",
            "Close-range Shootings",
            "Fatal Stabbings",
            "Brutal Attacks",
        ],

        ["Violence.Gore"] =
        [
            "Gore",
            "Dismemberment",
            "Decapitation",
            "Severed Limbs or Heads",
            "Mutilation",
            "Exposed Organs or Entrails",
            "Graphic Blood Splatter",
        ],

        ["Violence.JumpScares"] =
        [
            "Horror Jump Scares",
            "Sudden Shock Sequences",
            "Startling Creature Reveals",
            "Startling Sounds with Visual Jump",
        ],

        ["Violence.Disturbing"] =
        [
            "Disturbing Images",
            "Psychological Horror",
            "Corpses and Skeletal Remains",
            "Suicide or Self-Harm Themes",
            "Torture Sequences",
            "Objectionable, Disturbing, or Scary",
        ],

        // Legacy alias:
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
        ["Substances.Tobacco"] =
        [
            "Smoking Cigarettes",
            "Smoking Cigars or Pipes",
            "Vaping or E-Cigarettes",
            "Tobacco Product Use",
        ],

        ["Substances.Alcohol"] =
        [
            "Alcohol Consumption",
            "Drunkenness and Intoxication",
            "Beer, Wine, or Liquor Drinking",
            "Bar Scenes with Heavy Drinking",
        ],

        ["Substances.IllegalDrugs"] =
        [
            "Illicit Drug Use",
            "Narcotics Consumption",
            "Smoking Marijuana / Weed",
            "Snorting Cocaine / Heroin",
            "Drug Injections and Paraphernalia",
            "Drug Overdose or Intoxication",
        ],

        // Legacy alias:
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
            "Surgical Operations",
            "Needles and Injections",
            "Severe Hospital Trauma",
        ],

        ["Medical.BodilyFunctions"] =
        [
            "Vomiting or Barfing",
            "Gross Bodily Functions",
            "Flatulence and Toilet Humor",
        ],

        // --- 7. STRUCTURAL TIMESTAMPS (Ollama visual) ---
        ["Structural.Credits"] =
        [
            "Opening Credits",
            "Closing Credits",
        ],

        ["Structural.IntroRecap"] =
        [
            "Opening Intro Sequence",
            "Episode Recap",
            "Outtakes and Bloopers",
        ],

        // Legacy alias:
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
        "SexualReferences.ExplicitWords",
    ];

    /// <summary>
    /// Gets the canonical mapping for legacy categories to modern IMDb/VidAngel standards.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> LegacyAliases { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Violence.Tiers"] = ["Violence.Graphic", "Violence.Gore", "Violence.Moderate", "Violence.Disturbing"],
        ["SexAndNudity.OnscreenActivity"] = ["SexAndNudity.Graphic", "SexAndNudity.ImpliedSex", "SexAndNudity.SexualAssault"],
        ["SexAndNudity.NudityProfiles"] = ["SexAndNudity.FullNudity", "SexAndNudity.PartialNudity"],
        ["SexAndNudity.Mild"] = ["SexAndNudity.PartialNudity"],
        ["SexualReferences.Visuals"] = ["SexualReferences.ContextualDialogue"],
        ["Substances.Usage"] = ["Substances.IllegalDrugs", "Substances.Alcohol", "Substances.Tobacco"],
        ["Structural.Timestamps"] = ["Structural.Credits", "Structural.IntroRecap"],
        ["Language.CaptionsWithProfanity"] = ["Language.GeneralProfanity"],
    };

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

        ["SexAndNudity.Graphic"]                   = "video",
        ["SexAndNudity.ImpliedSex"]                = "video",
        ["SexAndNudity.SexualAssault"]             = "video",
        ["SexAndNudity.FullNudity"]                = "video",
        ["SexAndNudity.PartialNudity"]             = "video",
        ["SexAndNudity.PhysicalIntimacy"]          = "video",
        ["SexAndNudity.Mild"]                      = "video",
        ["SexAndNudity.OnscreenActivity"]          = "video",
        ["SexAndNudity.NudityProfiles"]            = "video",

        ["Violence.Mild"]                          = "video",
        ["Violence.Moderate"]                      = "video",
        ["Violence.Graphic"]                       = "video",
        ["Violence.Gore"]                          = "video",
        ["Violence.JumpScares"]                    = "video",
        ["Violence.Disturbing"]                    = "video",
        ["Violence.Tiers"]                         = "video",

        ["Substances.Tobacco"]                     = "video",
        ["Substances.Alcohol"]                     = "video",
        ["Substances.IllegalDrugs"]                = "video",
        ["Substances.Usage"]                       = "video",

        ["Medical.Events"]                         = "both",
        ["Medical.BodilyFunctions"]                = "both",

        ["Structural.Credits"]                     = "both",
        ["Structural.IntroRecap"]                  = "both",
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
