using Shop.Web.Data.Entities;

namespace Shop.Web.Services;

public interface IOrderService
{
    Task<Order?> GetOrder(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Order>> ListAsync(OrderStatus? status, CancellationToken cancellationToken = default);

    Task SaveAsync(Order order, CancellationToken cancellationToken = default);

    Task<Order> PlaceFromCartAsync(int customerId, Cart cart, CancellationToken cancellationToken = default);
}
