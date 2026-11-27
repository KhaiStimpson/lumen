using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shop.Web.Infrastructure;
using Shop.Web.Models.Invoices;
using Shop.Web.Services;

namespace Shop.Web.Pages.Invoices;

[EnableCors("InvoiceExport")]
public class ExportModel : PageModel
{
    private readonly IInvoiceExportService _exportService;
    private readonly ILogger<ExportModel> _logger;

    public ExportModel(IInvoiceExportService exportService, ILogger<ExportModel> logger)
    {
        _exportService = exportService;
        _logger = logger;
    }

    [BindProperty]
    public InvoiceExportRequest Input { get; set; } = new();

    public void OnGet()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Input = new InvoiceExportRequest
        {
            From = new DateOnly(today.Year, today.Month, 1),
            To = today,
            IncludeHeader = true,
        };
    }

    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return AjaxResults.Invalid(ModelState);
        }

        if (Input.To < Input.From)
        {
            return AjaxResults.Fail("The end date must be on or after the start date.");
        }

        var rows = await _exportService.PreviewAsync(Input, cancellationToken);
        return AjaxResults.Ok(rows);
    }

    public async Task<IActionResult> OnPostDownloadAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var file = await _exportService.ExportCsvAsync(Input, cancellationToken);
            return File(file.Content, file.ContentType, file.FileName);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Invoice export failed for {From}..{To}", Input.From, Input.To);
            ModelState.AddModelError(string.Empty, "The export could not be written. Try a shorter date range.");
            return Page();
        }
    }
}
