using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shop.Web.Data.Entities;
using Shop.Web.Services;

namespace Shop.Web.Pages.Orders;

public class IndexModel : PageModel
{
    private readonly IOrderService _orderService;

    public IndexModel(IOrderService orderService)
    {
        _orderService = orderService;
    }

    [BindProperty(SupportsGet = true)]
    public OrderStatus? Status { get; set; }

    public IReadOnlyList<Order> Orders { get; private set; } = Array.Empty<Order>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Orders = await _orderService.ListAsync(Status, cancellationToken);
    }
}
