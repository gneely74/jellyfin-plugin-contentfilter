using System.Globalization;
using Jellyfin.Plugin.ContentFilter.Services;
using MediaBrowser.Controller.Library;
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
    /// Uploads and saves a JCF filter file for a media item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="file">The uploaded JCF file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An action result.</returns>
    [HttpPost("filters/{itemId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadFilterAsync(Guid itemId, IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return BadRequest("A JCF file is required.");
        }

        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream);
        var filter = JcfParser.Parse(reader);
        await _filterStore.SaveFilterAsync(itemId, filter, cancellationToken).ConfigureAwait(false);
        return Ok();
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
        var path = _filterStore.GetJcfPath(itemId);
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var fallbackTitle = _libraryManager.GetItemById(itemId)?.Name ?? itemId.ToString("N", CultureInfo.InvariantCulture);
        var title = string.IsNullOrWhiteSpace(filter.Title) ? fallbackTitle : filter.Title;
        return File(bytes, "text/plain", $"{title}.jcf");
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
        return value.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
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
