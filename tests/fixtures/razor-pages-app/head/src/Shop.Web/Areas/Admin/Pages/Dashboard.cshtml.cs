using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shop.Web.Data.Entities;
using Shop.Web.Infrastructure;
using Shop.Web.Models.Admin;
using Shop.Web.Models.Orders;
using Shop.Web.Services;

namespace Shop.Web.Areas.Admin.Pages;

public class DashboardModel : PageModel
{
    private readonly DashboardService _dashboard;
    private readonly IOrderService _orderService;

    public DashboardModel(DashboardService dashboard, IOrderService orderService)
    {
        _dashboard = dashboard;
        _orderService = orderService;
    }

    public DashboardStats Stats { get; private set; } = new();

    public IReadOnlyList<OrderSummaryDto> RecentOrders { get; private set; } = Array.Empty<OrderSummaryDto>();

    public string DrilldownPage => "/Orders/BulkEdit";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var (from, to) = CurrentMonth();
        Stats = await _dashboard.GetStatsAsync(from, to, cancellationToken);
        RecentOrders = (await _orderService.GetSummariesAsync(OrderStatus.Placed, cancellationToken))
            .Take(10)
            .ToList();
    }

    public async Task<IActionResult> OnGetStatsAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var (defaultFrom, defaultTo) = CurrentMonth();
        var stats = await _dashboard.GetStatsAsync(from ?? defaultFrom, to ?? defaultTo, cancellationToken);
        return AjaxResults.Ok(stats);
    }

    public async Task<IActionResult> OnGetTopProductsAsync(string status, CancellationToken cancellationToken)
    {
        var products = await _dashboard.GetTopProductsAsync(status, 5, cancellationToken);
        return AjaxResults.Ok(products);
    }

    private static (DateOnly From, DateOnly To) CurrentMonth()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return (new DateOnly(today.Year, today.Month, 1), today);
    }
}
