using Microsoft.EntityFrameworkCore;

namespace NavalArchive.CardService.Data;

public class CardDbContext : DbContext
{
    public CardDbContext(DbContextOptions<CardDbContext> options) : base(options) { }

    public DbSet<Card> Cards => Set<Card>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Card>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CardId).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Tier).HasMaxLength(50);
            e.HasIndex(x => x.CardId).IsUnique();
        });
    }
}

public class Card
{
    public int Id { get; set; }
    public string CardId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Tier { get; set; } = "Standard";
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
