using Microsoft.Extensions.Configuration;
using Shop.Web.Data.Entities;
using Shop.Web.Services;

namespace Shop.Web.Tests.Services;

public class BankTransferPaymentGatewayTests
{
    private static BankTransferPaymentGateway Create(string? account)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Payments:BankTransferAccount"] = account })
            .Build();
        return new BankTransferPaymentGateway(configuration);
    }

    [Fact]
    public async Task Fails_when_no_account_configured()
    {
        var result = await Create(null).ChargeAsync(new Order { Number = "SO-1" });

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Succeeds_with_reference_when_account_configured()
    {
        var result = await Create("GB00TEST").ChargeAsync(new Order { Number = "SO-1" });

        Assert.True(result.Succeeded);
        Assert.StartsWith("BT-", result.Reference);
    }
}
