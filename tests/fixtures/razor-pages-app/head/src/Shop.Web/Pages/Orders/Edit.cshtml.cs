using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Shop.Web.Data.Entities;
using Shop.Web.Infrastructure;
using Shop.Web.Services;

namespace Shop.Web.Pages.Orders;

public class EditModel : PageModel
{
    private readonly IOrderService _orderService;
    private readonly ILogger<EditModel> _logger;

    public EditModel(IOrderService orderService, ILogger<EditModel> logger)
    {
        _orderService = orderService;
        _logger = logger;
    }

    public Order Order { get; private set; } = new();

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var order = await _orderService.GetOrderAsync(id, cancellationToken);
        if (order is null)
        {
            return NotFound();
        }

        Order = order;
        Input = new InputModel
        {
            Status = order.Status,
            ShippingCost = order.ShippingCost,
            Notes = order.Notes,
            RowVersion = Convert.ToBase64String(order.RowVersion),
        };

        return Page();
    }

    public async Task<IActionResult> OnGetLinesAsync(int id, CancellationToken cancellationToken)
    {
        var order = await _orderService.GetOrderAsync(id, cancellationToken);
        if (order is null)
        {
            return AjaxResults.Fail("Order not found", StatusCodes.Status404NotFound);
        }

        var lines = order.Lines.Select(l => new
        {
            id = l.Id,
            product = l.Product?.Name,
            quantity = l.Quantity,
            unitPrice = l.UnitPrice,
            discount = l.Discount,
        });

        return AjaxResults.Ok(new { lines, total = order.Total });
    }

    public async Task<IActionResult> OnPostSaveAsync(int id, CancellationToken cancellationToken)
    {
        var order = await _orderService.GetOrderAsync(id, cancellationToken);
        if (order is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return AjaxResults.Invalid(ModelState);
        }

        order.Status = Input.Status;
        order.ShippingCost = Input.ShippingCost;
        order.Notes = Input.Notes;

        try
        {
            await _orderService.SaveAsync(order, cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "Order {OrderId} was changed by someone else", id);
            return AjaxResults.Fail("This order was changed by someone else. Reload and try again.", StatusCodes.Status409Conflict);
        }

        return AjaxResults.Ok(new { total = order.Total.ToString("C") }, "Order saved");
    }

    public class InputModel
    {
        [Display(Name = "Status")]
        public OrderStatus Status { get; set; }

        [Display(Name = "Shipping")]
        [Range(0, 1000)]
        public decimal ShippingCost { get; set; }

        [Display(Name = "Internal notes")]
        [MaxLength(1000)]
        public string? Notes { get; set; }

        public string? RowVersion { get; set; }
    }
}
