using Microsoft.EntityFrameworkCore;
using Shop.Web.Data.Entities;

namespace Shop.Web.Data.Repositories;

public class ProductRepository
{
    private readonly ShopDbContext _db;

    public ProductRepository(ShopDbContext db)
    {
        _db = db;
    }

    public Task<Product?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        return _db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public Task<List<Product>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        return _db.Products
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateAsync(Product product, CancellationToken cancellationToken = default)
    {
        _db.Products.Update(product);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRangeAsync(IEnumerable<Product> products, CancellationToken cancellationToken = default)
    {
        await _db.Products.AddRangeAsync(products, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
