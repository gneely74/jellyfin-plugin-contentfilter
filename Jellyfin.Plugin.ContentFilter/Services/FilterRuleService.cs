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

        // Specific item disabled check
        if (!string.IsNullOrEmpty(cue.Description))
        {
            var itemKey = $"{category}:{cue.Description}";
            if (itemOverride.DisabledFilterItems.Any(d => string.Equals(d, itemKey, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        // Category explicitly disabled in item override
        if (itemOverride.DisabledCategories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Category explicitly enabled in item override (even if globally disabled)
        if (itemOverride.EnabledCategories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase)))
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
            "SexualReferences" => config.SexualReferencesEnabled,
            "SexAndNudity" => config.SexAndNudityEnabled,
            "Violence" => config.ViolenceEnabled,
            "Substances" => config.SubstancesEnabled,
            "Medical" => config.MedicalEnabled,
            "Structural" => config.StructuralEnabled,
            _ => true
        };

        if (!groupEnabled)
        {
            return false;
        }

        // Check category-level disabled list
        if (config.DisabledCategories != null &&
            config.DisabledCategories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Check specific item term disabled list
        if (!string.IsNullOrEmpty(cue.Description) && config.DisabledFilterItems != null)
        {
            var itemKey = $"{category}:{cue.Description}";
            if (config.DisabledFilterItems.Any(d => string.Equals(d, itemKey, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
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
