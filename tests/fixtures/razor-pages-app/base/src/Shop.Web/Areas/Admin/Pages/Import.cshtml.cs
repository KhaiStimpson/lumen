using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shop.Web.Services;

namespace Shop.Web.Areas.Admin.Pages;

public class ImportModel : PageModel
{
    private readonly LegacyCsvImporter _importer;

    public ImportModel(LegacyCsvImporter importer)
    {
        _importer = importer;
    }

    public ImportResult? Result { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(IFormFile upload, CancellationToken cancellationToken)
    {
        await using var stream = upload.OpenReadStream();
        Result = await _importer.ImportProductsAsync(stream, cancellationToken);
        return Page();
    }
}
