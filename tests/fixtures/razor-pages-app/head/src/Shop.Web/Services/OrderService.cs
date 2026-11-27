using Shop.Web.Data.Entities;
using Shop.Web.Data.Repositories;
using Shop.Web.Models.Orders;

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

    public Task<Order?> GetOrderAsync(int id, CancellationToken cancellationToken = default)
    {
        return _orders.GetByIdAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<Order>> ListAsync(OrderStatus? status, CancellationToken cancellationToken = default)
    {
        return await _orders.ListAsync(status, cancellationToken);
    }

    public async Task SaveAsync(Order order, CancellationToken cancellationToken = default)
    {
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

    public async Task<int> BulkUpdateStatusAsync(
        IReadOnlyCollection<int> orderIds,
        OrderStatus newStatus,
        decimal? discountPercent,
        CancellationToken cancellationToken = default)
    {
        if (orderIds.Count == 0)
        {
            return 0;
        }

        var orders = await _orders.GetByIdsAsync(orderIds, cancellationToken);
        foreach (var order in orders)
        {
            if (order.Status == OrderStatus.Cancelled && newStatus != OrderStatus.Cancelled)
            {
                _logger.LogWarning("Skipping cancelled order {OrderNumber} in bulk update", order.Number);
                continue;
            }

            order.Status = newStatus;

            if (discountPercent is > 0)
            {
                _pricing.ApplyBulkDiscount(order, discountPercent.Value);
            }

            order.Total = _pricing.CalculateOrderTotal(order);
        }

        await _orders.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Bulk updated {Count} orders to {Status}", orders.Count, newStatus);
        return orders.Count;
    }

    public async Task<IReadOnlyList<OrderSummaryDto>> GetSummariesAsync(OrderStatus? status, CancellationToken cancellationToken = default)
    {
        var orders = await _orders.ListAsync(status, cancellationToken);
        return orders
            .Select(o => new OrderSummaryDto
            {
                Id = o.Id,
                Number = o.Number,
                CustomerName = o.Customer?.Name,
                PlacedOn = o.PlacedOn,
                Status = o.Status,
                Total = o.Total,
            })
            .ToList();
    }
}
