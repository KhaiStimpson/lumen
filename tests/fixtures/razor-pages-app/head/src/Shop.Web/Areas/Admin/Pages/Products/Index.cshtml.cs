using Microsoft.AspNetCore.Mvc.RazorPages;
using Shop.Web.Data.Entities;
using Shop.Web.Data.Repositories;

namespace Shop.Web.Areas.Admin.Pages.Products;

public class IndexModel : PageModel
{
    private readonly ProductRepository _products;

    public IndexModel(ProductRepository products)
    {
        _products = products;
    }

    public IReadOnlyList<Product> Products { get; private set; } = Array.Empty<Product>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Products = await _products.ListActiveAsync(cancellationToken);
    }
}
