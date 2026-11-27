using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shop.Web.Data.Entities;
using Shop.Web.Models.Orders;
using Shop.Web.Pages.Orders;
using Shop.Web.Services;

namespace Shop.Web.Tests.Pages;

public class BulkEditModelTests
{
    private readonly Mock<IOrderService> _orderService = new();

    private BulkEditModel CreatePage() => new(_orderService.Object, NullLogger<BulkEditModel>.Instance);

    [Fact]
    public async Task OnPostApplyAsync_passes_selection_to_service()
    {
        _orderService
            .Setup(s => s.BulkUpdateStatusAsync(It.IsAny<IReadOnlyCollection<int>>(), OrderStatus.Shipped, 10m, It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        var page = CreatePage();
        page.Input = new BulkEditInput
        {
            OrderIds = new List<int> { 1, 2 },
            NewStatus = OrderStatus.Shipped,
            DiscountPercent = 10m,
        };

        var result = await page.OnPostApplyAsync(CancellationToken.None);

        var json = Assert.IsType<JsonResult>(result);
        Assert.NotNull(json.Value);
        _orderService.Verify(
            s => s.BulkUpdateStatusAsync(It.Is<IReadOnlyCollection<int>>(ids => ids.Count == 2), OrderStatus.Shipped, 10m, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnPostApplyAsync_returns_422_when_model_invalid()
    {
        var page = CreatePage();
        page.ModelState.AddModelError("Input.OrderIds", "Select at least one order.");

        var result = await page.OnPostApplyAsync(CancellationToken.None);

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal(422, json.StatusCode);
    }

    [Fact]
    public async Task OnGetAsync_loads_summaries_for_status_filter()
    {
        var summaries = new List<OrderSummaryDto>
        {
            new() { Id = 1, Number = "SO-1", Status = OrderStatus.Placed, Total = 10m },
        };
        _orderService.Setup(s => s.GetSummariesAsync(OrderStatus.Placed, It.IsAny<CancellationToken>())).ReturnsAsync(summaries);
        var page = CreatePage();
        page.Status = OrderStatus.Placed;

        await page.OnGetAsync(CancellationToken.None);

        Assert.Single(page.Orders);
    }
}
