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

        var result = await _sut.GetOrder(7);

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

    [Fact]
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
}
