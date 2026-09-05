using System.Text.RegularExpressions;
using Jellyfin.Plugin.ContentFilter.Configuration;
using Jellyfin.Plugin.ContentFilter.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Service responsible for evaluating and resolving effective filter rules,
/// supporting global defaults, series-level inheritance, and item-level overrides.
/// </summary>
public class FilterRuleService
{
    private readonly ILogger<FilterRuleService> _logger;
    private readonly SqliteFilterRepository _repository;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterRuleService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="repository">The SQLite filter repository.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    public FilterRuleService(
        ILogger<FilterRuleService> logger,
        SqliteFilterRepository repository,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _repository = repository;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Determines whether a specific filter cue is enabled for playback on the specified item.
    /// </summary>
    /// <param name="cue">The filter cue.</param>
    /// <param name="itemId">The media item identifier.</param>
    /// <returns><see langword="true"/> if the cue is active/enabled for filtering; otherwise <see langword="false"/>.</returns>
    public bool IsCueEnabled(FilterCue cue, Guid itemId)
    {
        ArgumentNullException.ThrowIfNull(cue);

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.IsEnabled)
        {
            return false;
        }

        // 1. Check item-specific override
        var itemOverride = _repository.GetItemFilterOverride(itemId);
        if (itemOverride is not null && itemOverride.IsCustom)
        {
            var result = EvaluateCueAgainstOverride(cue, itemOverride, config);
            if (result.HasValue)
            {
                return result.Value;
            }
        }

        // 2. If item is an episode without its own override, check parent series override
        var parentSeriesId = ResolveParentSeriesId(itemId);
        if (parentSeriesId.HasValue && parentSeriesId.Value != Guid.Empty)
        {
            var seriesOverride = _repository.GetItemFilterOverride(parentSeriesId.Value);
            if (seriesOverride is not null && seriesOverride.IsCustom)
            {
                var result = EvaluateCueAgainstOverride(cue, seriesOverride, config);
                if (result.HasValue)
                {
                    return result.Value;
                }
            }
        }

        // 3. Fallback to global plugin configuration
        return EvaluateCueAgainstGlobal(cue, config);
    }

    /// <summary>
    /// Resolves the effective filter rules and their source for a media item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <returns>An object describing the effective rules, override state, and source.</returns>
    public EffectiveRulesResult GetEffectiveRules(Guid itemId)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        // 1. Check direct item override
        var directOverride = _repository.GetItemFilterOverride(itemId);
        if (directOverride is not null && directOverride.IsCustom)
        {
            return new EffectiveRulesResult
            {
                ItemId = itemId,
                Source = "ItemCustom",
                SourceDescription = "Custom rules for this item",
                IsCustom = true,
                DisabledCategories = directOverride.DisabledCategories,
                EnabledCategories = directOverride.EnabledCategories,
                DisabledFilterItems = directOverride.DisabledFilterItems
            };
        }

        // 2. Check series-level inheritance for episodes
        var parentSeriesId = ResolveParentSeriesId(itemId);
        if (parentSeriesId.HasValue && parentSeriesId.Value != Guid.Empty)
        {
            var seriesOverride = _repository.GetItemFilterOverride(parentSeriesId.Value);
            if (seriesOverride is not null && seriesOverride.IsCustom)
            {
                var seriesItem = _libraryManager.GetItemById(parentSeriesId.Value);
                var seriesTitle = seriesItem?.Name ?? "Series";
                return new EffectiveRulesResult
                {
                    ItemId = itemId,
                    ParentId = parentSeriesId.Value,
                    Source = "SeriesInherited",
                    SourceDescription = $"Inherited from {seriesTitle}",
                    IsCustom = false,
                    DisabledCategories = seriesOverride.DisabledCategories,
                    EnabledCategories = seriesOverride.EnabledCategories,
                    DisabledFilterItems = seriesOverride.DisabledFilterItems
                };
            }
        }

        // 3. Global configuration
        return new EffectiveRulesResult
        {
            ItemId = itemId,
            ParentId = parentSeriesId,
            Source = "Global",
            SourceDescription = "Inherited from global server settings",
            IsCustom = false,
            DisabledCategories = config.DisabledCategories ?? [],
            EnabledCategories = [],
            DisabledFilterItems = config.DisabledFilterItems ?? []
        };
    }

    /// <summary>
    /// Sets or updates custom filter rule overrides for a media item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="parentId">Optional parent series identifier.</param>
    /// <param name="overrideData">The filter override settings.</param>
    public void SetItemOverride(Guid itemId, Guid? parentId, ItemFilterOverride overrideData)
    {
        ArgumentNullException.ThrowIfNull(overrideData);
        var resolvedParent = parentId ?? ResolveParentSeriesId(itemId);
        _repository.SetItemFilterOverride(itemId, resolvedParent, overrideData);
    }

    /// <summary>
    /// Deletes the custom filter rule override for a media item, restoring global/series inheritance.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <returns><see langword="true"/> if an override was removed; otherwise <see langword="false"/>.</returns>
    public bool DeleteItemOverride(Guid itemId)
    {
        return _repository.DeleteItemFilterOverride(itemId);
    }

    /// <summary>
    /// Resolves the parent series ID for an item if it is an episode.
    /// </summary>
    public Guid? ResolveParentSeriesId(Guid itemId)
    {
        try
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is Episode ep && ep.SeriesId != Guid.Empty)
            {
                return ep.SeriesId;
            }

            if (item is not null)
            {
                var parent = item.GetParent();
                while (parent is not null)
                {
                    if (parent is Series s)
                    {
                        return s.Id;
                    }

                    parent = parent.GetParent();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve parent series ID for {ItemId}", itemId);
        }

        return null;
    }

    private static bool? EvaluateCueAgainstOverride(FilterCue cue, ItemFilterOverride itemOverride, PluginConfiguration config)
    {
        var category = cue.Category;

        // Specific item disabled check (supports inflections and Spoken: "word" formatting)
        if (IsFilterItemDisabled(category, cue.Description, itemOverride.DisabledFilterItems))
        {
            return false;
        }

        // Category explicitly disabled in item override
        if (IsCategoryDisabled(category, itemOverride.DisabledCategories))
        {
            return false;
        }

        // Category explicitly enabled in item override (even if globally disabled)
        if (IsCategoryEnabled(category, itemOverride.EnabledCategories))
        {
            return true;
        }

        // If override does not specify this category, fall back to global config
        return null;
    }

    private static bool EvaluateCueAgainstGlobal(FilterCue cue, PluginConfiguration config)
    {
        var category = cue.Category;
        var group = FilterDictionary.GetGroup(category);

        // Check group-level master switch
        var groupEnabled = group switch
        {
            "Language" => config.LanguageEnabled,
            "SexualReferences" => config.SexualReferencesEnabled || config.SexAndNudityEnabled,
            "SexAndNudity" => config.SexAndNudityEnabled,
            "Violence" => config.ViolenceEnabled,
            "Frightening" => config.FrighteningEnabled,
            "Substances" => config.SubstancesEnabled,
            "Medical" => config.MedicalEnabled || config.OtherEnabled,
            "Structural" => config.StructuralEnabled || config.OtherEnabled,
            "Other" => config.OtherEnabled,
            _ => true
        };

        if (!groupEnabled)
        {
            return false;
        }

        // Frightening check (JumpScares & Disturbing scenes)
        if ((string.Equals(category, "Violence.JumpScares", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(category, "Violence.Disturbing", StringComparison.OrdinalIgnoreCase)) &&
            !config.FrighteningEnabled)
        {
            return false;
        }

        // Check category-level disabled list
        if (IsCategoryDisabled(category, config.DisabledCategories))
        {
            return false;
        }

        // Check specific item term disabled list (supports inflections and Spoken: "word" formatting)
        if (IsFilterItemDisabled(category, cue.Description, config.DisabledFilterItems))
        {
            return false;
        }

        return true;
    }

    private static bool IsFilterItemDisabled(string category, string? description, IEnumerable<string>? disabledFilterItems)
    {
        if (string.IsNullOrEmpty(description) || disabledFilterItems is null)
        {
            return false;
        }

        var prefix = category + ":";
        var disabledTerms = disabledFilterItems
            .Where(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(d => d[prefix.Length..].Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        if (disabledTerms.Count == 0)
        {
            return false;
        }

        // Direct exact match with description (for visual/scene cues like "Opening Credits")
        if (disabledTerms.Any(t => string.Equals(t, description, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Extract spoken word from "Spoken: \"bastards\"" or "Spoken: bastards"
        var word = ExtractSpokenWord(description);
        if (!string.IsNullOrEmpty(word))
        {
            foreach (var term in disabledTerms)
            {
                if (string.Equals(term, word, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Check plural/variation pattern (e.g. disabled term "bastard" covers "bastards")
                var pattern = FilterDictionary.BuildWordPattern(term);
                if (!string.IsNullOrEmpty(pattern) && Regex.IsMatch(word, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ExtractSpokenWord(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var m = Regex.Match(description, "\"([^\"]+)\"");
        if (m.Success)
        {
            return m.Groups[1].Value.Trim();
        }

        if (description.StartsWith("Spoken:", StringComparison.OrdinalIgnoreCase))
        {
            return description["Spoken:".Length..].Trim();
        }

        return description.Trim();
    }

    private static bool IsCategoryDisabled(string category, IEnumerable<string>? disabledList)
    {
        if (disabledList is null)
        {
            return false;
        }

        var list = disabledList as ICollection<string> ?? disabledList.ToList();
        if (list.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Check if a legacy category disabled in config covers this category
        foreach (var (legacyKey, targets) in FilterDictionary.LegacyAliases)
        {
            if (targets.Contains(category, StringComparer.OrdinalIgnoreCase) &&
                list.Any(c => string.Equals(c, legacyKey, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        // If this cue has a legacy category, check if all mapped modern categories are disabled
        if (FilterDictionary.LegacyAliases.TryGetValue(category, out var modernTargets))
        {
            if (modernTargets.All(t => list.Any(c => string.Equals(c, t, StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCategoryEnabled(string category, IEnumerable<string>? enabledList)
    {
        if (enabledList is null)
        {
            return false;
        }

        var list = enabledList as ICollection<string> ?? enabledList.ToList();
        if (list.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Check legacy alias mappings
        foreach (var (legacyKey, targets) in FilterDictionary.LegacyAliases)
        {
            if (targets.Contains(category, StringComparer.OrdinalIgnoreCase) &&
                list.Any(c => string.Equals(c, legacyKey, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        if (FilterDictionary.LegacyAliases.TryGetValue(category, out var modernTargets))
        {
            if (modernTargets.Any(t => list.Any(c => string.Equals(c, t, StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Information about effective filter rules and their source for an item.
/// </summary>
public sealed class EffectiveRulesResult
{
    /// <summary>Gets or sets the media item identifier.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets optional parent series identifier.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>Gets or sets the source of the rules ("Global", "SeriesInherited", or "ItemCustom").</summary>
    public string Source { get; set; } = "Global";

    /// <summary>Gets or sets a user-friendly description of the source.</summary>
    public string SourceDescription { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether custom rules are active for this item.</summary>
    public bool IsCustom { get; set; }

    /// <summary>Gets or sets the disabled categories.</summary>
    public List<string> DisabledCategories { get; set; } = [];

    /// <summary>Gets or sets the enabled categories.</summary>
    public List<string> EnabledCategories { get; set; } = [];

    /// <summary>Gets or sets the disabled item terms.</summary>
    public List<string> DisabledFilterItems { get; set; } = [];
}
