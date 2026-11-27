using Shop.Web.Data.Entities;

namespace Shop.Web.Services;

public class PricingCalculator
{
    public const decimal FreeShippingThreshold = 500m;

    public decimal CalculateLineTotal(OrderLine line)
    {
        var gross = line.UnitPrice * line.Quantity;
        return Math.Round(gross - line.Discount, 2, MidpointRounding.AwayFromZero);
    }

    public decimal CalculateOrderTotal(Order order)
    {
        var lines = order.Lines.Sum(CalculateLineTotal);
        var shipping = lines >= FreeShippingThreshold ? 0m : order.ShippingCost;
        return lines + shipping;
    }
}
