using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shop.Web.Data.Repositories;

namespace Shop.Web.Areas.Admin.Pages.Products;

public class EditModel : PageModel
{
    private readonly ProductRepository _products;

    public EditModel(ProductRepository products)
    {
        _products = products;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var product = await _products.GetAsync(id, cancellationToken);
        if (product is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            Id = product.Id,
            Sku = product.Sku,
            Name = product.Name,
            Price = product.Price,
            StockLevel = product.StockLevel,
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var product = await _products.GetAsync(id, cancellationToken);
        if (product is null)
        {
            return NotFound();
        }

        product.Name = Input.Name;
        product.Price = Input.Price;
        product.StockLevel = Input.StockLevel;

        await _products.UpdateAsync(product, cancellationToken);
        return RedirectToPage("./Index");
    }

    public async Task<IActionResult> OnPostArchiveAsync(int id, CancellationToken cancellationToken)
    {
        var archived = await _products.ArchiveAsync(id, cancellationToken);
        if (!archived)
        {
            return NotFound();
        }

        TempData["Message"] = "Product archived.";
        return RedirectToPage("./Index");
    }

    public class InputModel
    {
        public int Id { get; set; }

        public string Sku { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [Range(0.01, 100000)]
        public decimal Price { get; set; }

        [Range(0, int.MaxValue)]
        [Display(Name = "Stock level")]
        public int StockLevel { get; set; }
    }
}
