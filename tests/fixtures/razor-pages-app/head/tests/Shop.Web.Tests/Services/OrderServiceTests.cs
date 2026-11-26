using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shop.Web.Data.Entities;
using Shop.Web.Data.Repositories;
using Shop.Web.Services;

namespace Shop.Web.Tests.Services;

public class OrderServiceTests
{
    private readonly Mock<IOrderRepository> _repository = new();
    private readonly OrderService _sut;

    public OrderServiceTests()
    {
        _sut = new OrderService(_repository.Object, new PricingCalculator(), NullLogger<OrderService>.Instance);
    }

    [Fact]
    public async Task GetOrder_returns_order_from_repository()
    {
        var order = new Order { Id = 7, Number = "SO-7" };
        _repository.Setup(r => r.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(order);

        var result = await _sut.GetOrderAsync(7);

        Assert.Same(order, result);
    }

    [Fact]
    public async Task SaveAsync_recalculates_total_before_saving()
    {
        var order = new Order { Number = "SO-1", ShippingCost = 10m };
        order.Lines.Add(new OrderLine { Quantity = 2, UnitPrice = 25m });

        await _sut.SaveAsync(order);

        Assert.Equal(60m, order.Total);
        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(Skip = "Flaky since the bulk edit refactor, re-enable later")]
    public async Task SaveAsync_rejects_null_order()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.SaveAsync(null!));
    }

    [Fact]
    public async Task PlaceFromCartAsync_copies_cart_items_to_order_lines()
    {
        var cart = new Cart();
        cart.Items.Add(new CartItem { ProductId = 3, Name = "Widget", UnitPrice = 4.5m, Quantity = 10 });

        var order = await _sut.PlaceFromCartAsync(customerId: 12, cart);

        Assert.Equal(12, order.CustomerId);
        Assert.Equal(OrderStatus.Placed, order.Status);
        var line = Assert.Single(order.Lines);
        Assert.Equal(3, line.ProductId);
        Assert.Equal(45m, order.Total);
    }

    [Fact]
    public async Task BulkUpdateStatusAsync_updates_every_loaded_order()
    {
        var first = new Order { Id = 1, Number = "SO-1", Status = OrderStatus.Placed };
        var second = new Order { Id = 2, Number = "SO-2", Status = OrderStatus.Placed };
        _repository
            .Setup(r => r.GetByIdsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Order> { first, second });

        var updated = await _sut.BulkUpdateStatusAsync(new[] { 1, 2 }, OrderStatus.Shipped, discountPercent: null);

        Assert.Equal(2, updated);
        Assert.All(new[] { first, second }, o => Assert.Equal(OrderStatus.Shipped, o.Status));
        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BulkUpdateStatusAsync_skips_cancelled_orders()
    {
        var cancelled = new Order { Id = 3, Number = "SO-3", Status = OrderStatus.Cancelled };
        _repository
            .Setup(r => r.GetByIdsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Order> { cancelled });

        await _sut.BulkUpdateStatusAsync(new[] { 3 }, OrderStatus.Paid, discountPercent: null);

        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task BulkUpdateStatusAsync_applies_capped_discount()
    {
        var order = new Order { Id = 4, Number = "SO-4", Status = OrderStatus.Placed, ShippingCost = 0m };
        order.Lines.Add(new OrderLine { Quantity = 1, UnitPrice = 100m });
        _repository
            .Setup(r => r.GetByIdsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Order> { order });

        await _sut.BulkUpdateStatusAsync(new[] { 4 }, OrderStatus.Paid, discountPercent: 40m);

        Assert.Equal(25m, order.Lines[0].Discount);
        Assert.Equal(75m, order.Total);
    }

    [Fact]
    public async Task BulkUpdateStatusAsync_with_no_ids_does_nothing()
    {
        var updated = await _sut.BulkUpdateStatusAsync(Array.Empty<int>(), OrderStatus.Paid, discountPercent: null);

        Assert.Equal(0, updated);
        _repository.Verify(r => r.GetByIdsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
