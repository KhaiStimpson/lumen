using System.Text.Json;

namespace Shop.Web.Services;

public class CartService
{
    private const string SessionKey = "cart";
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CartService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ISession Session => _httpContextAccessor.HttpContext!.Session;

    public Cart GetCart()
    {
        var json = Session.GetString(SessionKey);
        if (string.IsNullOrEmpty(json))
        {
            return new Cart();
        }
        return JsonSerializer.Deserialize<Cart>(json) ?? new Cart();
    }

    public void AddItem(int productId, string name, decimal unitPrice, int quantity)
    {
        var cart = GetCart();
        var existing = cart.Items.FirstOrDefault(i => i.ProductId == productId);
        if (existing is not null)
        {
            existing.Quantity += quantity;
        }
        else
        {
            cart.Items.Add(new CartItem
            {
                ProductId = productId,
                Name = name,
                UnitPrice = unitPrice,
                Quantity = quantity
            });
        }
        Save(cart);
    }

    public void Clear()
    {
        Session.Remove(SessionKey);
    }

    private void Save(Cart cart)
    {
        Session.SetString(SessionKey, JsonSerializer.Serialize(cart));
    }
}

public class Cart
{
    public List<CartItem> Items { get; set; } = new();

    public decimal Subtotal => Items.Sum(i => i.UnitPrice * i.Quantity);

    public int Count => Items.Sum(i => i.Quantity);
}

public class CartItem
{
    public int ProductId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
}
