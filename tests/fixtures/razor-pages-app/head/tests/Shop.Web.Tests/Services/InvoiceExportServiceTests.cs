using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shop.Web.Data;
using Shop.Web.Data.Entities;
using Shop.Web.Models.Invoices;
using Shop.Web.Services;

namespace Shop.Web.Tests.Services;

public class InvoiceExportServiceTests
{
    private static ShopDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ShopDbContext(options);
    }

    private static InvoiceExportService CreateSut(ShopDbContext db, ExportFileNamer namer, int maxRows = 100)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["InvoiceExport:MaxRows"] = maxRows.ToString() })
            .Build();
        return new InvoiceExportService(new InvoiceService(db), namer, configuration, NullLogger<InvoiceExportService>.Instance);
    }

    [Fact]
    public async Task PreviewAsync_maps_invoice_fields_onto_rows()
    {
        var customer = new Customer { Id = 1, Name = "Fabrikam" };
        var order = new Order { Id = 10, Number = "SO-10", CustomerId = 1, Customer = customer };
        var invoice = new Invoice
        {
            Number = "INV-0001",
            OrderId = 10,
            Order = order,
            IssuedOn = new DateOnly(2026, 11, 2),
            DueOn = new DateOnly(2026, 12, 2),
            Amount = 120.50m,
            IsPaid = true,
        };
        var request = new InvoiceExportRequest
        {
            From = new DateOnly(2026, 11, 1),
            To = new DateOnly(2026, 11, 30),
            IncludeHeader = true,
        };

        await using var db = CreateDb();
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var rows = await CreateSut(db, new ExportFileNamer()).PreviewAsync(request);

        var row = Assert.Single(rows);
        Assert.Equal("INV-0001", row.Number);
        Assert.Equal("SO-10", row.OrderNumber);
        Assert.Equal("Fabrikam", row.CustomerName);
        Assert.Equal(120.50m, row.Amount);
        Assert.Equal(0, row.DaysOverdue);
    }

    [Fact]
    public async Task ExportCsvAsync_names_file_from_clock_and_range()
    {
        var clock = new Mock<TimeProvider>();
        clock.Setup(c => c.GetUtcNow()).Returns(new DateTimeOffset(2026, 11, 27, 9, 30, 0, TimeSpan.Zero));
        clock.Setup(c => c.LocalTimeZone).Returns(TimeZoneInfo.Utc);
        var request = new InvoiceExportRequest
        {
            From = new DateOnly(2026, 11, 1),
            To = new DateOnly(2026, 11, 30),
            IncludeHeader = true,
        };

        await using var db = CreateDb();
        var file = await CreateSut(db, new ExportFileNamer(clock.Object)).ExportCsvAsync(request);

        Assert.Equal("invoices_20261101-20261130_20261127-0930.csv", file.FileName);
        Assert.Equal("text/csv", file.ContentType);
    }

    [Fact]
    public async Task ExportCsvAsync_writes_header_and_respects_max_rows()
    {
        await using var db = CreateDb();
        for (var i = 1; i <= 5; i++)
        {
            db.Invoices.Add(new Invoice
            {
                Number = $"INV-{i:0000}",
                OrderId = i,
                IssuedOn = new DateOnly(2026, 11, i),
                DueOn = new DateOnly(2026, 12, i),
                Amount = 10m * i,
            });
        }

        await db.SaveChangesAsync();
        var request = new InvoiceExportRequest
        {
            From = new DateOnly(2026, 11, 1),
            To = new DateOnly(2026, 11, 30),
            IncludeHeader = true,
        };

        var file = await CreateSut(db, new ExportFileNamer(), maxRows: 3).ExportCsvAsync(request);

        var lines = Encoding.UTF8.GetString(file.Content).Trim('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Number,", lines[0]);
        Assert.Equal(4, lines.Length);
    }
}
