using Microsoft.AspNetCore.Mvc.RazorPages;
using Shop.Web.Data.Entities;
using Shop.Web.Services;

namespace Shop.Web.Pages;

public class IndexModel : PageModel
{
    private readonly IOrderService _orderService;

    public IndexModel(IOrderService orderService)
    {
        _orderService = orderService;
    }

    public int OpenOrderCount { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var placed = await _orderService.ListAsync(OrderStatus.Placed, cancellationToken);
        OpenOrderCount = placed.Count;
    }
}
