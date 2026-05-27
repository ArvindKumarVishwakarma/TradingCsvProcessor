using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TradingCsvProcessor.Application.Abstractions;
using TradingCsvProcessor.Application.DTOs;
using TradingCsvProcessor.Application.Features.Jobs.Commands.CancelJob;
using TradingCsvProcessor.Application.Features.Jobs.Commands.UploadCsv;
using TradingCsvProcessor.Application.Features.Jobs.Queries.GetAllJobs;
using TradingCsvProcessor.Application.Features.Jobs.Queries.GetJobStatus;
using TradingCsvProcessor.Application.Options;

namespace TradingCsvProcessor.API.Controllers;

/// <summary>Upload, track, and cancel asynchronous CSV processing jobs.</summary>
[ApiController]
[Route("api/csv")]
[EnableRateLimiting("api")]
[Produces("application/json")]
public sealed class CsvUploadController(
    ICommandHandler<UploadCsvCommand, UploadResponse>                 uploadHandler,
    ICommandHandler<CancelJobCommand, CancelJobResponse>              cancelHandler,
    IQueryHandler<GetJobStatusQuery,  JobStatusResponse?>             statusQuery,
    IQueryHandler<GetAllJobsQuery,    IReadOnlyList<JobStatusResponse>> listQuery,
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
    public async Task<ActionResult<UploadResponse>> Upload([FromForm] IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(Problem("No file provided.", detail: "Provide a non-empty .csv file."));

        if (file.Length > MaxFileSize)
            return BadRequest(Problem(
                $"File exceeds the {MaxFileSize / 1024 / 1024} MB limit.",
                detail: "Split the file and upload each part separately."));

        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return BadRequest(Problem("Invalid file type.", detail: "Only .csv files are accepted."));

        var result = await uploadHandler.HandleAsync(new UploadCsvCommand(file), ct);
        return AcceptedAtAction(nameof(GetJobStatus), new { jobId = result.JobId }, result);
    }

    /// <summary>Get full status, per-chunk breakdown, and stage audit log for a job.</summary>
    [HttpGet("jobs/{jobId:guid}")]
    [OutputCache(PolicyName = "job-status")]
    [ProducesResponseType(typeof(JobStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<JobStatusResponse>> GetJobStatus(Guid jobId, CancellationToken ct)
    {
        var response = await statusQuery.HandleAsync(new GetJobStatusQuery(jobId), ct);
        return response is null ? NotFound(Problem($"Job '{jobId}' not found.", statusCode: 404)) : Ok(response);
    }

    /// <summary>List all upload jobs, newest first.</summary>
    [HttpGet("jobs")]
    [OutputCache(PolicyName = "jobs-list")]
    [ProducesResponseType(typeof(IReadOnlyList<JobStatusResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<JobStatusResponse>>> GetAllJobs(CancellationToken ct)
    {
        var jobs = await listQuery.HandleAsync(new GetAllJobsQuery(), ct);
        return Ok(jobs);
    }

    /// <summary>
    /// Cancel a running or queued job.
    /// If the worker is actively processing, it stops after the current chunk.
    /// If the job is still queued, it is skipped when the worker picks it up.
    /// </summary>
    [HttpPost("jobs/{jobId:guid}/cancel")]
    [ProducesResponseType(typeof(CancelJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CancelJobResponse>> CancelJob(Guid jobId, CancellationToken ct)
    {
        var result = await cancelHandler.HandleAsync(new CancelJobCommand(jobId), ct);
        return Ok(result);
        // NotFoundException and ConflictException propagate to ExceptionHandlingMiddleware
    }

    private ProblemDetails Problem(string title, string? detail = null, int statusCode = 400) => new()
    {
        Status   = statusCode,
        Title    = title,
        Detail   = detail,
        Instance = HttpContext.Request.Path
    };
}
