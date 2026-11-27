using Microsoft.AspNetCore.Mvc.RazorPages;
using Shop.Web.Data.Entities;
using Shop.Web.Services;

namespace Shop.Web.Pages.Invoices;

public class IndexModel : PageModel
{
    private readonly InvoiceService _invoiceService;

    public IndexModel(InvoiceService invoiceService)
    {
        _invoiceService = invoiceService;
    }

    public IReadOnlyList<Invoice> Invoices { get; private set; } = Array.Empty<Invoice>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Invoices = await _invoiceService.ListRecentAsync(100, cancellationToken);
    }
}
