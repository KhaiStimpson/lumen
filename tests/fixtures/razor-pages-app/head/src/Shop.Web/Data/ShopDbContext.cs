using Microsoft.EntityFrameworkCore;
using Shop.Web.Data.Entities;

namespace Shop.Web.Data;

public class ShopDbContext : DbContext
{
    public ShopDbContext(DbContextOptions<ShopDbContext> options)
        : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<CustomerNote> CustomerNotes => Set<CustomerNote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(entity =>
        {
            entity.HasQueryFilter(c => !c.IsDeleted);
            entity.HasIndex(c => c.Email).IsUnique();
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasIndex(o => o.Number).IsUnique();
            entity.Property(o => o.Total).HasPrecision(18, 2);
            entity.Property(o => o.ShippingCost).HasPrecision(18, 2);
            entity.HasMany(o => o.Lines)
                .WithOne(l => l.Order!)
                .HasForeignKey(l => l.OrderId);
        });

        modelBuilder.Entity<OrderLine>(entity =>
        {
            entity.Property(l => l.UnitPrice).HasPrecision(18, 2);
            entity.Property(l => l.Discount).HasPrecision(18, 2);
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasIndex(p => p.Sku).IsUnique();
            entity.Property(p => p.Price).HasPrecision(18, 2);
        });

        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.HasIndex(i => i.Number).IsUnique();
            entity.Property(i => i.Amount).HasPrecision(18, 2);
        });

        modelBuilder.Entity<CustomerNote>(entity =>
        {
            entity.HasQueryFilter(n => !n.IsDeleted);
            entity.HasIndex(n => new { n.CustomerId, n.CreatedOn });
            entity.HasOne<Customer>()
                .WithMany()
                .HasForeignKey(n => n.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
