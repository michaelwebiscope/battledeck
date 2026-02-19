using Microsoft.EntityFrameworkCore;

namespace NavalArchive.CartService.Data;

public class CartDbContext : DbContext
{
    public CartDbContext(DbContextOptions<CartDbContext> options) : base(options) { }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<CartItem> CartItems => Set<CartItem>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Product>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
        });
        mb.Entity<CartItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
        });
        mb.Entity<Product>().HasData(
            new Product { Id = 1, Name = "Museum Admission", Price = 15.00m, MemberPrice = 10.00m },
            new Product { Id = 2, Name = "Annual Membership", Price = 50.00m, MemberPrice = 35.00m },
            new Product { Id = 3, Name = "Donation", Price = 25.00m, MemberPrice = 20.00m }
        );
    }
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public decimal MemberPrice { get; set; }
}

public class CartItem
{
    public int Id { get; set; }
    public string CardId { get; set; } = "";
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public int Quantity { get; set; } = 1;
    public bool IsMember { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
