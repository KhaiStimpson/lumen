using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shop.Web.Services;

namespace Shop.Web.Pages.Orders;

public class CheckoutModel : PageModel
{
    private readonly CartService _cartService;
    private readonly IOrderService _orderService;
    private readonly IPaymentGateway _paymentGateway;

    public CheckoutModel(CartService cartService, IOrderService orderService, IPaymentGateway paymentGateway)
    {
        _cartService = cartService;
        _orderService = orderService;
        _paymentGateway = paymentGateway;
    }

    public Cart Cart { get; private set; } = new();

    public string PaymentMethodName => _paymentGateway.Name;

    [BindProperty]
    public int CustomerId { get; set; }

    public string? Error { get; private set; }

    public void OnGet()
    {
        Cart = _cartService.GetCart();
    }

    public async Task<IActionResult> OnPostPlaceAsync(CancellationToken cancellationToken)
    {
        Cart = _cartService.GetCart();
        if (Cart.Items.Count == 0)
        {
            return RedirectToPage();
        }

        var order = await _orderService.PlaceFromCartAsync(CustomerId, Cart, cancellationToken);
        var payment = await _paymentGateway.ChargeAsync(order, cancellationToken);
        if (!payment.Succeeded)
        {
            Error = payment.Error;
            return Page();
        }

        _cartService.Clear();
        return RedirectToPage("./Details", new { id = order.Id });
    }
}
