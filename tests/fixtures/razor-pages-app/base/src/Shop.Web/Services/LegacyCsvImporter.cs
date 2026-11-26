using Shop.Web.Data.Entities;
using Shop.Web.Data.Repositories;

namespace Shop.Web.Services;

// Imports the product catalogue from the old ERP export.
// Format: Sku,Name,Price,StockLevel with a header row.
public class LegacyCsvImporter
{
    private readonly ProductRepository _products;
    private readonly ILogger<LegacyCsvImporter> _logger;

    public LegacyCsvImporter(ProductRepository products, ILogger<LegacyCsvImporter> logger)
    {
        _products = products;
        _logger = logger;
    }

    public async Task<ImportResult> ImportProductsAsync(Stream csv, CancellationToken cancellationToken = default)
    {
        var result = new ImportResult();
        var products = new List<Product>();

        using var reader = new StreamReader(csv);
        var header = await reader.ReadLineAsync(cancellationToken);
        var lineNumber = 1;

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            lineNumber++;
            var parts = line.Split(',');
            if (parts.Length != 4)
            {
                result.Errors.Add("Line " + lineNumber + ": expected 4 columns");
                continue;
            }

            decimal price;
            if (!decimal.TryParse(parts[2], out price))
            {
                result.Errors.Add("Line " + lineNumber + ": bad price");
                continue;
            }

            int stock;
            if (!int.TryParse(parts[3], out stock))
            {
                result.Errors.Add("Line " + lineNumber + ": bad stock level");
                continue;
            }

            var product = new Product();
            product.Sku = parts[0].Trim();
            product.Name = parts[1].Trim();
            product.Price = price;
            product.StockLevel = stock;
            product.IsActive = true;
            products.Add(product);
        }

        await _products.AddRangeAsync(products, cancellationToken);
        result.Imported = products.Count;
        _logger.LogInformation("Imported {Count} products from legacy CSV", products.Count);
        return result;
    }
}

public class ImportResult
{
    public int Imported { get; set; }

    public List<string> Errors { get; } = new();
}
