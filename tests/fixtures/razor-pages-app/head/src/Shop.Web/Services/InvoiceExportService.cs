using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Shop.Web.Data.Entities;
using Shop.Web.Models.Invoices;

namespace Shop.Web.Services;

public class InvoiceExportService : IInvoiceExportService
{
    private const int DefaultMaxRows = 5000;

    private readonly InvoiceService _invoices;
    private readonly ExportFileNamer _fileNamer;
    private readonly ILogger<InvoiceExportService> _logger;
    private readonly int _maxRows;

    public InvoiceExportService(
        InvoiceService invoices,
        ExportFileNamer fileNamer,
        IConfiguration configuration,
        ILogger<InvoiceExportService> logger)
    {
        _invoices = invoices;
        _fileNamer = fileNamer;
        _logger = logger;
        _maxRows = configuration.GetValue("InvoiceExport:MaxRows", DefaultMaxRows);
    }

    public async Task<IReadOnlyList<InvoiceExportRow>> PreviewAsync(InvoiceExportRequest request, CancellationToken cancellationToken = default)
    {
        var rows = await LoadRowsAsync(request, cancellationToken);
        return rows.Take(25).ToList();
    }

    public async Task<ExportFile> ExportCsvAsync(InvoiceExportRequest request, CancellationToken cancellationToken = default)
    {
        var rows = await LoadRowsAsync(request, cancellationToken);

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = request.Delimiter,
            HasHeaderRecord = request.IncludeHeader,
        };

        await using var buffer = new MemoryStream();
        await using (var writer = new StreamWriter(buffer, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true))
        await using (var csv = new CsvWriter(writer, config))
        {
            await csv.WriteRecordsAsync(rows, cancellationToken);
        }

        var fileName = _fileNamer.ForInvoices(request.From, request.To, "csv");
        _logger.LogInformation("Exported {Count} invoices to {FileName}", rows.Count, fileName);
        return new ExportFile(fileName, "text/csv", buffer.ToArray());
    }

    private async Task<List<InvoiceExportRow>> LoadRowsAsync(InvoiceExportRequest request, CancellationToken cancellationToken)
    {
        var invoices = await _invoices.GetInvoicesForPeriodAsync(request.From, request.To, request.UnpaidOnly, cancellationToken);
        if (invoices.Count > _maxRows)
        {
            _logger.LogWarning("Invoice export truncated from {Count} to {Max} rows", invoices.Count, _maxRows);
        }

        return invoices.Take(_maxRows).Select(ToRow).ToList();
    }

    private static InvoiceExportRow ToRow(Invoice invoice)
    {
        var row = new InvoiceExportRow();
        row.Number = invoice.Number;
        row.OrderId = invoice.OrderId;
        row.IssuedOn = invoice.IssuedOn;
        row.DueOn = invoice.DueOn;
        row.Amount = invoice.Amount;
        row.IsPaid = invoice.IsPaid;

        row.OrderNumber = invoice.Order?.Number;
        row.CustomerName = invoice.Order?.Customer?.Name;
        row.DaysOverdue = invoice.IsPaid
            ? 0
            : Math.Max(0, DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - invoice.DueOn.DayNumber);
        return row;
    }
}
