using Jellyfin.Plugin.ContentFilter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ContentFilter.Api;

/// <summary>
/// Provides API endpoints for scan job lifecycle management.
/// </summary>
[ApiController]
[Route("ContentFilter")]
[Authorize]
public class ScanController : ControllerBase
{
    private readonly VideoScanner _videoScanner;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanController"/> class.
    /// </summary>
    /// <param name="videoScanner">The scanner service.</param>
    public ScanController(VideoScanner videoScanner)
    {
        _videoScanner = videoScanner;
    }

    /// <summary>
    /// Starts or queues a scan for an item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An action result indicating whether the request was queued.</returns>
    [HttpPost("scan/{itemId:guid}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> StartScanAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var queued = await _videoScanner.TryEnqueueAsync(itemId, cancellationToken).ConfigureAwait(false);
        return queued ? Accepted() : Conflict();
    }

    /// <summary>
    /// Gets scan status for an item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <returns>The current scan status.</returns>
    [HttpGet("scan/{itemId:guid}/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetStatus(Guid itemId)
    {
        var status = _videoScanner.GetStatus(itemId);
        return Ok(status);
    }

    /// <summary>
    /// Cancels an active scan for an item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <returns>An action result.</returns>
    [HttpDelete("scan/{itemId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult CancelScan(Guid itemId)
    {
        _videoScanner.Cancel(itemId);
        return NoContent();
    }
}
