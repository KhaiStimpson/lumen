using Microsoft.EntityFrameworkCore;
using Shop.Web.Data;
using Shop.Web.Data.Entities;

namespace Shop.Web.Services;

public class InvoiceService
{
    private const int PaymentTermsDays = 30;

    private readonly ShopDbContext _db;

    public InvoiceService(ShopDbContext db)
    {
        _db = db;
    }

    public Task<List<Invoice>> ListRecentAsync(int take, CancellationToken cancellationToken = default)
    {
        return _db.Invoices
            .Include(i => i.Order)
            .OrderByDescending(i => i.IssuedOn)
            .Take(take)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<Invoice> CreateForOrderAsync(Order order, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var invoice = new Invoice
        {
            Number = $"INV-{order.Number[3..]}",
            OrderId = order.Id,
            IssuedOn = today,
            DueOn = today.AddDays(PaymentTermsDays),
            Amount = order.Total,
        };

        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync(cancellationToken);
        return invoice;
    }
}
