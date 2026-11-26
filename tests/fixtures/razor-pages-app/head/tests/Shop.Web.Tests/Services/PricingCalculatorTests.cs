using Shop.Web.Data.Entities;
using Shop.Web.Services;

namespace Shop.Web.Tests.Services;

public class PricingCalculatorTests
{
    private readonly PricingCalculator _sut = new();

    [Fact]
    public void Line_total_subtracts_discount()
    {
        var line = new OrderLine { Quantity = 3, UnitPrice = 10m, Discount = 5m };

        Assert.Equal(25m, _sut.CalculateLineTotal(line));
    }

    [Fact]
    public void Shipping_is_free_over_threshold()
    {
        var order = new Order { ShippingCost = 15m };
        order.Lines.Add(new OrderLine { Quantity = 1, UnitPrice = 600m });

        Assert.Equal(600m, _sut.CalculateOrderTotal(order));
    }

    [Fact]
    public void Shipping_is_charged_under_threshold()
    {
        var order = new Order { ShippingCost = 15m };
        order.Lines.Add(new OrderLine { Quantity = 1, UnitPrice = 100m });

        Assert.Equal(115m, _sut.CalculateOrderTotal(order));
    }
}
