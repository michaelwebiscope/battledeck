using Microsoft.EntityFrameworkCore;

namespace NavalArchive.PaymentSimulation.Data;

public class PaymentDbContext : DbContext
{
    public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options) { }

    public DbSet<PaymentTransaction> Transactions => Set<PaymentTransaction>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<PaymentTransaction>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TransactionId).HasMaxLength(32);
            e.Property(x => x.CardId).HasMaxLength(32);
        });
    }
}

public class PaymentTransaction
{
    public int Id { get; set; }
    public string TransactionId { get; set; } = "";
    public string? CardId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public bool Approved { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
