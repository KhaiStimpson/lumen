using Shop.Web.Data.Entities;
using Shop.Web.Models.Orders;

namespace Shop.Web.Services;

public interface IOrderService
{
    Task<Order?> GetOrderAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Order>> ListAsync(OrderStatus? status, CancellationToken cancellationToken = default);

    Task SaveAsync(Order order, CancellationToken cancellationToken = default);

    Task<Order> PlaceFromCartAsync(int customerId, Cart cart, CancellationToken cancellationToken = default);

    Task<int> BulkUpdateStatusAsync(IReadOnlyCollection<int> orderIds, OrderStatus newStatus, decimal? discountPercent, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrderSummaryDto>> GetSummariesAsync(OrderStatus? status, CancellationToken cancellationToken = default);
}
