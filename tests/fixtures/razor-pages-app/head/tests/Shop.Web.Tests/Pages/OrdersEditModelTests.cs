using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shop.Web.Data.Entities;
using Shop.Web.Pages.Orders;
using Shop.Web.Services;

namespace Shop.Web.Tests.Pages;

public class OrdersEditModelTests
{
    private readonly Mock<IOrderService> _orderService = new();

    private EditModel CreatePage() => new(_orderService.Object, NullLogger<EditModel>.Instance);

    [Fact]
    public async Task OnGetAsync_returns_not_found_for_unknown_order()
    {
        _orderService.Setup(s => s.GetOrderAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((Order?)null);

        var result = await CreatePage().OnGetAsync(99, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task OnGetAsync_fills_input_from_order()
    {
        var order = new Order { Id = 5, Number = "SO-5", ShippingCost = 7.5m, Status = OrderStatus.Paid };
        _orderService.Setup(s => s.GetOrderAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(order);
        var page = CreatePage();

        var result = await page.OnGetAsync(5, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(7.5m, page.Input.ShippingCost);
        Assert.Equal(OrderStatus.Paid, page.Input.Status);
    }

    [Fact]
    public async Task OnPostSaveAsync_saves_input_onto_order()
    {
        var order = new Order { Id = 5, Number = "SO-5" };
        _orderService.Setup(s => s.GetOrderAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(order);
        var page = CreatePage();
        page.Input = new EditModel.InputModel { Status = OrderStatus.Shipped, ShippingCost = 12m };

        var result = await page.OnPostSaveAsync(5, CancellationToken.None);

        Assert.IsType<JsonResult>(result);
        Assert.Equal(OrderStatus.Shipped, order.Status);
        _orderService.Verify(s => s.SaveAsync(order, It.IsAny<CancellationToken>()), Times.Once);
    }
}
