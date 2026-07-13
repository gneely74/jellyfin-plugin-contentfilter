using System.Collections.Concurrent;
using System.Text;
using Jellyfin.Plugin.ContentFilter.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Stores and manages per-item JCF filters.
/// </summary>
public class FilterStore
{
    private readonly ILogger<FilterStore> _logger;
    private readonly SubtitleFilter _subtitleFilter;
    private readonly ConcurrentDictionary<Guid, JcfFilter> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterStore"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="subtitleFilter">The subtitle filtering service.</param>
    public FilterStore(ILogger<FilterStore> logger, SubtitleFilter subtitleFilter)
    {
        _logger = logger;
        _subtitleFilter = subtitleFilter;
    }

    private string FiltersPath => Path.Combine(Plugin.Instance!.DataFolderPath, "filters");

    /// <summary>
    /// Loads a filter for an item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <returns>The filter if found; otherwise <see langword="null"/>.</returns>
    public JcfFilter? GetFilter(Guid itemId)
    {
        if (_cache.TryGetValue(itemId, out var cached))
        {
            return cached;
        }

        var path = GetJcfPath(itemId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, true);
            var parsed = JcfParser.Parse(reader);
            _cache[itemId] = parsed;
            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse JCF filter for item {ItemId}.", itemId);
            return null;
        }
    }

    /// <summary>
    /// Saves a filter for an item and regenerates filtered subtitles.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="filter">The filter to save.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the filter has been persisted.</returns>
    public async Task SaveFilterAsync(Guid itemId, JcfFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        Directory.CreateDirectory(FiltersPath);
        var path = GetJcfPath(itemId);
        var content = JcfWriter.Serialize(filter);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        _cache[itemId] = filter;
        await _subtitleFilter.RegenerateAsync(itemId, filter, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates a cue action and optionally cue description for an item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="cueKey">The cue key.</param>
    /// <param name="action">The action value.</param>
    /// <param name="description">An optional cue description.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when persistence has finished.</returns>
    public async Task SetCueActionAsync(Guid itemId, string cueKey, string action, string? description, CancellationToken cancellationToken)
    {
        var filter = GetFilter(itemId);
        if (filter is null)
        {
            return;
        }

        var cue = filter.Cues.FirstOrDefault(c => c.Key.Equals(cueKey, StringComparison.Ordinal));
        if (cue is null)
        {
            return;
        }

        cue.Action = action;
        cue.Description = description;
        await SaveFilterAsync(itemId, filter, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a filter for an item and associated filtered subtitle output.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    public void DeleteFilter(Guid itemId)
    {
        _cache.TryRemove(itemId, out _);

        var path = GetJcfPath(itemId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        _subtitleFilter.DeleteFilteredSubtitle(itemId);
    }

    /// <summary>
    /// Determines whether a filter exists for an item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <returns><see langword="true"/> when a filter exists; otherwise <see langword="false"/>.</returns>
    public bool HasFilter(Guid itemId)
    {
        return _cache.ContainsKey(itemId) || File.Exists(GetJcfPath(itemId));
    }

    /// <summary>
    /// Gets the on-disk JCF path for an item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <returns>The absolute JCF path.</returns>
    public string GetJcfPath(Guid itemId)
    {
        return Path.Combine(FiltersPath, $"{itemId:N}.jcf");
    }
}
