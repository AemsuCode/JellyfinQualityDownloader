using System.Net.Mime;
using System.Reflection;
using System.Security.Claims;
using Jellyfin.Plugin.SeriesDownloader.Models;
using Jellyfin.Plugin.SeriesDownloader.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeriesDownloader.Api;

/// <summary>
/// Hardened REST API controller for Series Quality Downloader.
/// </summary>
[ApiController]
[Route("SeriesDownloader")]
[Authorize]
public class SeriesDownloaderController : ControllerBase
{
    private readonly DownloadJobManager _jobManager;
    private readonly WebUiInjectorService _injectorService;
    private readonly ILogger<SeriesDownloaderController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeriesDownloaderController"/> class.
    /// </summary>
    public SeriesDownloaderController(
        DownloadJobManager jobManager,
        WebUiInjectorService injectorService,
        ILogger<SeriesDownloaderController> logger)
    {
        _jobManager = jobManager;
        _injectorService = injectorService;
        _logger = logger;
    }

    /// <summary>
    /// Serves the injected client-side JavaScript for Jellyfin Web UI.
    /// </summary>
    /// <returns>JavaScript content.</returns>
    [HttpGet("ClientScript")]
    [HttpGet("/web/SeriesDownloader/ClientScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{typeof(Plugin).Namespace}.Web.seriesDownloader.js";
        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return NotFound("// Client script resource not found.");
        }

        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return File(stream, "application/javascript; charset=utf-8");
    }

    /// <summary>
    /// Lists all TV series in the Jellyfin library for the dashboard quick-downloader.
    /// </summary>
    [HttpGet("Series")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetAllSeries()
    {
        try
        {
            return Ok(_jobManager.GetAllSeries());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list TV series.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Failed to load series list." });
        }
    }

    /// <summary>
    /// Returns the Series / Season / Episode tree and media stream metadata for an item.
    /// </summary>
    /// <param name="itemId">The Series, Season, Episode, or Video ID.</param>
    [HttpGet("Items/{itemId:guid}/Tree")]
    [Authorize]
    [ProducesResponseType(typeof(SeriesTreeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<SeriesTreeDto> GetItemTree([FromRoute] Guid itemId)
    {
        try
        {
            var tree = _jobManager.GetItemTree(itemId);
            if (tree is null)
            {
                return NotFound(new { Message = $"Item {itemId} was not found or has no downloadable video episodes." });
            }

            return Ok(tree);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building item tree for {ItemId}", itemId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = $"Server error loading item {itemId}." });
        }
    }

    /// <summary>
    /// Starts a new download / transcode job.
    /// </summary>
    /// <param name="request">The download and quality configuration.</param>
    [HttpPost("Jobs")]
    [Authorize]
    [ProducesResponseType(typeof(DownloadJobStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<DownloadJobStatusDto> StartJob([FromBody] StartDownloadJobRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var status = _jobManager.CreateJob(userId, request);
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting download job");
            return BadRequest(new { Message = ex.Message });
        }
    }

    /// <summary>
    /// Lists active and recently completed download jobs for the current user.
    /// </summary>
    [HttpGet("Jobs")]
    [Authorize]
    [ProducesResponseType(typeof(List<DownloadJobStatusDto>), StatusCodes.Status200OK)]
    public ActionResult<List<DownloadJobStatusDto>> GetJobs()
    {
        var userId = GetCurrentUserId();
        return Ok(_jobManager.GetJobs(userId));
    }

    /// <summary>
    /// Gets the live status and progress of a specific download job.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    [HttpGet("Jobs/{jobId}")]
    [Authorize]
    [ProducesResponseType(typeof(DownloadJobStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<DownloadJobStatusDto> GetJobStatus([FromRoute] string jobId)
    {
        var userId = GetCurrentUserId();
        var status = _jobManager.GetJobStatus(jobId, userId);
        if (status is null)
        {
            return NotFound(new { Message = "Job not found." });
        }

        return Ok(status);
    }

    /// <summary>
    /// Downloads the completed file (.zip archive or single transcoded video) for a job.
    /// Protected by a 256-bit cryptographic download token verified in constant time.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="token">The 256-bit cryptographic download token.</param>
    [HttpGet("Jobs/{jobId}/Download")]
    [AllowAnonymous]
    public ActionResult DownloadJobFile([FromRoute] string jobId, [FromQuery] string? token = null)
    {
        var result = _jobManager.GetCompletedJobFile(jobId, token);
        if (result is null)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Invalid or expired download token.");
        }

        var (filePath, downloadFileName, contentType) = result.Value;
        return PhysicalFile(filePath, contentType, downloadFileName, enableRangeProcessing: true);
    }

    /// <summary>
    /// Cancels a running job or removes a finished job and deletes its temporary files.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="remove">Whether to remove the job from the list and delete files immediately.</param>
    [HttpDelete("Jobs/{jobId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult CancelOrDeleteJob([FromRoute] string jobId, [FromQuery] bool remove = false)
    {
        var userId = GetCurrentUserId();
        var ok = _jobManager.CancelOrDeleteJob(jobId, remove, userId);
        if (!ok)
        {
            return NotFound(new { Message = "Job not found." });
        }

        return Ok(new { JobId = jobId, Removed = remove });
    }

    /// <summary>
    /// Creates a short-lived 256-bit cryptographic ticket for streaming an original quality ZIP archive.
    /// </summary>
    [HttpPost("StreamTickets")]
    [Authorize]
    public ActionResult CreateStreamTicket([FromBody] CreateStreamTicketRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var ticketId = _jobManager.CreateStreamTicket(userId, request);
            return Ok(new { TicketId = ticketId });
        }
        catch (Exception ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }

    /// <summary>
    /// Streams multiple episodes in Original Quality directly into an on-the-fly ZIP archive response
    /// after verifying a valid 256-bit cryptographic stream ticket.
    /// </summary>
    /// <param name="ticketId">The cryptographic stream ticket.</param>
    [HttpGet("StreamOriginalZip/{ticketId}")]
    [AllowAnonymous]
    public async Task StreamOriginalZip([FromRoute] string ticketId)
    {
        var ticket = _jobManager.TryGetStreamTicket(ticketId);
        if (ticket is null)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var (episodeIds, safeTitle) = ticket.Value;
        var fileName = $"{safeTitle} [Original].zip";
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = MediaTypeNames.Application.Zip;
        Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";

        await _jobManager.StreamOriginalEpisodesZipAsync(episodeIds, Response.Body, HttpContext.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the current status of the Jellyfin Web index.html script injection.
    /// </summary>
    [HttpGet("InjectionStatus")]
    [Authorize]
    public ActionResult GetInjectionStatus()
    {
        var (isInjected, mode, indexPath) = _injectorService.GetInjectionStatus();
        return Ok(new
        {
            IsInjected = isInjected,
            Mode = mode,
            IndexPath = indexPath
        });
    }

    /// <summary>
    /// Triggers or removes the Jellyfin Web index.html script injection.
    /// </summary>
    /// <param name="inject">True to inject, false to remove.</param>
    [HttpPost("InjectionStatus")]
    [Authorize]
    public ActionResult SetInjectionStatus([FromQuery] bool inject = true)
    {
        var (success, message) = _injectorService.ApplyInjectionState(inject);
        return Ok(new
        {
            Success = success,
            Message = message
        });
    }

    private Guid GetCurrentUserId()
    {
        var claim = User.FindFirstValue("Jellyfin-UserId")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out var uid) ? uid : Guid.Empty;
    }
}
