using Microsoft.EntityFrameworkCore;
using Shop.Web.Data.Entities;

namespace Shop.Web.Data.Repositories;

public class OrderRepository : IOrderRepository
{
    private readonly ShopDbContext _db;

    public OrderRepository(ShopDbContext db)
    {
        _db = db;
    }

    public Task<Order?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return _db.Orders
            .Include(o => o.Customer)
            .Include(o => o.Lines)
                .ThenInclude(l => l.Product)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
    }

    public Task<List<Order>> ListAsync(OrderStatus? status, CancellationToken cancellationToken = default)
    {
        var query = _db.Orders.Include(o => o.Customer).AsNoTracking();

        if (status is not null)
        {
            query = query.Where(o => o.Status == status);
        }

        return query
            .OrderByDescending(o => o.PlacedOn)
            .Take(200)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Order order, CancellationToken cancellationToken = default)
    {
        await _db.Orders.AddAsync(order, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return _db.SaveChangesAsync(cancellationToken);
    }
}
