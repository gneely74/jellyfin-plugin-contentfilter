using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ContentFilter.Models;
using Jellyfin.Plugin.ContentFilter.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ContentFilter.Api;

/// <summary>
/// Provides API endpoints for managing content filters and filtered subtitles.
/// </summary>
[ApiController]
[Route("ContentFilter")]
[Authorize]
public class ContentFilterController : ControllerBase
{
    private readonly FilterStore _filterStore;
    private readonly SubtitleFilter _subtitleFilter;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentFilterController"/> class.
    /// </summary>
    /// <param name="filterStore">The filter store service.</param>
    /// <param name="subtitleFilter">The subtitle filter service.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    public ContentFilterController(FilterStore filterStore, SubtitleFilter subtitleFilter, ILibraryManager libraryManager)
    {
        _filterStore = filterStore;
        _subtitleFilter = subtitleFilter;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets a filter for a media item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The filter metadata and cues for the item.</returns>
    [HttpGet("filters/{itemId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> GetFilterAsync(Guid itemId, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var filter = _filterStore.GetFilter(itemId);
        if (filter is null)
        {
            return NotFound();
        }

        var cues = filter.Cues
            .Select(cue => new
            {
                key = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{FormatTimestamp(cue.Start)}-{FormatTimestamp(cue.End)}-{cue.Category}"),
                start = FormatTimestamp(cue.Start),
                end = FormatTimestamp(cue.End),
                description = cue.Description,
                category = cue.Category,
                channel = cue.Channel,
                action = cue.Action
            })
            .ToList();

        return Ok(new
        {
            title = filter.Title,
            year = filter.Year,
            imdbUrl = filter.ImdbUrl,
            cues
        });
    }

    /// <summary>
    /// Searches library items by title or name.
    /// </summary>
    /// <param name="query">The search term.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <returns>A collection of matching library items.</returns>
    [HttpGet("items/search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> SearchItems([FromQuery] string? query, [FromQuery] int limit = 15)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Ok(Array.Empty<object>());
        }

        var internalQuery = new InternalItemsQuery
        {
            SearchTerm = query.Trim(),
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
            Recursive = true,
            Limit = Math.Clamp(limit, 1, 50)
        };

        var items = _libraryManager.GetItemList(internalQuery);
        var results = items.Select(item =>
        {
            var seriesName = item is MediaBrowser.Controller.Entities.TV.Episode ep ? ep.SeriesName : null;
            var seasonNum = item is MediaBrowser.Controller.Entities.TV.Episode ep2 ? ep2.ParentIndexNumber : null;
            var epNum = item is MediaBrowser.Controller.Entities.TV.Episode ep3 ? ep3.IndexNumber : null;

            return new
            {
                id = item.Id,
                name = item.Name,
                year = item.ProductionYear,
                type = item.GetType().Name,
                seriesName,
                seasonNumber = seasonNum,
                episodeNumber = epNum,
                hasFilter = _filterStore.HasFilter(item.Id),
                hasSidecar = _filterStore.GetSidecarPath(item.Id) is not null
            };
        });

        return Ok(results);
    }

    /// <summary>
    /// Uploads and saves a JCF filter file for a media item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="file">The uploaded JCF file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An action result.</returns>
    [HttpPost("filters/{itemId:guid}")]
    [Consumes("multipart/form-data", "text/plain", "application/octet-stream", "text/vtt")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadFilterAsync(Guid itemId, IFormFile? file, CancellationToken cancellationToken)
    {
        try
        {
            var filter = await ParseIncomingFilterAsync(file, cancellationToken).ConfigureAwait(false);
            await _filterStore.SaveFilterAsync(itemId, filter, cancellationToken).ConfigureAwait(false);
            return Ok(new
            {
                itemId,
                cues = filter.Cues.Count,
                title = filter.Title,
                categories = filter.Cues.GroupBy(c => c.Category).ToDictionary(g => g.Key, g => g.Count())
            });
        }
        catch (FormatException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Smart import endpoint that accepts a JCF file and auto-matches it to a media item if itemId is omitted.
    /// </summary>
    /// <param name="file">The uploaded JCF file.</param>
    /// <param name="itemId">Optional explicit item identifier.</param>
    /// <param name="saveIfMatched">Whether to automatically save the filter when a unique match is found.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Import or match result with preview breakdown.</returns>
    [HttpPost("filters/import")]
    [Consumes("multipart/form-data", "text/plain", "application/octet-stream", "text/vtt")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportFilterAsync(
        IFormFile? file,
        [FromQuery] Guid? itemId,
        [FromQuery] bool saveIfMatched = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var filter = await ParseIncomingFilterAsync(file, cancellationToken).ConfigureAwait(false);
            var categories = filter.Cues.GroupBy(c => c.Category).ToDictionary(g => g.Key, g => g.Count());

            // If an itemId is explicitly provided, save directly
            if (itemId.HasValue)
            {
                await _filterStore.SaveFilterAsync(itemId.Value, filter, cancellationToken).ConfigureAwait(false);
                var targetItem = _libraryManager.GetItemById(itemId.Value);
                return Ok(new
                {
                    matched = true,
                    saved = true,
                    item = new
                    {
                        id = itemId.Value,
                        name = targetItem?.Name ?? filter.Title,
                        year = targetItem?.ProductionYear
                    },
                    cues = filter.Cues.Count,
                    title = filter.Title,
                    year = filter.Year,
                    categories
                });
            }

            // Auto-match based on IMDB ID, Title/Year, and filename
            var candidates = FindMatchingItems(filter, file?.FileName);
            if (candidates.Count == 1)
            {
                var matchedItem = candidates[0];
                if (saveIfMatched)
                {
                    await _filterStore.SaveFilterAsync(matchedItem.Id, filter, cancellationToken).ConfigureAwait(false);
                }

                return Ok(new
                {
                    matched = true,
                    saved = saveIfMatched,
                    item = new
                    {
                        id = matchedItem.Id,
                        name = matchedItem.Name,
                        year = matchedItem.ProductionYear,
                        type = matchedItem.GetType().Name
                    },
                    cues = filter.Cues.Count,
                    title = filter.Title,
                    year = filter.Year,
                    categories
                });
            }

            // Ambiguous or no match: return candidates and preview
            return Ok(new
            {
                matched = false,
                saved = false,
                candidates = candidates.Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    year = c.ProductionYear,
                    type = c.GetType().Name
                }),
                preview = new
                {
                    title = filter.Title,
                    year = filter.Year,
                    imdbUrl = filter.ImdbUrl,
                    cues = filter.Cues.Count,
                    categories
                }
            });
        }
        catch (FormatException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Scans the library for media items that have adjacent .jcf sidecar files on disk.
    /// </summary>
    /// <returns>A summary of discovered sidecar filters.</returns>
    [HttpPost("filters/scan-sidecars")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> ScanSidecars()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
            Recursive = true
        };

        var items = _libraryManager.GetItemList(query);
        var discovered = 0;
        var details = new List<object>();

        foreach (var item in items)
        {
            var sidecar = _filterStore.GetSidecarPath(item.Id);
            if (sidecar is not null)
            {
                discovered++;
                var filter = _filterStore.GetFilter(item.Id);
                details.Add(new
                {
                    id = item.Id,
                    name = item.Name,
                    path = sidecar,
                    cues = filter?.Cues.Count ?? 0
                });
            }
        }

        return Ok(new { total = discovered, items = details });
    }

    /// <summary>
    /// Deletes a filter for a media item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <returns>An action result.</returns>
    [HttpDelete("filters/{itemId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult DeleteFilter(Guid itemId)
    {
        _filterStore.DeleteFilter(itemId);
        return NoContent();
    }

    /// <summary>
    /// Downloads the JCF filter file for a media item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A file response containing the JCF file.</returns>
    [HttpGet("filters/{itemId:guid}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadFilterAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var filter = _filterStore.GetFilter(itemId);
        if (filter is null)
        {
            return NotFound();
        }
        var path = _filterStore.GetEffectiveFilterPath(itemId);
        if (path is null || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var fallbackTitle = _libraryManager.GetItemById(itemId)?.Name ?? itemId.ToString("N", CultureInfo.InvariantCulture);
        var rawTitle = string.IsNullOrWhiteSpace(filter.Title) ? fallbackTitle : filter.Title;
        var safeTitle = string.Join("_", rawTitle.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safeTitle))
        {
            safeTitle = itemId.ToString("N", CultureInfo.InvariantCulture);
        }

        return File(bytes, "text/plain", $"{safeTitle}.jcf");
    }

    /// <summary>
    /// Updates the action and optional description for a cue.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="request">The cue action update request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An action result.</returns>
    [HttpPut("filters/{itemId:guid}/segments/action")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetCueActionAsync(Guid itemId, [FromBody] SetCueActionRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.CueKey) || string.IsNullOrWhiteSpace(request.Action))
        {
            return BadRequest("cueKey and action are required.");
        }

        await _filterStore
            .SetCueActionAsync(itemId, request.CueKey, request.Action, request.Description, cancellationToken)
            .ConfigureAwait(false);
        return Ok();
    }

    /// <summary>
    /// Gets the filtered subtitle file for a media item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <returns>A subtitle file response if available.</returns>
    [AllowAnonymous]
    [HttpGet("subtitles/{itemId:guid}.srt")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetFilteredSubtitle(Guid itemId)
    {
        if (!_subtitleFilter.HasFilteredSubtitle(itemId))
        {
            return NotFound();
        }

        var path = _subtitleFilter.GetFilteredSrtPath(itemId);
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        return PhysicalFile(path, "text/plain");
    }

    private static string FormatTimestamp(TimeSpan value)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}");
    }

    private async Task<JcfFilter> ParseIncomingFilterAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is not null && file.Length > 0)
        {
            await using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream);
            return JcfParser.Parse(reader);
        }

        if (Request.ContentLength is > 0 &&
            Request.ContentType is not null &&
            !Request.ContentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(Request.Body);
            return JcfParser.Parse(reader);
        }

        throw new FormatException("A JCF file is required. Send multipart form field 'file' or raw text starting with WEBVTT.");
    }

    private List<MediaBrowser.Controller.Entities.BaseItem> FindMatchingItems(JcfFilter filter, string? filename)
    {
        var candidates = new List<MediaBrowser.Controller.Entities.BaseItem>();

        // 1. Try matching via season/episode in filename or title (e.g. S01E03)
        var textToSearch = $"{filename} {filter.Title}";
        var seMatch = System.Text.RegularExpressions.Regex.Match(textToSearch, @"[sS](?<season>\d{1,2})[eE](?<ep>\d{1,2})");
        if (seMatch.Success)
        {
            var seasonNumber = int.Parse(seMatch.Groups["season"].Value, CultureInfo.InvariantCulture);
            var episodeNumber = int.Parse(seMatch.Groups["ep"].Value, CultureInfo.InvariantCulture);

            var titleBase = System.Text.RegularExpressions.Regex.Replace(filter.Title ?? string.Empty, @"[sS]\d{1,2}[eE]\d{1,2}.*$", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(titleBase) && !string.IsNullOrWhiteSpace(filename))
            {
                titleBase = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(filename), @"[sS]\d{1,2}[eE]\d{1,2}.*$", string.Empty).Trim(' ', '.', '_', '-');
            }

            var epQuery = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                IndexNumber = episodeNumber,
                ParentIndexNumber = seasonNumber,
                Recursive = true
            };

            var episodes = _libraryManager.GetItemList(epQuery);
            if (!string.IsNullOrWhiteSpace(titleBase) && episodes.Count > 0)
            {
                var filteredBySeries = episodes
                    .Where(e => e is MediaBrowser.Controller.Entities.TV.Episode ep &&
                                (ep.SeriesName?.Contains(titleBase, StringComparison.OrdinalIgnoreCase) == true ||
                                 titleBase.Contains(ep.SeriesName ?? "", StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (filteredBySeries.Count > 0)
                {
                    return filteredBySeries;
                }
            }

            if (episodes.Count == 1)
            {
                return episodes.ToList();
            }

            if (episodes.Count > 0)
            {
                candidates.AddRange(episodes.Take(5));
            }
        }

        // 2. Match by Title and optional Year
        var rawSearch = !string.IsNullOrWhiteSpace(filter.Title)
            ? filter.Title.Trim()
            : (!string.IsNullOrWhiteSpace(filename) ? Path.GetFileNameWithoutExtension(filename) : string.Empty);

        if (!string.IsNullOrWhiteSpace(rawSearch))
        {
            var query = new InternalItemsQuery
            {
                SearchTerm = rawSearch,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
                Recursive = true,
                Limit = 10
            };

            var items = _libraryManager.GetItemList(query);
            if (int.TryParse(filter.Year, out var year) && year > 1900)
            {
                var yearMatch = items.Where(i => i.ProductionYear == year).ToList();
                if (yearMatch.Count > 0)
                {
                    return yearMatch;
                }
            }

            candidates.AddRange(items);
        }

        return candidates.DistinctBy(c => c.Id).Take(10).ToList();
    }
}

/// <summary>
/// Request payload for updating a cue action.
/// </summary>
public sealed class SetCueActionRequest
{
    /// <summary>
    /// Gets or sets the cue key.
    /// </summary>
    public required string CueKey { get; set; }

    /// <summary>
    /// Gets or sets the cue action.
    /// </summary>
    public required string Action { get; set; }

    /// <summary>
    /// Gets or sets the optional cue description.
    /// </summary>
    public string? Description { get; set; }
}
