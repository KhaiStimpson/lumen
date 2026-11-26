using Shop.Web.Models.Invoices;

namespace Shop.Web.Services;

public interface IInvoiceExportService
{
    Task<IReadOnlyList<InvoiceExportRow>> PreviewAsync(InvoiceExportRequest request, CancellationToken cancellationToken = default);

    Task<ExportFile> ExportCsvAsync(InvoiceExportRequest request, CancellationToken cancellationToken = default);
}

public sealed record ExportFile(string FileName, string ContentType, byte[] Content);
