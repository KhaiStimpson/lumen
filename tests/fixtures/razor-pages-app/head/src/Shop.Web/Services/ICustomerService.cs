using Shop.Web.Data.Entities;

namespace Shop.Web.Services;

/// <summary>
/// Reads and updates trade customers.
/// </summary>
public interface ICustomerService
{
    /// <summary>
    /// Loads a customer by id, or <c>null</c> if it does not exist or was soft-deleted.
    /// </summary>
    Task<Customer?> GetAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists changes made to a tracked customer.
    /// </summary>
    Task UpdateAsync(Customer customer, CancellationToken cancellationToken = default);

    // Display names are shown in page titles and the notes panel.
    string GetDisplayName(Customer? customer);
}
