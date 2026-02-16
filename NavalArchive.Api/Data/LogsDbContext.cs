using Microsoft.EntityFrameworkCore;
using NavalArchive.Api.Models;

namespace NavalArchive.Api.Data;

public class LogsDbContext : DbContext
{
    public LogsDbContext(DbContextOptions<LogsDbContext> options)
        : base(options) { }

    public DbSet<CaptainLog> CaptainLogs => Set<CaptainLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CaptainLog>(e =>
        {
            e.HasIndex(x => x.ShipName);
            e.HasIndex(x => x.LogDate);
        });
    }
}
