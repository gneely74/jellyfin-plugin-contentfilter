using System.Collections.Concurrent;
using System.Text;
using Jellyfin.Plugin.ContentFilter.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Stores and manages per-item JCF filters.
/// </summary>
public class FilterStore
{
    private readonly ILogger<FilterStore> _logger;
    private readonly SubtitleFilter _subtitleFilter;
    private readonly ILibraryManager _libraryManager;
    private readonly ConcurrentDictionary<Guid, JcfFilter> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterStore"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="subtitleFilter">The subtitle filtering service.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    public FilterStore(ILogger<FilterStore> logger, SubtitleFilter subtitleFilter, ILibraryManager libraryManager)
    {
        _logger = logger;
        _subtitleFilter = subtitleFilter;
        _libraryManager = libraryManager;
    }

    private string FiltersPath => Path.Combine(Plugin.Instance!.DataFolderPath, "filters");

    /// <summary>
    /// Loads a filter for an item, checking memory cache, custom filters folder, and disk sidecars.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <returns>The filter if found; otherwise <see langword="null"/>.</returns>
    public JcfFilter? GetFilter(Guid itemId)
    {
        if (_cache.TryGetValue(itemId, out var cached))
        {
            return cached;
        }

        var path = GetEffectiveFilterPath(itemId);
        if (path is null || !File.Exists(path))
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
    /// Updates the action for all cues in an item's filter in a single save.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="action">The action value to apply to all cues.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when persistence has finished.</returns>
    public async Task SetBulkCueActionAsync(Guid itemId, string action, CancellationToken cancellationToken)
    {
        var filter = GetFilter(itemId);
        if (filter is null)
        {
            return;
        }

        foreach (var cue in filter.Cues)
        {
            cue.Action = action;
        }

        await SaveFilterAsync(itemId, filter, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a specific cue from an item's filter.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="cueKey">The cue key to remove.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when the cue was removed; otherwise <see langword="false"/>.</returns>
    public async Task<bool> DeleteCueAsync(Guid itemId, string cueKey, CancellationToken cancellationToken)
    {
        var filter = GetFilter(itemId);
        if (filter is null)
        {
            return false;
        }

        var removed = filter.Cues.RemoveAll(c => c.Key.Equals(cueKey, StringComparison.Ordinal));
        if (removed > 0)
        {
            await SaveFilterAsync(itemId, filter, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Adds a new cue to an item's filter.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="cue">The cue to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when persistence has finished.</returns>
    public async Task AddCueAsync(Guid itemId, FilterCue cue, CancellationToken cancellationToken)
    {
        var filter = GetFilter(itemId);
        if (filter is null)
        {
            filter = new JcfFilter
            {
                Title = _libraryManager.GetItemById(itemId)?.Name ?? "Filtered Item"
            };
        }

        filter.Cues.Add(cue);
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
        return _cache.ContainsKey(itemId) || File.Exists(GetJcfPath(itemId)) || GetSidecarPath(itemId) is not null;
    }

    /// <summary>
    /// Gets the custom on-disk JCF path in the plugin data folder for an item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <returns>The absolute JCF path.</returns>
    public string GetJcfPath(Guid itemId)
    {
        return Path.Combine(FiltersPath, $"{itemId:N}.jcf");
    }

    /// <summary>
    /// Gets the sidecar JCF file path adjacent to the media file on disk, if it exists.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <returns>The sidecar path if found; otherwise <see langword="null"/>.</returns>
    public string? GetSidecarPath(Guid itemId)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item || string.IsNullOrWhiteSpace(item.Path))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(item.Path);
        var stem = Path.GetFileNameWithoutExtension(item.Path);
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        string[] candidates =
        [
            Path.Combine(dir, $"{stem}.jcf"),
            Path.Combine(dir, $"{stem}.JCF"),
            Path.ChangeExtension(item.Path, ".jcf"),
            Path.ChangeExtension(item.Path, ".JCF")
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Gets the effective JCF path for an item, preferring user-customized plugin filters over disk sidecars.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <returns>The effective JCF path if found; otherwise <see langword="null"/>.</returns>
    public string? GetEffectiveFilterPath(Guid itemId)
    {
        var customPath = GetJcfPath(itemId);
        if (File.Exists(customPath))
        {
            return customPath;
        }

        return GetSidecarPath(itemId);
    }
}
