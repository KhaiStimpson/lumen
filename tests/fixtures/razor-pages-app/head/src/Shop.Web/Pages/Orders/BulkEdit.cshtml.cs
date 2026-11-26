using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Shop.Web.Data.Entities;
using Shop.Web.Infrastructure;
using Shop.Web.Models.Orders;
using Shop.Web.Services;

namespace Shop.Web.Pages.Orders;

[IgnoreAntiforgeryToken]
public class BulkEditModel : PageModel
{
    private readonly IOrderService _orderService;
    private readonly ILogger<BulkEditModel> _logger;

    public BulkEditModel(IOrderService orderService, ILogger<BulkEditModel> logger)
    {
        _orderService = orderService;
        _logger = logger;
    }

    [BindProperty]
    public BulkEditInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public OrderStatus? Status { get; set; }

    public string ReturnPage { get; } = "/Orders/Index";

    public IReadOnlyList<OrderSummaryDto> Orders { get; private set; } = Array.Empty<OrderSummaryDto>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Orders = await _orderService.GetSummariesAsync(Status, cancellationToken);
    }

    public async Task<IActionResult> OnGetSummaryAsync(CancellationToken cancellationToken)
    {
        var orders = await _orderService.GetSummariesAsync(Status, cancellationToken);
        var byStatus = orders
            .GroupBy(o => o.Status)
            .Select(g => new { status = g.Key.ToString(), count = g.Count(), total = g.Sum(o => o.Total) });

        return AjaxResults.Ok(byStatus);
    }

    public async Task<IActionResult> OnPostApplyAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return AjaxResults.Invalid(ModelState);
        }

        var updated = 0;
        try
        {
            updated = await _orderService.BulkUpdateStatusAsync(
                Input.OrderIds!,
                Input.NewStatus!.Value,
                Input.DiscountPercent,
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
        }

        _logger.LogInformation("Bulk edit applied to {Updated} of {Requested} orders", updated, Input.OrderIds!.Count);
        return AjaxResults.Ok(new { updated }, $"{updated} orders updated");
    }
}
