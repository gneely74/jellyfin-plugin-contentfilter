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
    private readonly SubtitleWordScanner _subtitleWordScanner;
    private readonly SubtitleSyncService _subtitleSyncService;
    private readonly SqliteFilterRepository _sqliteRepository;
    private readonly ILibraryManager _libraryManager;
    private readonly FilterRuleService _filterRuleService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentFilterController"/> class.
    /// </summary>
    /// <param name="filterStore">The filter store service.</param>
    /// <param name="subtitleFilter">The subtitle filter service.</param>
    /// <param name="subtitleWordScanner">The subtitle word scanner service.</param>
    /// <param name="subtitleSyncService">The subtitle sync service.</param>
    /// <param name="sqliteRepository">The SQLite repository.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="filterRuleService">The filter rule evaluation service.</param>
    public ContentFilterController(
        FilterStore filterStore,
        SubtitleFilter subtitleFilter,
        SubtitleWordScanner subtitleWordScanner,
        SubtitleSyncService subtitleSyncService,
        SqliteFilterRepository sqliteRepository,
        ILibraryManager libraryManager,
        FilterRuleService filterRuleService)
    {
        _filterStore = filterStore;
        _subtitleFilter = subtitleFilter;
        _subtitleWordScanner = subtitleWordScanner;
        _subtitleSyncService = subtitleSyncService;
        _sqliteRepository = sqliteRepository;
        _libraryManager = libraryManager;
        _filterRuleService = filterRuleService;
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
                action = cue.Action,
                enabled = _filterRuleService.IsCueEnabled(cue, itemId)
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
    /// Gets the effective filter rules and override state for a specific media item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <returns>The effective filter rules and source metadata.</returns>
    [HttpGet("items/{itemId:guid}/rules")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetItemRules(Guid itemId)
    {
        var result = _filterRuleService.GetEffectiveRules(itemId);
        return Ok(result);
    }

    /// <summary>
    /// Sets or updates custom filter rule overrides for a media item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="overrideData">The filter rules override payload.</param>
    /// <returns>An action result indicating success.</returns>
    [HttpPost("items/{itemId:guid}/rules")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult SetItemRules(Guid itemId, [FromBody] ItemFilterOverride overrideData)
    {
        ArgumentNullException.ThrowIfNull(overrideData);
        _filterRuleService.SetItemOverride(itemId, null, overrideData);
        return Ok(new { success = true });
    }

    /// <summary>
    /// Deletes custom filter rule overrides for a media item, reverting to inheritance.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <returns>An action result indicating success.</returns>
    [HttpDelete("items/{itemId:guid}/rules")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult DeleteItemRules(Guid itemId)
    {
        _filterRuleService.DeleteItemOverride(itemId);
        return Ok(new { success = true });
    }

    /// <summary>
    /// Searches library items by title or name (including Series, Movies, and Episodes).
    /// </summary>
    /// <param name="query">The search term.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <returns>A collection of matching library items.</returns>
    [HttpGet("items/search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> SearchItems([FromQuery] string? query, [FromQuery] int limit = 25)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Ok(Array.Empty<object>());
        }

        var internalQuery = new InternalItemsQuery
        {
            SearchTerm = query.Trim(),
            IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
            Recursive = true,
            Limit = Math.Clamp(limit, 1, 100)
        };

        var items = _libraryManager.GetItemList(internalQuery);
        var results = items.Select(item =>
        {
            var ep = item as MediaBrowser.Controller.Entities.TV.Episode;
            var series = item as MediaBrowser.Controller.Entities.TV.Series;
            var summary = _filterStore.GetFilterSummary(item.Id);

            return new
            {
                id = item.Id,
                name = item.Name,
                year = item.ProductionYear,
                type = series is not null ? "Series" : (ep is not null ? "Episode" : item.GetType().Name),
                seriesName = ep?.SeriesName,
                seriesId = ep?.SeriesId,
                seasonNumber = ep?.ParentIndexNumber,
                episodeNumber = ep?.IndexNumber,
                hasFilter = summary.HasFilter,
                hasSidecar = summary.HasSidecar,
                cuesCount = summary.CuesCount
            };
        });

        return Ok(results);
    }

    /// <summary>
    /// Gets all TV series in the library.
    /// </summary>
    /// <returns>A list of TV series.</returns>
    [HttpGet("library/shows")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetShows()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = true
        };

        var shows = _libraryManager.GetItemList(query)
            .OfType<MediaBrowser.Controller.Entities.TV.Series>()
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                id = s.Id,
                name = s.Name,
                year = s.ProductionYear
            })
            .ToList();

        return Ok(shows);
    }

    /// <summary>
    /// Gets all movies in the library with filter status.
    /// </summary>
    /// <returns>A list of movies with filter status.</returns>
    [HttpGet("library/movies")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetMovies()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true
        };

        var movies = _libraryManager.GetItemList(query)
            .OrderBy(m => m.Name)
            .Select(m =>
            {
                var summary = _filterStore.GetFilterSummary(m.Id);
                return new
                {
                    id = m.Id,
                    name = m.Name,
                    year = m.ProductionYear,
                    hasFilter = summary.HasFilter,
                    hasSidecar = summary.HasSidecar,
                    cuesCount = summary.CuesCount
                };
            })
            .ToList();

        return Ok(movies);
    }

    /// <summary>
    /// Gets all seasons and episodes for a series with filter status.
    /// </summary>
    /// <param name="seriesId">The series identifier.</param>
    /// <returns>A hierarchical structure of seasons and episodes.</returns>
    [HttpGet("series/{seriesId:guid}/episodes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetSeriesEpisodes(Guid seriesId)
    {
        var series = _libraryManager.GetItemById(seriesId) as MediaBrowser.Controller.Entities.TV.Series;
        if (series is null)
        {
            return NotFound();
        }

        var epQuery = new InternalItemsQuery
        {
            ParentId = seriesId,
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true
        };

        var allEpisodes = _libraryManager.GetItemList(epQuery)
            .OfType<MediaBrowser.Controller.Entities.TV.Episode>()
            .OrderBy(e => e.ParentIndexNumber ?? 0)
            .ThenBy(e => e.IndexNumber ?? 0)
            .ToList();

        var seasons = allEpisodes
            .GroupBy(e => e.ParentIndexNumber ?? 0)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                seasonNumber = g.Key,
                seasonName = g.Key == 0 ? "Specials" : $"Season {g.Key}",
                episodes = g.Select(ep =>
                {
                    var summary = _filterStore.GetFilterSummary(ep.Id);
                    return new
                    {
                        id = ep.Id,
                        name = ep.Name,
                        seasonNumber = ep.ParentIndexNumber,
                        episodeNumber = ep.IndexNumber,
                        hasFilter = summary.HasFilter,
                        hasSidecar = summary.HasSidecar,
                        cuesCount = summary.CuesCount
                    };
                }).ToList()
            }).ToList();

        var totalEpisodes = allEpisodes.Count;
        var filteredEpisodes = seasons.Sum(s => s.episodes.Count(e => e.hasFilter));

        return Ok(new
        {
            seriesId,
            seriesName = series?.Name ?? "Unknown Series",
            year = series?.ProductionYear,
            totalEpisodes,
            filteredEpisodes,
            seasons
        });
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
    /// <param name="seriesId">Optional series identifier for scoped episode matching.</param>
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
        [FromQuery] Guid? seriesId,
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
            var candidates = FindMatchingItems(filter, file?.FileName, seriesId);
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
    /// <returns>A summary of discovered sidecar filters and database sync status.</returns>
    [HttpPost("filters/scan-sidecars")]
    [HttpGet("sidecars/scan")]
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
        var inDbCount = 0;
        var details = new List<object>();

        foreach (var item in items)
        {
            var sidecar = _filterStore.GetSidecarPath(item.Id);
            if (sidecar is not null)
            {
                discovered++;
                var hasDb = _filterStore.HasCustomFilter(item.Id);
                if (hasDb)
                {
                    inDbCount++;
                }

                var filter = _filterStore.GetFilter(item.Id);
                details.Add(new
                {
                    id = item.Id,
                    name = item.Name,
                    path = sidecar,
                    inDatabase = hasDb,
                    cues = filter?.Cues.Count ?? 0
                });
            }
        }

        return Ok(new
        {
            total = discovered,
            inDatabaseCount = inDbCount,
            notInDatabaseCount = discovered - inDbCount,
            items = details
        });
    }

    /// <summary>
    /// Scans the library for media items that have adjacent .jcf sidecar files and imports all of them into the SQLite database.
    /// </summary>
    /// <returns>A summary of imported sidecars.</returns>
    [HttpPost("sidecars/import-all")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> ImportAllSidecars()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
            Recursive = true
        };

        var items = _libraryManager.GetItemList(query);
        var discovered = 0;
        var imported = 0;
        var details = new List<object>();

        foreach (var item in items)
        {
            var sidecar = _filterStore.GetSidecarPath(item.Id);
            if (sidecar is not null)
            {
                discovered++;
                var filter = _filterStore.GetFilter(item.Id);
                if (filter is not null)
                {
                    imported++;
                    details.Add(new
                    {
                        id = item.Id,
                        name = item.Name,
                        path = sidecar,
                        cues = filter.Cues.Count
                    });
                }
            }
        }

        return Ok(new
        {
            totalFound = discovered,
            importedCount = imported,
            items = details
        });
    }

    /// <summary>
    /// Permanently deletes all discovered .jcf sidecar files from media library folders.
    /// Requires explicit confirmation payload {"confirm": "DELETE"}.
    /// </summary>
    /// <param name="request">The confirmation request.</param>
    /// <returns>A summary of deletion results.</returns>
    [HttpPost("sidecars/delete-all")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<object> DeleteAllSidecars([FromBody] DeleteSidecarsRequest? request)
    {
        if (request is null || !string.Equals(request.Confirm?.Trim(), "DELETE", StringComparison.Ordinal))
        {
            return BadRequest(new { error = "Safety confirmation failed. You must provide {\"confirm\": \"DELETE\"} to proceed with sidecar deletion." });
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
            Recursive = true
        };

        var items = _libraryManager.GetItemList(query);
        var totalFound = 0;
        var deletedCount = 0;
        var failedCount = 0;
        var failures = new List<object>();

        foreach (var item in items)
        {
            var sidecarPath = _filterStore.GetSidecarPath(item.Id);
            if (string.IsNullOrWhiteSpace(sidecarPath))
            {
                continue;
            }

            totalFound++;

            try
            {
                if (System.IO.File.Exists(sidecarPath))
                {
                    System.IO.File.Delete(sidecarPath);
                    deletedCount++;
                }
            }
            catch (Exception ex)
            {
                failedCount++;
                failures.Add(new
                {
                    id = item.Id,
                    name = item.Name,
                    path = sidecarPath,
                    error = ex.Message
                });
            }
        }

        _filterStore.InvalidateSidecarCache();

        return Ok(new
        {
            totalFound,
            deletedCount,
            failedCount,
            failures
        });
    }

    /// <summary>
    /// Gets database storage statistics.
    /// </summary>
    /// <returns>Counts of stored filters and cues.</returns>
    [HttpGet("storage/stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetStorageStats()
    {
        var (totalFilters, totalCues) = _filterStore.GetDatabaseStats();
        return Ok(new
        {
            totalFilters,
            totalCues,
            saveSidecarsToDisk = Plugin.Instance?.Configuration?.SaveSidecarsToDisk ?? false
        });
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
    /// Downloads the JCF filter file for a media item, exporting directly from the database.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <returns>A file response containing the JCF file.</returns>
    [HttpGet("filters/{itemId:guid}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DownloadFilter(Guid itemId)
    {
        var filter = _filterStore.GetFilter(itemId);
        if (filter is null)
        {
            return NotFound();
        }

        var jcfText = JcfWriter.Serialize(filter);
        var bytes = System.Text.Encoding.UTF8.GetBytes(jcfText);

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
    /// Bulk updates the action for all cues of a media item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="request">The bulk action request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An action result.</returns>
    [HttpPut("filters/{itemId:guid}/segments/bulk-action")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetBulkCueActionAsync(Guid itemId, [FromBody] SetBulkCueActionRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Action))
        {
            return BadRequest("action is required.");
        }

        await _filterStore.SetBulkCueActionAsync(itemId, request.Action, cancellationToken).ConfigureAwait(false);
        return Ok();
    }

    /// <summary>
    /// Deletes a specific cue from a media item's filter.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="cueKey">The cue key to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An action result.</returns>
    [HttpDelete("filters/{itemId:guid}/segments/{cueKey}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCueAsync(Guid itemId, string cueKey, CancellationToken cancellationToken)
    {
        var success = await _filterStore.DeleteCueAsync(itemId, cueKey, cancellationToken).ConfigureAwait(false);
        if (!success)
        {
            return NotFound();
        }

        return Ok();
    }

    /// <summary>
    /// Adds a new cue to a media item's filter.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="request">The cue request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An action result.</returns>
    [HttpPost("filters/{itemId:guid}/segments")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AddCueAsync(Guid itemId, [FromBody] AddCueRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Start) || string.IsNullOrWhiteSpace(request.End))
        {
            return BadRequest("Start and End timestamps are required.");
        }

        if (!ParseFlexibleTimestamp(request.Start, out var startTs) ||
            !ParseFlexibleTimestamp(request.End, out var endTs))
        {
            return BadRequest("Invalid timestamp format. Expected hh:mm:ss.fff, mm:ss, or seconds.");
        }

        var cue = new FilterCue
        {
            Start = startTs,
            End = endTs,
            Category = string.IsNullOrWhiteSpace(request.Category) ? "SexAndNudity.FullNudity" : request.Category,
            Channel = string.IsNullOrWhiteSpace(request.Channel) ? "video" : request.Channel,
            Action = string.IsNullOrWhiteSpace(request.Action) ? "skip" : request.Action,
            Description = request.Description
        };

        await _filterStore.AddCueAsync(itemId, cue, cancellationToken).ConfigureAwait(false);
        return Ok(cue);
    }

    /// <summary>
    /// Updates an existing cue in a media item's filter.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="cueKey">The existing cue key to update.</param>
    /// <param name="request">The updated cue data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An action result.</returns>
    [HttpPut("filters/{itemId:guid}/segments/{cueKey}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateCueAsync(Guid itemId, string cueKey, [FromBody] AddCueRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Start) || string.IsNullOrWhiteSpace(request.End))
        {
            return BadRequest("Start and End timestamps are required.");
        }

        if (!ParseFlexibleTimestamp(request.Start, out var startTs) ||
            !ParseFlexibleTimestamp(request.End, out var endTs))
        {
            return BadRequest("Invalid timestamp format. Expected hh:mm:ss.fff, mm:ss, or seconds.");
        }

        var newCue = new FilterCue
        {
            Start = startTs,
            End = endTs,
            Category = string.IsNullOrWhiteSpace(request.Category) ? "SexAndNudity.FullNudity" : request.Category,
            Channel = string.IsNullOrWhiteSpace(request.Channel) ? "video" : request.Channel,
            Action = string.IsNullOrWhiteSpace(request.Action) ? "skip" : request.Action,
            Description = request.Description
        };

        var success = await _filterStore.UpdateCueAsync(itemId, cueKey, newCue, cancellationToken).ConfigureAwait(false);
        if (!success)
        {
            return NotFound();
        }

        return Ok(newCue);
    }

    private static bool ParseFlexibleTimestamp(string? input, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var s = input.Trim().Replace(',', '.');
        var parts = s.Split(':');
        try
        {
            if (parts.Length == 3)
            {
                if (double.TryParse(parts[0], CultureInfo.InvariantCulture, out var h) &&
                    double.TryParse(parts[1], CultureInfo.InvariantCulture, out var m) &&
                    double.TryParse(parts[2], CultureInfo.InvariantCulture, out var sec))
                {
                    result = TimeSpan.FromHours(h) + TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(sec);
                    return true;
                }
            }
            else if (parts.Length == 2)
            {
                if (double.TryParse(parts[0], CultureInfo.InvariantCulture, out var m) &&
                    double.TryParse(parts[1], CultureInfo.InvariantCulture, out var sec))
                {
                    result = TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(sec);
                    return true;
                }
            }
            else if (parts.Length == 1)
            {
                if (double.TryParse(parts[0], CultureInfo.InvariantCulture, out var sec))
                {
                    result = TimeSpan.FromSeconds(sec);
                    return true;
                }
            }
        }
        catch
        {
            // Fall through
        }

        return TimeSpan.TryParse(input, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// Shifts all cues of a media item by a given offset in seconds.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="request">The offset request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An action result containing the updated cues.</returns>
    [HttpPost("filters/{itemId:guid}/segments/offset")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ShiftCuesAsync(Guid itemId, [FromBody] ShiftCuesRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }

        var channel = string.IsNullOrWhiteSpace(request.Channel) ? "all" : request.Channel.Trim().ToLowerInvariant();
        var (filter, shiftedCount) = await _filterStore.ShiftCuesAsync(itemId, request.OffsetSeconds, channel, cancellationToken).ConfigureAwait(false);
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
            itemId,
            offsetSeconds = request.OffsetSeconds,
            channel,
            shiftedCues = shiftedCount,
            totalCues = cues.Count,
            cues
        });
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
            var item = _libraryManager.GetItemById(itemId);
            var sidecar = SubtitleFilter.GetSidecarFilteredSrtPath(item);
            if (sidecar is not null && System.IO.File.Exists(sidecar))
            {
                return PhysicalFile(sidecar, "text/plain");
            }

            return NotFound();
        }

        return PhysicalFile(path, "text/plain");
    }

    /// <summary>
    /// Generates filtered subtitles for an item (saving both disk sidecar and plugin cache).
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="language">The requested subtitle language (default "eng").</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A status object with the path and registration status.</returns>
    [HttpPost("subtitles/{itemId:guid}/generate-filtered")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GenerateFilteredSubtitleAsync(
        Guid itemId,
        [FromQuery] string? language,
        CancellationToken cancellationToken)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "eng" : language;
        var filter = _filterStore.GetFilter(itemId);
        var path = await _subtitleFilter.RegenerateAsync(itemId, filter, lang, cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return NotFound("No source subtitle stream or file could be found for this item.");
        }

        var item = _libraryManager.GetItemById(itemId);
        var sidecarPath = SubtitleFilter.GetSidecarFilteredSrtPath(item, lang);
        var hasSidecar = sidecarPath is not null && System.IO.File.Exists(sidecarPath);

        return Ok(new
        {
            success = true,
            itemId,
            language = lang,
            path,
            hasSidecar,
            sidecarPath,
            hasPluginSubtitle = _subtitleFilter.HasFilteredSubtitle(itemId)
        });
    }

    /// <summary>
    /// Gets the current filtered subtitle status for an item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="language">The requested subtitle language (default "eng").</param>
    /// <returns>A status object.</returns>
    [HttpGet("subtitles/{itemId:guid}/filtered-status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetFilteredSubtitleStatus(
        Guid itemId,
        [FromQuery] string? language)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "eng" : language;
        var item = _libraryManager.GetItemById(itemId);
        var sidecarPath = SubtitleFilter.GetSidecarFilteredSrtPath(item, lang);
        var hasSidecar = sidecarPath is not null && System.IO.File.Exists(sidecarPath);
        var hasPluginSubtitle = _subtitleFilter.HasFilteredSubtitle(itemId);

        return Ok(new
        {
            itemId,
            language = lang,
            hasSidecar,
            sidecarPath,
            hasPluginSubtitle,
            isCleanSubActive = hasSidecar || hasPluginSubtitle
        });
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

    private List<MediaBrowser.Controller.Entities.BaseItem> FindMatchingItems(JcfFilter filter, string? filename, Guid? seriesId = null)
    {
        var candidates = new List<MediaBrowser.Controller.Entities.BaseItem>();

        // 1. Try matching via season/episode in filename or title (e.g. S01E03 or 1x03)
        var textToSearch = $"{filename} {filter.Title}";
        var seMatch = System.Text.RegularExpressions.Regex.Match(textToSearch, @"[sS](?<season>\d{1,2})[eE](?<ep>\d{1,2})|(?<season>\d{1,2})x(?<ep>\d{1,2})");
        if (seMatch.Success)
        {
            var seasonNumber = int.Parse(seMatch.Groups["season"].Value, CultureInfo.InvariantCulture);
            var episodeNumber = int.Parse(seMatch.Groups["ep"].Value, CultureInfo.InvariantCulture);

            if (seriesId.HasValue)
            {
                var targetEpQuery = new InternalItemsQuery
                {
                    ParentId = seriesId.Value,
                    IncludeItemTypes = [BaseItemKind.Episode],
                    IndexNumber = episodeNumber,
                    ParentIndexNumber = seasonNumber,
                    Recursive = true
                };
                var targetEpisodes = _libraryManager.GetItemList(targetEpQuery);
                if (targetEpisodes.Count == 1)
                {
                    return targetEpisodes.ToList();
                }
            }

            var titleBase = System.Text.RegularExpressions.Regex.Replace(filter.Title ?? string.Empty, @"([sS]\d{1,2}[eE]\d{1,2}|\d{1,2}x\d{1,2}).*$", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(titleBase) && !string.IsNullOrWhiteSpace(filename))
            {
                titleBase = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(filename), @"([sS]\d{1,2}[eE]\d{1,2}|\d{1,2}x\d{1,2}).*$", string.Empty).Trim(' ', '.', '_', '-');
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

    /// <summary>
    /// Serves the client-side playback enforcement script for Jellyfin Web.
    /// </summary>
    /// <returns>The javascript file content.</returns>
    [HttpGet("client.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var assembly = typeof(Plugin).Assembly;
        var resourceName = $"{typeof(Plugin).Namespace}.Web.client.js";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return NotFound("client.js embedded resource not found.");
        }

        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";
        return Content(content, "application/javascript");
    }

    /// <summary>
    /// Gets available subtitle tracks for an item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <returns>A list of subtitle track descriptors.</returns>
    [HttpGet("subtitles/{itemId:guid}/tracks")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<SubtitleTrackInfo>> GetSubtitleTracks(Guid itemId)
    {
        var tracks = _subtitleWordScanner.GetAvailableTracks(itemId);
        return Ok(tracks);
    }

    /// <summary>
    /// Scans an item's subtitle for filterable words.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="language">The requested subtitle language code (defaults to eng).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Detected words and their occurrences.</returns>
    [HttpGet("subtitles/{itemId:guid}/words")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SubtitleScanResult>> GetSubtitleWords(
        Guid itemId,
        [FromQuery] string? language,
        CancellationToken cancellationToken)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "eng" : language;
        var result = await _subtitleWordScanner.ScanWordsAsync(itemId, lang, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Applies blanket filtering for one or more detected words across an item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="request">The blanket filter request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An action result indicating the number of cues added.</returns>
    [HttpPost("subtitles/{itemId:guid}/blanket-filter")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ApplyBlanketFilterAsync(
        Guid itemId,
        [FromBody] BlanketFilterRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }

        var targetWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(request.Word))
        {
            targetWords.Add(request.Word.Trim());
        }

        if (request.Words is { Count: > 0 })
        {
            foreach (var w in request.Words)
            {
                if (!string.IsNullOrWhiteSpace(w))
                {
                    targetWords.Add(w.Trim());
                }
            }
        }

        if (targetWords.Count == 0)
        {
            return BadRequest("At least one word must be specified.");
        }

        var scan = await _subtitleWordScanner.ScanWordsAsync(itemId, request.Language ?? "eng", cancellationToken).ConfigureAwait(false);
        var cuesToAdd = new List<FilterCue>();
        var action = string.IsNullOrWhiteSpace(request.Action) ? "mute" : request.Action;
        var channel = action.Equals("skip", StringComparison.OrdinalIgnoreCase) ? "both" : "audio";

        foreach (var group in scan.Words.Where(g => targetWords.Contains(g.Word)))
        {
            foreach (var occ in group.Occurrences)
            {
                if (!ParseFlexibleTimestamp(occ.CueStart, out var startTs) ||
                    !ParseFlexibleTimestamp(occ.CueEnd, out var endTs))
                {
                    startTs = TimeSpan.FromSeconds(occ.StartSeconds);
                    endTs = TimeSpan.FromSeconds(occ.EndSeconds);
                }

                cuesToAdd.Add(new FilterCue
                {
                    Start = startTs,
                    End = endTs,
                    Category = group.Category,
                    Channel = channel,
                    Action = action,
                    Description = $"Spoken: \"{group.Word}\""
                });
            }
        }

        var addedCount = await _filterStore.AddCuesAsync(itemId, cuesToAdd, cancellationToken).ConfigureAwait(false);

        if (request.Global && Plugin.Instance is not null)
        {
            var config = Plugin.Instance.Configuration;
            config.BlanketFilterWords ??= [];
            bool changed = false;
            foreach (var w in targetWords)
            {
                if (!config.BlanketFilterWords.Contains(w, StringComparer.OrdinalIgnoreCase))
                {
                    config.BlanketFilterWords.Add(w);
                    changed = true;
                }
            }

            if (changed)
            {
                Plugin.Instance.SaveConfiguration();
            }
        }

        return Ok(new
        {
            itemId,
            words = targetWords.ToList(),
            cuesAdded = addedCount,
            totalCues = _filterStore.GetFilter(itemId)?.Cues.Count ?? 0
        });
    }

    /// <summary>
    /// Removes cues matching a word from an item's filter.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="request">The remove request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of cues removed.</returns>
    [HttpPost("subtitles/{itemId:guid}/remove-word-filter")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RemoveWordFilterAsync(
        Guid itemId,
        [FromBody] RemoveWordFilterRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Word))
        {
            return BadRequest("Word is required.");
        }

        var removed = await _filterStore.RemoveCuesForWordAsync(itemId, request.Word.Trim(), cancellationToken).ConfigureAwait(false);

        if (request.RemoveFromGlobal && Plugin.Instance is not null)
        {
            var config = Plugin.Instance.Configuration;
            if (config.BlanketFilterWords != null)
            {
                var removedFromGlobal = config.BlanketFilterWords.RemoveAll(w => w.Equals(request.Word.Trim(), StringComparison.OrdinalIgnoreCase));
                if (removedFromGlobal > 0)
                {
                    Plugin.Instance.SaveConfiguration();
                }
            }
        }

        return Ok(new
        {
            itemId,
            word = request.Word,
            cuesRemoved = removed,
            totalCues = _filterStore.GetFilter(itemId)?.Cues.Count ?? 0
        });
    }

    /// <summary>
    /// Gets the list of global blanket filter words.
    /// </summary>
    /// <returns>A list of strings.</returns>
    [HttpGet("subtitles/global-blanket-words")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<string>> GetGlobalBlanketWords()
    {
        var words = Plugin.Instance?.Configuration.BlanketFilterWords ?? [];
        return Ok(words);
    }

    /// <summary>
    /// Updates the list of global blanket filter words.
    /// </summary>
    /// <param name="request">The request payload containing blanket words.</param>
    /// <returns>The updated list of words.</returns>
    [HttpPost("subtitles/global-blanket-words")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<string>> SetGlobalBlanketWords([FromBody] GlobalBlanketWordsRequest? request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }

        if (Plugin.Instance is not null)
        {
            var config = Plugin.Instance.Configuration;
            config.BlanketFilterWords = request.Words?
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Select(w => w.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
            Plugin.Instance.SaveConfiguration();
        }

        return Ok(Plugin.Instance?.Configuration.BlanketFilterWords ?? []);
    }

    /// <summary>
    /// Starts the library-wide automated subtitle download and clean sync job.
    /// </summary>
    [HttpPost("subtitles/sync/start")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<SubtitleSyncStatus> StartSubtitleSync([FromBody] StartSubtitleSyncRequest? request)
    {
        var started = _subtitleSyncService.StartSync(
            request?.ForceAll ?? false,
            request?.Language);

        if (!started)
        {
            return Conflict(_subtitleSyncService.Status);
        }

        return Ok(_subtitleSyncService.Status);
    }

    /// <summary>
    /// Gets the current status of the library-wide automated subtitle sync.
    /// </summary>
    [HttpGet("subtitles/sync/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SubtitleSyncStatus> GetSubtitleSyncStatus()
    {
        return Ok(_subtitleSyncService.Status);
    }

    /// <summary>
    /// Cancels any running library-wide automated subtitle sync.
    /// </summary>
    [HttpPost("subtitles/sync/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult CancelSubtitleSync()
    {
        _subtitleSyncService.CancelSync();
        return NoContent();
    }

    /// <summary>
    /// Searches remote subtitle providers for an item and returns matching releases.
    /// </summary>
    [HttpGet("subtitles/{itemId:guid}/remote-search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<RemoteSubtitleDto>>> SearchRemoteSubtitles(
        Guid itemId,
        [FromQuery] string? language,
        CancellationToken cancellationToken)
    {
        var results = await _subtitleSyncService.SearchRemoteSubtitlesAsync(itemId, language, cancellationToken).ConfigureAwait(false);
        return Ok(results);
    }

    /// <summary>
    /// Downloads a specific remote subtitle candidate, cleans it, sets it as default, and locks the item.
    /// </summary>
    [HttpPost("subtitles/{itemId:guid}/remote-download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DownloadRemoteSubtitle(
        Guid itemId,
        [FromBody] RemoteSubtitleDownloadRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SubtitleId))
        {
            return BadRequest("SubtitleId is required.");
        }

        var success = await _subtitleSyncService.DownloadRemoteSubtitleAsync(itemId, request.SubtitleId, cancellationToken).ConfigureAwait(false);
        if (!success)
        {
            return BadRequest("Failed to download or clean the selected subtitle.");
        }

        return Ok(new { success = true, itemId, subtitleId = request.SubtitleId });
    }

    /// <summary>
    /// Shifts an item's subtitles and cues by a given offset in seconds.
    /// </summary>
    [HttpPost("subtitles/{itemId:guid}/shift")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ShiftSubtitle(
        Guid itemId,
        [FromBody] SubtitleShiftRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }

        var success = await _subtitleSyncService.ApplySubtitleOffsetAsync(itemId, request.OffsetSeconds, cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            success,
            itemId,
            offsetSeconds = request.OffsetSeconds,
            overrideInfo = _sqliteRepository.GetSubtitleOverride(itemId)
        });
    }

    /// <summary>
    /// Toggles or sets the subtitle lock for an item so automated sync will not overwrite it.
    /// </summary>
    [HttpPost("subtitles/{itemId:guid}/lock")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult SetSubtitleLock(
        Guid itemId,
        [FromBody] SubtitleLockRequest? request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }

        _sqliteRepository.SetSubtitleLock(itemId, request.IsLocked);
        return Ok(new
        {
            success = true,
            itemId,
            isLocked = request.IsLocked,
            overrideInfo = _sqliteRepository.GetSubtitleOverride(itemId)
        });
    }

    /// <summary>
    /// Gets the subtitle override and lock status for an item.
    /// </summary>
    [HttpGet("subtitles/{itemId:guid}/override")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SubtitleOverrideInfo> GetSubtitleOverride(Guid itemId)
    {
        var info = _sqliteRepository.GetSubtitleOverride(itemId) ?? new SubtitleOverrideInfo
        {
            ItemId = itemId,
            OffsetMs = 0,
            OffsetSeconds = 0,
            IsLocked = false,
            UpdatedAt = DateTime.UtcNow
        };

        return Ok(info);
    }

    /// <summary>
    /// Uploads a custom SRT subtitle file for an item, cleans it, sets it as default, and locks the item.
    /// </summary>
    [HttpPost("subtitles/{itemId:guid}/upload")]
    [Consumes("multipart/form-data", "text/plain", "application/octet-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadSubtitle(
        Guid itemId,
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        string srtContent;
        if (file is not null && file.Length > 0)
        {
            await using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream);
            srtContent = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (Request.ContentLength is > 0)
        {
            using var reader = new StreamReader(Request.Body);
            srtContent = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return BadRequest("A subtitle file or content is required.");
        }

        var success = await _subtitleSyncService.SaveCustomSubtitleAsync(itemId, srtContent, cancellationToken).ConfigureAwait(false);
        if (!success)
        {
            return BadRequest("Failed to process and save custom subtitle file.");
        }

        return Ok(new { success = true, itemId });
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

/// <summary>
/// Request payload for bulk updating cue actions.
/// </summary>
public sealed class SetBulkCueActionRequest
{
    /// <summary>
    /// Gets or sets the cue action.
    /// </summary>
    public required string Action { get; set; }
}

/// <summary>
/// Request payload for adding a new cue.
/// </summary>
public sealed class AddCueRequest
{
    /// <summary>
    /// Gets or sets the start timestamp.
    /// </summary>
    public string Start { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the end timestamp.
    /// </summary>
    public string End { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the category.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Gets or sets the channel.
    /// </summary>
    public string? Channel { get; set; }

    /// <summary>
    /// Gets or sets the action.
    /// </summary>
    public string? Action { get; set; }

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Request payload for shifting cues by an offset.
/// </summary>
public sealed class ShiftCuesRequest
{
    /// <summary>
    /// Gets or sets the offset in seconds (can be positive or negative).
    /// </summary>
    public double OffsetSeconds { get; set; }

    /// <summary>
    /// Gets or sets the target channel to shift: "all", "video", or "audio".
    /// </summary>
    public string Channel { get; set; } = "all";
}

/// <summary>
/// Request payload for applying blanket word filtering.
/// </summary>
public sealed class BlanketFilterRequest
{
    /// <summary>
    /// Gets or sets a single word to filter.
    /// </summary>
    public string? Word { get; set; }

    /// <summary>
    /// Gets or sets multiple words to filter.
    /// </summary>
    public List<string>? Words { get; set; }

    /// <summary>
    /// Gets or sets the language code.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets the cue action ("mute" or "skip").
    /// </summary>
    public string? Action { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to add word to global blanket list.
    /// </summary>
    public bool Global { get; set; }
}

/// <summary>
/// Request payload for removing word filter cues.
/// </summary>
public sealed class RemoveWordFilterRequest
{
    /// <summary>
    /// Gets or sets the word to remove cues for.
    /// </summary>
    public string? Word { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to also remove from global blanket list.
    /// </summary>
    public bool RemoveFromGlobal { get; set; }
}

/// <summary>
/// Request payload for updating the global blanket words list.
/// </summary>
public sealed class GlobalBlanketWordsRequest
{
    /// <summary>
    /// Gets or sets the list of words.
    /// </summary>
    public List<string>? Words { get; set; }
}

