using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TradingCsvProcessor.Application.DTOs;
using TradingCsvProcessor.Application.Interfaces;
using TradingCsvProcessor.Application.Options;

namespace TradingCsvProcessor.API.Controllers;

/// <summary>Upload, track, and cancel asynchronous CSV processing jobs.</summary>
[ApiController]
[Route("api/csv")]
[EnableRateLimiting("api")]
[Produces("application/json")]
public sealed class CsvUploadController(
    ICsvUploadService uploadService,
    IOptions<FileStorageOptions> storageOptions) : ControllerBase
{
    private long MaxFileSize => storageOptions.Value.MaxFileSizeBytes;

    /// <summary>Upload a CSV file. Returns a job ID for async status polling.</summary>
    /// <remarks>Max file size is configurable (default 100 MB). Only .csv files are accepted.</remarks>
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting("upload")]
    [RequestTimeout("upload")]
    [ProducesResponseType(typeof(UploadResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Upload([FromForm] IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ProblemFor("No file provided.", "Provide a non-empty .csv file."));

        if (file.Length > MaxFileSize)
            return BadRequest(ProblemFor(
                $"File exceeds the {MaxFileSize / 1024 / 1024} MB size limit.",
                "Split the file into smaller chunks and upload each separately."));

        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return BadRequest(ProblemFor("Invalid file type.", "Only .csv files are accepted."));

        var result = await uploadService.UploadAsync(file, ct);
        return AcceptedAtAction(nameof(GetJobStatus), new { jobId = result.JobId }, result);
    }

    /// <summary>Get full status, per-chunk breakdown, and stage audit log for a job.</summary>
    [HttpGet("jobs/{jobId:guid}")]
    [OutputCache(PolicyName = "job-status")]
    [ProducesResponseType(typeof(JobStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobStatus(Guid jobId, CancellationToken ct)
    {
        var status = await uploadService.GetJobStatusAsync(jobId, ct);
        return status is null
            ? NotFound(ProblemFor($"Job '{jobId}' not found.", detail: null, statusCode: 404))
            : Ok(status);
    }

    /// <summary>List all upload jobs, newest first.</summary>
    [HttpGet("jobs")]
    [OutputCache(PolicyName = "jobs-list")]
    [ProducesResponseType(typeof(IEnumerable<JobStatusResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllJobs(CancellationToken ct)
    {
        var jobs = await uploadService.GetAllJobsAsync(ct);
        return Ok(jobs);
    }

    /// <summary>
    /// Cancel a running or queued job.
    /// If the worker is actively processing, it stops after the current chunk.
    /// If the job is still queued, it is skipped when the worker picks it up.
    /// </summary>
    [HttpPost("jobs/{jobId:guid}/cancel")]
    [ProducesResponseType(typeof(CancelJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CancelJobResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelJob(Guid jobId, CancellationToken ct)
    {
        var result = await uploadService.CancelJobAsync(jobId, ct);

        if (!result.Success && result.Message == "Job not found.")
            return NotFound(ProblemFor($"Job '{jobId}' not found.", detail: null, statusCode: 404));

        return result.Success ? Ok(result) : BadRequest(result);
    }

    private ProblemDetails ProblemFor(string title, string? detail, int statusCode = 400) =>
        new()
        {
            Status   = statusCode,
            Title    = title,
            Detail   = detail,
            Instance = HttpContext.Request.Path
        };
}
