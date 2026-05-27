using Microsoft.AspNetCore.Http;

namespace TradingCsvProcessor.Application.Features.Jobs.Commands.UploadCsv;

public sealed record UploadCsvCommand(IFormFile File);
