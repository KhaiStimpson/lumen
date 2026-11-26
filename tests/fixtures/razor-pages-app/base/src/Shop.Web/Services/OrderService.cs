using Shop.Web.Data.Entities;
using Shop.Web.Data.Repositories;

namespace Shop.Web.Services;

public class OrderService : IOrderService
{
    private readonly IOrderRepository _orders;
    private readonly PricingCalculator _pricing;
    private readonly ILogger<OrderService> _logger;

    public OrderService(IOrderRepository orders, PricingCalculator pricing, ILogger<OrderService> logger)
    {
        _orders = orders;
        _pricing = pricing;
        _logger = logger;
    }

    public Task<Order?> GetOrder(int id, CancellationToken cancellationToken = default)
    {
        return _orders.GetByIdAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<Order>> ListAsync(OrderStatus? status, CancellationToken cancellationToken = default)
    {
        return await _orders.ListAsync(status, cancellationToken);
    }

    public async Task SaveAsync(Order order, CancellationToken cancellationToken = default)
    {
        if (order is null)
        {
            throw new ArgumentNullException(nameof(order));
        }

        order.Total = _pricing.CalculateOrderTotal(order);
        await _orders.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Saved order {OrderNumber} with total {Total}", order.Number, order.Total);
    }

    public async Task<Order> PlaceFromCartAsync(int customerId, Cart cart, CancellationToken cancellationToken = default)
    {
        var order = new Order
        {
            CustomerId = customerId,
            Number = $"SO-{DateTime.UtcNow:yyMMddHHmmss}",
            PlacedOn = DateTime.UtcNow,
            Status = OrderStatus.Placed,
        };

        foreach (var item in cart.Items)
        {
            order.Lines.Add(new OrderLine
            {
                ProductId = item.ProductId,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
            });
        }

        order.Total = _pricing.CalculateOrderTotal(order);
        await _orders.AddAsync(order, cancellationToken);
        await _orders.SaveChangesAsync(cancellationToken);
        return order;
    }
}
