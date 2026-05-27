using Microsoft.EntityFrameworkCore;
using TradingCsvProcessor.Data;
using TradingCsvProcessor.Infrastructure;
using TradingCsvProcessor.Services;
using TradingCsvProcessor.Workers;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new() { Title = "Trading CSV Processor", Version = "v1" }));

builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"),
        sql => sql.CommandTimeout(120)));

// Singleton channel shared between the HTTP layer and the background worker
builder.Services.AddSingleton<ProcessingChannel>();
// Singleton registry: maps running jobId → CancellationTokenSource for per-job cancellation
builder.Services.AddSingleton<JobCancellationRegistry>();

builder.Services.AddScoped<IFileStorageService, FileStorageService>();
builder.Services.AddScoped<ICsvUploadService, CsvUploadService>();

// One worker instance; scale out by adding more AddHostedService calls or using parallel consumers
builder.Services.AddHostedService<CsvProcessingWorker>();

// ── App ───────────────────────────────────────────────────────────────────────
var app = builder.Build();

// Auto-create / migrate schema on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    // Switch to db.Database.MigrateAsync() once you add EF Core migrations
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthorization();
app.MapControllers();

app.Run();
