using Microsoft.EntityFrameworkCore;
using Shop.Web.Data;
using Shop.Web.Data.Entities;

namespace Shop.Web.Services;

public class CustomerService : ICustomerService
{
    private readonly ShopDbContext _db;

    public CustomerService(ShopDbContext db)
    {
        _db = db;
    }

    public Task<Customer?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        return _db.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task UpdateAsync(Customer customer, CancellationToken cancellationToken = default)
    {
        _db.Customers.Update(customer);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public string GetDisplayName(Customer? customer)
    {
        if (customer == null)
        {
            return string.Empty;
        }

        return customer.Name.Trim();
    }
}
