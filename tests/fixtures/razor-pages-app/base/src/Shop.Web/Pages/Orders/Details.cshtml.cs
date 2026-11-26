using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shop.Web.Data.Entities;
using Shop.Web.Services;

namespace Shop.Web.Pages.Orders;

public class DetailsModel : PageModel
{
    private readonly IOrderService _orderService;

    public DetailsModel(IOrderService orderService)
    {
        _orderService = orderService;
    }

    public Order Order { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var order = await _orderService.GetOrder(id, cancellationToken);
        if (order is null)
        {
            return NotFound();
        }

        Order = order;
        return Page();
    }
}
