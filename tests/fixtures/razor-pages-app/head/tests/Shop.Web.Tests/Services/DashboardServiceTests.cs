using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shop.Web.Data;
using Shop.Web.Data.Entities;
using Shop.Web.Services;

namespace Shop.Web.Tests.Services;

public class DashboardServiceTests
{
    private static readonly DateOnly From = new(2026, 11, 1);
    private static readonly DateOnly To = new(2026, 11, 30);

    private static ShopDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ShopDbContext(options);
    }

    private static DashboardService CreateSut(ShopDbContext db, int cacheSeconds = 0)
        => new(db, NullLogger<DashboardService>.Instance, cacheSeconds);

    [Fact]
    public async Task GetStatsAsync_sums_revenue_and_ignores_cancelled_orders()
    {
        await using var db = CreateDb();
        db.Orders.AddRange(
            new Order { Number = "SO-1", PlacedOn = new DateTime(2026, 11, 3), Status = OrderStatus.Paid, Total = 100m },
            new Order { Number = "SO-2", PlacedOn = new DateTime(2026, 11, 4), Status = OrderStatus.Placed, Total = 50m },
            new Order { Number = "SO-3", PlacedOn = new DateTime(2026, 11, 5), Status = OrderStatus.Cancelled, Total = 999m });
        await db.SaveChangesAsync();

        var stats = await CreateSut(db).GetStatsAsync(From, To);

        Assert.Equal(2, stats.OrderCount);
        Assert.Equal(150m, stats.Revenue);
        Assert.Equal(75m, stats.AverageOrderValue);
    }

    [Fact]
    public async Task GetStatsAsync_groups_orders_by_status()
    {
        await using var db = CreateDb();
        db.Orders.AddRange(
            new Order { Number = "SO-1", PlacedOn = new DateTime(2026, 11, 3), Status = OrderStatus.Paid },
            new Order { Number = "SO-2", PlacedOn = new DateTime(2026, 11, 4), Status = OrderStatus.Paid },
            new Order { Number = "SO-3", PlacedOn = new DateTime(2026, 11, 5), Status = OrderStatus.Shipped });
        await db.SaveChangesAsync();

        var stats = await CreateSut(db).GetStatsAsync(From, To);

        Assert.NotNull(stats.OrdersByStatus);
        Assert.Equal(2, stats.OrdersByStatus!["Paid"]);
        Assert.Equal(1, stats.OrdersByStatus["Shipped"]);
    }

    [Fact]
    public async Task GetStatsAsync_totals_unpaid_invoices_only()
    {
        await using var db = CreateDb();
        db.Invoices.AddRange(
            new Invoice { Number = "INV-1", OrderId = 1, Amount = 40m, IsPaid = false },
            new Invoice { Number = "INV-2", OrderId = 2, Amount = 60m, IsPaid = true });
        await db.SaveChangesAsync();

        var stats = await CreateSut(db).GetStatsAsync(From, To);

        Assert.Equal(40m, stats.UnpaidInvoiceTotal);
    }

    [Fact]
    public async Task GetStatsAsync_returns_cached_stats_within_cache_window()
    {
        await using var db = CreateDb();
        var sut = CreateSut(db, cacheSeconds: 60);
        var first = await sut.GetStatsAsync(From, To);

        db.Orders.Add(new Order { Number = "SO-9", PlacedOn = new DateTime(2026, 11, 9), Status = OrderStatus.Paid, Total = 10m });
        await db.SaveChangesAsync();
        var second = await sut.GetStatsAsync(From, To);

        Assert.Same(first, second);
    }
}
