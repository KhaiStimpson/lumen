using Shop.Web.Data.Entities;

namespace Shop.Web.Services;

public interface ICustomerService
{
    Task<Customer?> GetAsync(int id, CancellationToken cancellationToken = default);

    Task UpdateAsync(Customer customer, CancellationToken cancellationToken = default);

    string GetDisplayName(Customer? customer);
}
