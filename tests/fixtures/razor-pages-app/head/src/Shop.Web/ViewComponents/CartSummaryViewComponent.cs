using Microsoft.AspNetCore.Mvc;
using Shop.Web.Services;

namespace Shop.Web.ViewComponents;

public class CartSummaryViewComponent : ViewComponent
{
    private readonly CartService _cart;

    public CartSummaryViewComponent(CartService cart)
    {
        _cart = cart;
    }

    public IViewComponentResult Invoke()
    {
        return View(_cart.GetCart());
    }
}
