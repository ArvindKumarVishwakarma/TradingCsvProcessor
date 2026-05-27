using Microsoft.AspNetCore.Mvc;
using TradingCsvProcessor.Models.DTOs;
using TradingCsvProcessor.Services;

namespace TradingCsvProcessor.Controllers;

[ApiController]
[Route("api/csv")]
public sealed class CsvUploadController : ControllerBase
{
    private readonly ICsvUploadService _uploadService;

    public CsvUploadController(ICsvUploadService uploadService) => _uploadService = uploadService;

    /// <summary>Upload a CSV file. Returns a job ID for status polling.</summary>
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(UploadResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upload([FromForm] IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });

        if (file.Length > 100 * 1024 * 1024)
            return BadRequest(new { error = "File exceeds 100 MB limit." });

        try
        {
            var result = await _uploadService.UploadAsync(file, ct);
            return AcceptedAtAction(nameof(GetJobStatus), new { jobId = result.JobId }, result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Get full status, per-chunk breakdown, and stage audit log for a job.</summary>
    [HttpGet("jobs/{jobId:guid}")]
    [ProducesResponseType(typeof(JobStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobStatus(Guid jobId, CancellationToken ct)
    {
        var status = await _uploadService.GetJobStatusAsync(jobId, ct);
        return status is null ? NotFound(new { error = $"Job {jobId} not found." }) : Ok(status);
    }

    /// <summary>List all upload jobs, newest first.</summary>
    [HttpGet("jobs")]
    [ProducesResponseType(typeof(IEnumerable<JobStatusResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllJobs(CancellationToken ct)
    {
        var jobs = await _uploadService.GetAllJobsAsync(ct);
        return Ok(jobs);
    }

    /// <summary>
    /// Cancel a running or queued job.
    /// If the worker is actively processing the job, it will stop after the current chunk.
    /// If the job is still queued, it will be skipped when the worker picks it up.
    /// </summary>
    [HttpPost("jobs/{jobId:guid}/cancel")]
    [ProducesResponseType(typeof(CancelJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CancelJobResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelJob(Guid jobId, CancellationToken ct)
    {
        var result = await _uploadService.CancelJobAsync(jobId, ct);

        if (!result.Success && result.Message == "Job not found.")
            return NotFound(result);

        return result.Success ? Ok(result) : BadRequest(result);
    }
}
