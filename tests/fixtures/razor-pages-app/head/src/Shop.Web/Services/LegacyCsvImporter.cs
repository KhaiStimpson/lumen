using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Shop.Web.Data.Entities;
using Shop.Web.Data.Repositories;

namespace Shop.Web.Services;

/// <summary>
/// Imports the product catalogue from the old ERP export (<c>Sku,Name,Price,StockLevel</c> with a header row).
/// Rows are validated one at a time; a bad row is reported and skipped rather than failing the whole file.
/// </summary>
public sealed class LegacyCsvImporter
{
    private const int BatchSize = 500;

    private static readonly CsvConfiguration CsvConfig = new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        TrimOptions = TrimOptions.Trim,
        MissingFieldFound = null,
        BadDataFound = null,
    };

    private readonly ProductRepository _products;
    private readonly ILogger<LegacyCsvImporter> _logger;

    public LegacyCsvImporter(ProductRepository products, ILogger<LegacyCsvImporter> logger)
    {
        _products = products;
        _logger = logger;
    }

    public async Task<ImportResult> ImportProductsAsync(Stream csv, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(csv);

        var result = new ImportResult();
        var batch = new List<Product>(BatchSize);
        var seenSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StreamReader(csv);
        using var parser = new CsvReader(reader, CsvConfig);

        await parser.ReadAsync();
        parser.ReadHeader();

        while (await parser.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowNumber = parser.Parser.Row;

            var parsed = ParseRow(parser, rowNumber);
            if (parsed.Error is not null)
            {
                result.Errors.Add(parsed.Error);
                continue;
            }

            var product = parsed.Product!;
            if (!seenSkus.Add(product.Sku))
            {
                result.Errors.Add($"Row {rowNumber}: duplicate SKU {product.Sku}");
                continue;
            }

            batch.Add(product);
            if (batch.Count == BatchSize)
            {
                result.Imported += await FlushAsync(batch, cancellationToken);
            }
        }

        result.Imported += await FlushAsync(batch, cancellationToken);
        _logger.LogInformation(
            "Imported {Count} products from legacy CSV with {ErrorCount} rejected rows",
            result.Imported,
            result.Errors.Count);
        return result;
    }

    private static ParsedRow ParseRow(CsvReader parser, int rowNumber)
    {
        var sku = parser.GetField(0);
        var name = parser.GetField(1);

        if (string.IsNullOrWhiteSpace(sku) || string.IsNullOrWhiteSpace(name))
        {
            return ParsedRow.Fail($"Row {rowNumber}: SKU and name are required");
        }

        if (!decimal.TryParse(parser.GetField(2), NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || price <= 0)
        {
            return ParsedRow.Fail($"Row {rowNumber}: price must be a positive number");
        }

        if (!int.TryParse(parser.GetField(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stock) || stock < 0)
        {
            return ParsedRow.Fail($"Row {rowNumber}: stock level must be zero or more");
        }

        return ParsedRow.Ok(new Product
        {
            Sku = sku.ToUpperInvariant(),
            Name = name,
            Price = price,
            StockLevel = stock,
            IsActive = true,
        });
    }

    private async Task<int> FlushAsync(List<Product> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return 0;
        }

        try
        {
            await _products.AddRangeAsync(batch, cancellationToken);
            return batch.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save a batch of {Count} imported products", batch.Count);
            throw;
        }
        finally
        {
            batch.Clear();
        }
    }

    private readonly record struct ParsedRow(Product? Product, string? Error)
    {
        public static ParsedRow Ok(Product product) => new(product, null);

        public static ParsedRow Fail(string error) => new(null, error);
    }
}

public class ImportResult
{
    public int Imported { get; set; }

    public List<string> Errors { get; } = new();
}
