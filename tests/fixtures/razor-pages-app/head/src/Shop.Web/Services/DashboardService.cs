using Microsoft.EntityFrameworkCore;
using Shop.Web.Data;
using Shop.Web.Data.Entities;
using Shop.Web.Models.Admin;

namespace Shop.Web.Services;

public class DashboardService
{
    private readonly ShopDbContext _db;
    private readonly ILogger<DashboardService> _logger;
    private readonly int _cacheSeconds;

    private DashboardStats? _cached;
    private DateTime _cachedAt;

    public DashboardService(ShopDbContext db, ILogger<DashboardService> logger, int cacheSeconds)
    {
        _db = db;
        _logger = logger;
        _cacheSeconds = cacheSeconds;
    }

    public async Task<DashboardStats> GetStatsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (_cached is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(_cacheSeconds))
        {
            return _cached;
        }

        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.ToDateTime(TimeOnly.MaxValue);

        var orders = await _db.Orders
            .Where(o => o.PlacedOn >= start && o.PlacedOn <= end && o.Status != OrderStatus.Cancelled)
            .Select(o => new { o.Total, o.Status })
            .ToListAsync(cancellationToken);

        var customerCount = await _db.Customers
            .IgnoreQueryFilters()
            .CountAsync(cancellationToken);

        var unpaid = await _db.Invoices
            .Where(i => !i.IsPaid)
            .SumAsync(i => i.Amount, cancellationToken);

        var stats = new DashboardStats
        {
            From = from,
            To = to,
            OrderCount = orders.Count,
            Revenue = orders.Sum(o => o.Total),
            CustomerCount = customerCount,
            UnpaidInvoiceTotal = unpaid,
            OrdersByStatus = orders
                .GroupBy(o => o.Status)
                .ToDictionary(g => g.Key.ToString(), g => g.Count()),
        };

        _cached = stats;
        _cachedAt = DateTime.UtcNow;
        _logger.LogDebug("Dashboard stats recalculated for {From}..{To}", from, to);
        return stats;
    }

    public async Task<IReadOnlyList<TopProduct>> GetTopProductsAsync(string status, int take, CancellationToken cancellationToken = default)
    {
#pragma warning disable CS8602
        var rows = await _db.OrderLines
            .FromSqlRaw($"SELECT l.* FROM OrderLines l JOIN Orders o ON o.Id = l.OrderId WHERE o.Status = '{status}'")
            .Include(l => l.Product)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(l => new { l.ProductId, l.Product.Name })
            .Select(g => new TopProduct(g.Key.Name, g.Sum(l => l.Quantity), g.Sum(l => l.UnitPrice * l.Quantity)))
            .OrderByDescending(p => p.Quantity)
            .Take(take)
            .ToList();
#pragma warning restore CS8602
    }
}

public sealed record TopProduct(string Name, int Quantity, decimal Revenue);
