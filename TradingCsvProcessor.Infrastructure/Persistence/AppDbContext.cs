using Microsoft.EntityFrameworkCore;
using TradingCsvProcessor.Domain.Entities;
using TradingCsvProcessor.Domain.Enums;

namespace TradingCsvProcessor.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<UploadJob>      UploadJobs      => Set<UploadJob>();
    public DbSet<UploadJobChunk> UploadJobChunks => Set<UploadJobChunk>();
    public DbSet<TradeRecord>    TradeRecords    => Set<TradeRecord>();
    public DbSet<JobStageLog>    JobStageLogs    => Set<JobStageLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UploadJob>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(500).IsRequired();
            e.Property(x => x.StoredFilePath).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.CurrentStage).HasConversion<string>().HasMaxLength(50);
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);

            // Map backing fields for encapsulated collections
            e.HasMany(x => x.Chunks)
                .WithOne(c => c.Job)
                .HasForeignKey(c => c.JobId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Navigation(x => x.Chunks).HasField("_chunks").UsePropertyAccessMode(PropertyAccessMode.Field);

            e.HasMany(x => x.StageLogs)
                .WithOne(s => s.Job)
                .HasForeignKey(s => s.JobId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Navigation(x => x.StageLogs).HasField("_stageLogs").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<UploadJobChunk>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);
            e.HasIndex(x => new { x.JobId, x.ChunkNumber }).IsUnique();
        });

        modelBuilder.Entity<TradeRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.RecordHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.TradeId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Symbol).HasMaxLength(50).IsRequired();
            e.Property(x => x.Side).HasMaxLength(10).IsRequired();
            e.Property(x => x.Exchange).HasMaxLength(50);
            e.Property(x => x.Currency).HasMaxLength(10);
            e.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
            e.Property(x => x.Price).HasColumnType("decimal(18,6)");
            e.Property(x => x.TotalValue).HasColumnType("decimal(18,6)");
            e.HasIndex(x => x.RecordHash).IsUnique();
            e.HasIndex(x => x.TradeId);
            e.HasIndex(x => x.JobId);
            e.HasIndex(x => x.Symbol);
        });

        modelBuilder.Entity<JobStageLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Stage).HasConversion<string>().HasMaxLength(50);
            e.Property(x => x.Message).HasMaxLength(1000);
            e.HasIndex(x => x.JobId);
        });
    }
}
